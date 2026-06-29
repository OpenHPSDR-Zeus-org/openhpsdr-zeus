// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8Resampler — integer decimation from the Zeus RX audio rate (48 kHz) to the
// FT8 decoder rate (12 kHz), i.e. ÷4. A windowed-sinc FIR low-pass (cutoff just
// under the 6 kHz decimated Nyquist) runs before downsampling to reject
// aliasing. Stateful across blocks (keeps the filter history) so a continuous
// audio stream decimates seamlessly; one instance per RX slice.

namespace Zeus.Dsp.Ft8;

public sealed class Ft8Resampler
{
    public const int InputRate = 48_000;
    public const int OutputRate = 12_000;
    public const int Factor = InputRate / OutputRate; // 4

    private readonly float[] _taps;
    private readonly float[] _history; // last (taps-1) input samples
    private int _phase;                // input-sample counter mod Factor

    public Ft8Resampler(int numTaps = 49)
    {
        if (numTaps < Factor) numTaps = Factor;
        if ((numTaps & 1) == 0) numTaps++; // odd → symmetric, integer group delay
        _taps = BuildLowPass(numTaps, OutputRate / 2.0 - 500.0, InputRate);
        _history = new float[numTaps - 1];
        _phase = 0;
    }

    /// <summary>
    /// Decimate a block of 48 kHz mono samples to 12 kHz. Returns a new array of
    /// floor-ish length (input.Length / 4, phase-dependent). Stateful: call
    /// repeatedly with consecutive blocks of the same stream.
    /// </summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        int n = input.Length;
        if (n == 0) return [];

        int taps = _taps.Length;
        // Working buffer = history + input, so the FIR can reach back across the
        // block boundary.
        var buf = new float[(taps - 1) + n];
        _history.AsSpan().CopyTo(buf);
        input.CopyTo(buf.AsSpan(taps - 1));

        // Estimate output count and emit one sample every `Factor` inputs,
        // aligned to a running phase so block boundaries don't drop/duplicate.
        var outBuf = new float[(n / Factor) + 2];
        int outCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (_phase == 0)
            {
                // FIR centered at input index i (buf index i + taps-1 is the
                // newest sample; convolve over the taps preceding it).
                float acc = 0f;
                int baseIdx = i; // buf[baseIdx .. baseIdx+taps-1] are the taps newest-last
                for (int k = 0; k < taps; k++)
                    acc += _taps[k] * buf[baseIdx + k];
                if (outCount < outBuf.Length) outBuf[outCount++] = acc;
            }
            _phase = (_phase + 1) % Factor;
        }

        // Save the trailing (taps-1) samples as history for the next block.
        buf.AsSpan(buf.Length - (taps - 1)).CopyTo(_history);

        return outCount == outBuf.Length ? outBuf : outBuf[..outCount];
    }

    /// <summary>Reset filter history and phase (e.g. on band/mode change).</summary>
    public void Reset()
    {
        Array.Clear(_history);
        _phase = 0;
    }

    // Windowed-sinc (Hamming) low-pass FIR, normalized to unity DC gain.
    private static float[] BuildLowPass(int numTaps, double cutoffHz, double sampleRate)
    {
        var h = new float[numTaps];
        double fc = cutoffHz / sampleRate; // normalized cutoff (cycles/sample)
        int mid = (numTaps - 1) / 2;
        double sum = 0;
        for (int i = 0; i < numTaps; i++)
        {
            int m = i - mid;
            double sinc = (m == 0) ? 2 * fc : Math.Sin(2 * Math.PI * fc * m) / (Math.PI * m);
            double win = 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (numTaps - 1)); // Hamming
            double v = sinc * win;
            h[i] = (float)v;
            sum += v;
        }
        for (int i = 0; i < numTaps; i++) h[i] = (float)(h[i] / sum); // unity DC
        return h;
    }
}
