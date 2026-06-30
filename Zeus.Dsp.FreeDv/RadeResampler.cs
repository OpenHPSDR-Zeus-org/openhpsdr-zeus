// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Wideband output resampler for RADE V1. RADE's FARGAN vocoder synthesizes
// 16 kHz speech with energy out to ~8 kHz — far wider than the ~2.4 kHz FreeDV
// codec2 voice band. The classic FreeDvResampler.Interpolator is a 6:1 stage
// with a 3.4 kHz cutoff prototype; reusing it for RADE would dull the audio by
// rolling off everything above 3.4 kHz. This is a dedicated 3:1 polyphase
// interpolator (16 kHz -> 48 kHz) with a WIDEBAND prototype low-pass (cutoff
// ~7.2 kHz at Fs=48 kHz, below the 8 kHz output Nyquist) so the full FARGAN
// bandwidth survives the rate change. The polyphase structure, energy
// compensation (x Factor per phase), streaming history preservation, and
// zero-alloc / lock-free span -> span discipline mirror the classic
// FreeDvResampler.Interpolator exactly — only the ratio (3 vs 6) and the
// prototype cutoff (wideband vs voice) differ. The classic 6:1 path is left
// untouched.

namespace Zeus.Dsp.FreeDv;

/// <summary>
/// Wideband 16 kHz -&gt; 48 kHz (3:1) polyphase interpolator for RADE speech
/// output. Streaming, history-preserving, zero-allocation.
/// </summary>
internal static class RadeResampler
{
    internal const int Factor = 3;            // 48000 / 16000
    internal const int FsHigh = 48000;
    internal const int FsLow = 16000;
    // 32 taps/phase -> 96-tap prototype (divisible by 3). The 60-tap version had a
    // ~2.6 kHz Hamming transition band: it was only flat to ~5.5 kHz (rolling off
    // FARGAN's brilliance band → dull/"nasal" decoded speech on RX) AND its skirt
    // spilled past the 8 kHz Nyquist, aliasing on the 48→16 kHz mic decimation.
    // 96 taps tightens the transition to ~1.6 kHz: passband flat to ~6.5 kHz with
    // the stopband reached below 8 kHz, so more presence survives and the mic
    // anti-alias is clean. Cost is ~36 extra MACs per output sample — trivial.
    private const int TapsPerPhase = 32;      // -> 96-tap prototype (divisible by 3)
    private const double CutoffHz = 7200.0;   // wideband: below 8 kHz output Nyquist, keep FARGAN HF

    // Prototype low-pass, normalized so DC gain == 1.
    private static readonly float[] Prototype = DesignLowpass(TapsPerPhase * Factor, CutoffHz, FsHigh);

    private static float[] DesignLowpass(int numTaps, double cutoffHz, double fsHz)
    {
        var h = new double[numTaps];
        double fc = cutoffHz / fsHz;          // normalized cutoff (cycles/sample)
        double mid = (numTaps - 1) / 2.0;
        double sum = 0.0;
        for (int i = 0; i < numTaps; i++)
        {
            double n = i - mid;
            double sinc = (Math.Abs(n) < 1e-9) ? 2.0 * fc : Math.Sin(2.0 * Math.PI * fc * n) / (Math.PI * n);
            double w = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (numTaps - 1)); // Hamming
            double v = sinc * w;
            h[i] = v;
            sum += v;
        }
        var taps = new float[numTaps];
        for (int i = 0; i < numTaps; i++) taps[i] = (float)(h[i] / sum); // DC gain 1
        return taps;
    }

    /// <summary>Exact interpolator output for a given input length.</summary>
    internal static int InterpolatedLength(int inputLen) => inputLen * Factor;

    /// <summary>Upper bound on decimator output for a 48 kHz input block.</summary>
    internal static int MaxDecimatedLength(int inputLen) => inputLen / Factor + 1;

    internal static Interpolator NewInterpolator() => new(Prototype);

    internal static Decimator NewDecimator() => new(Prototype);

    /// <summary>
    /// Wideband 48 kHz -&gt; 16 kHz (3:1) FIR decimator for the RADE TX speech path
    /// — the inverse of <see cref="Interpolator"/>. Shares the same wideband
    /// prototype low-pass (~7.2 kHz cutoff, below the 16 kHz-output 8 kHz Nyquist)
    /// so the LPCNet analyzer sees the full speech band, not a 3.4 kHz-rolled-off
    /// version. Streaming, history-preserving, zero-allocation.
    /// </summary>
    internal sealed class Decimator
    {
        private readonly float[] _taps;
        private readonly float[] _delay; // circular input history, length = taps
        private int _head;
        private int _phase;              // counts input samples mod Factor

        internal Decimator(float[] prototype)
        {
            _taps = prototype;
            _delay = new float[prototype.Length];
        }

        internal void Reset()
        {
            Array.Clear(_delay);
            _head = 0;
            _phase = 0;
        }

        /// <summary>Consumes all of <paramref name="input"/> (48 kHz); writes one
        /// 16 kHz sample per Factor inputs to <paramref name="output"/> (size at
        /// least MaxDecimatedLength). Returns the number of samples written.</summary>
        internal int Process(ReadOnlySpan<float> input, Span<float> output)
        {
            int n = _taps.Length;
            int outCount = 0;
            foreach (float x in input)
            {
                _head = (_head + 1) % n;
                _delay[_head] = x;
                if (++_phase == Factor)
                {
                    _phase = 0;
                    float acc = 0f;
                    int idx = _head;
                    for (int k = 0; k < n; k++)
                    {
                        acc += _taps[k] * _delay[idx];
                        idx = (idx == 0) ? n - 1 : idx - 1;
                    }
                    output[outCount++] = acc;
                }
            }
            return outCount;
        }
    }

    /// <summary>16 kHz -&gt; 48 kHz. Polyphase: each input sample yields Factor output samples.</summary>
    internal sealed class Interpolator
    {
        private readonly float[][] _phases; // [Factor][tapsPerPhase]
        private readonly float[] _delay;    // input history, length = tapsPerPhase
        private int _head;

        internal Interpolator(float[] prototype)
        {
            int perPhase = prototype.Length / Factor;
            _phases = new float[Factor][];
            for (int p = 0; p < Factor; p++)
            {
                var sub = new float[perPhase];
                for (int k = 0; k < perPhase; k++)
                    sub[k] = prototype[k * Factor + p] * Factor; // x Factor: zero-stuff energy comp
                _phases[p] = sub;
            }
            _delay = new float[perPhase];
        }

        internal void Reset()
        {
            Array.Clear(_delay);
            _head = 0;
        }

        /// <summary>Consumes all of <paramref name="input"/>; writes produced 48 kHz
        /// samples to <paramref name="output"/> (size at least InterpolatedLength).
        /// Returns the number of samples written. No allocation.</summary>
        internal int Process(ReadOnlySpan<float> input, Span<float> output)
        {
            int n = _delay.Length;
            int outCount = 0;
            foreach (float x in input)
            {
                _head = (_head + 1) % n;
                _delay[_head] = x;
                for (int p = 0; p < Factor; p++)
                {
                    float[] sub = _phases[p];
                    float acc = 0f;
                    int idx = _head;
                    for (int k = 0; k < n; k++)
                    {
                        acc += sub[k] * _delay[idx];
                        idx = (idx == 0) ? n - 1 : idx - 1;
                    }
                    output[outCount++] = acc;
                }
            }
            return outCount;
        }
    }
}
