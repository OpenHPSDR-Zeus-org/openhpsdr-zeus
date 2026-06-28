// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8Synth — pure managed FT8/FT4 GFSK/MFSK tone synthesizer (the TX-side
// counterpart of Ft8Decoder.Encode). There is no native synth for FT8/FT4 (the
// vendored zeus_ft8 ships encode + decode only), so this is C# — cross-platform
// by construction, no P/Invoke, identical on macOS / Windows / Linux / Pi.
//
// Algorithm is faithful to WSJT-X gen_ft8wave.f90 / gen_ft4wave.f90 (the
// authoritative reference for ANAN-class digital, the role Thetis plays for the
// protocol/DSP path): build a smoothed instantaneous-frequency waveform by
// summing per-symbol shaping pulses, integrate to phase, emit sin(phi). The
// output is CONSTANT-ENVELOPE (a single clean tone at each instant) — this is
// the audio fed into the WDSP TX chain, NOT RF power. `amplitude` sets the audio
// level into TXA; drive/power are unchanged and owned elsewhere.
//
// FT8 uses an 8-GFSK Gaussian pulse (BT=2.0) spanning 3 symbols; FT4 uses a
// 4-MFSK raised-cosine (partial-response) pulse spanning 2 symbols. Both use
// modulation index hmod=1, so the tone spacing equals the baud rate
// (FT8 6.25 Hz / 0.16 s; FT4 20.8333 Hz / 0.048 s).

namespace Zeus.Dsp.Ft8;

/// <summary>
/// Pure-managed GFSK/MFSK synthesizer for FT8/FT4 transmit audio. Turns the FSK
/// tone indices from <see cref="Ft8Decoder.Encode"/> into a continuous-phase
/// mono waveform at any sample rate (48 kHz for the Zeus TX chain, 12 kHz for a
/// round-trip decode test).
/// </summary>
public static class Ft8Synth
{
    /// <summary>The canonical TX-chain audio sample rate.</summary>
    public const int TxSampleRate = 48000;

    // Per-protocol modulation constants (WSJT-X). hmod = 1 for both.
    private const double Ft8SymbolPeriodSec = 0.16;       // 79 symbols
    private const double Ft4SymbolPeriodSec = 0.048;      // 105 symbols
    private const int Ft8SymbolCount = 79;
    private const int Ft4SymbolCount = 105;
    private const double GaussianBt = 2.0;                // FT8 Gaussian pulse BT

    /// <summary>
    /// Synthesize the continuous-phase FT8/FT4 audio for a tone-index sequence.
    /// <paramref name="tones"/> are 0..7 (FT8) / 0..3 (FT4) as produced by
    /// <see cref="Ft8Decoder.Encode"/>. <paramref name="baseFreqHz"/> is the
    /// audio offset of tone 0 (the operator TX offset, default 1500 Hz).
    /// <paramref name="amplitude"/> is the peak audio level (RMS = amplitude/√2).
    /// Returns exactly <c>tones.Length * nsps</c> samples, or null if the tone
    /// count doesn't match the protocol.
    /// </summary>
    public static float[] Synth(
        byte[] tones,
        Ft8Protocol protocol = Ft8Protocol.Ft8,
        float baseFreqHz = 1500f,
        int sampleRate = TxSampleRate,
        float amplitude = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(tones);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        bool ft4 = protocol == Ft8Protocol.Ft4;
        int expectedSymbols = ft4 ? Ft4SymbolCount : Ft8SymbolCount;
        if (tones.Length != expectedSymbols)
            throw new ArgumentException(
                $"expected {expectedSymbols} {protocol} tones, got {tones.Length}", nameof(tones));

        double symbolPeriod = ft4 ? Ft4SymbolPeriodSec : Ft8SymbolPeriodSec;
        int nsps = (int)Math.Round(symbolPeriod * sampleRate);
        if (nsps <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate), "sample rate too low");
        int nsym = tones.Length;
        double dt = 1.0 / sampleRate;

        // Shaping pulse, 3 symbols wide (the FT4 raised-cosine is zero in the
        // outer thirds; the FT8 Gaussian tapers there).
        int pulseLen = 3 * nsps;
        var pulse = new double[pulseLen];
        for (int i = 0; i < pulseLen; i++)
        {
            // tt centred on the middle symbol. WSJT-X gen_ft8wave uses a 1-based
            // loop (Fortran i=1..3*nsps) with tt=(i-1.5*nsps)/nsps; our loop is
            // 0-based, so Fortran's i == our (i+1). The (i+1) below is therefore the
            // faithful translation — do NOT drop it (that would shift the pulse a
            // sample early).
            double tt = (i + 1 - 1.5 * nsps) / nsps;
            pulse[i] = ft4 ? RaisedCosinePulse(tt) : GaussianPulse(GaussianBt, tt);
        }

        // Smoothed instantaneous frequency (radians/sample). Length (nsym+2)*nsps
        // so the first and last symbols are fully shaped; trim the one-symbol
        // lead/lag after integrating.
        int dphiLen = (nsym + 2) * nsps;
        var dphi = new double[dphiLen];
        double carrier = 2.0 * Math.PI * baseFreqHz * dt;
        for (int k = 0; k < dphiLen; k++) dphi[k] = carrier;

        double dphiPeak = 2.0 * Math.PI / nsps; // hmod = 1
        for (int j = 0; j < nsym; j++)
        {
            int tone = tones[j];
            if (tone == 0) continue;
            int ib = j * nsps;
            double w = dphiPeak * tone;
            for (int k = 0; k < pulseLen; k++)
                dphi[ib + k] += w * pulse[k];
        }

        // Integrate to phase and emit. Keep the middle nsym*nsps samples
        // (discard the one-symbol lead/lag introduced by the pulse padding).
        var outAudio = new float[nsym * nsps];
        double phi = 0.0;
        for (int k = 0; k < nsps; k++) phi += dphi[k]; // advance through the lead symbol
        for (int n = 0; n < outAudio.Length; n++)
        {
            outAudio[n] = (float)(amplitude * Math.Sin(phi));
            phi += dphi[nsps + n];
            // Keep phi bounded so float drift never accumulates over 600k samples.
            if (phi > Math.PI) phi -= 2.0 * Math.PI;
            else if (phi < -Math.PI) phi += 2.0 * Math.PI;
        }

        // Raised-cosine ramp at the very start/end to kill key clicks (half a
        // symbol, WSJT-X uses nsps/8 — a half symbol is gentler and well inside
        // the decoder's tolerance since the sync symbols sit away from the edges).
        int nramp = Math.Max(1, nsps / 8);
        for (int n = 0; n < nramp && n < outAudio.Length; n++)
        {
            double env = 0.5 * (1.0 - Math.Cos(Math.PI * n / nramp));
            outAudio[n] *= (float)env;
            outAudio[outAudio.Length - 1 - n] *= (float)env;
        }

        return outAudio;
    }

    // Gaussian GFSK shaping pulse (WSJT-X gfsk_pulse). Integrated Gaussian over
    // one symbol, so a sequence of these sums to a smooth frequency trajectory.
    private static double GaussianPulse(double bt, double t)
    {
        double c = Math.PI * Math.Sqrt(2.0 / Math.Log(2.0));
        return 0.5 * (Erf(c * bt * (t + 0.5)) - Erf(c * bt * (t - 0.5)));
    }

    // FT4 raised-cosine (partial-response) shaping pulse spanning two symbols
    // ([-1,1]); a Nyquist pulse so adjacent symbols sum to unity with no ISI.
    private static double RaisedCosinePulse(double t)
    {
        if (t <= -1.0 || t >= 1.0) return 0.0;
        return 0.5 * (1.0 + Math.Cos(Math.PI * t));
    }

    // Abramowitz & Stegun 7.1.26 error-function approximation (|error| < 1.5e-7),
    // ample for shaping-pulse generation. .NET has no built-in Math.Erf.
    private static double Erf(double x)
    {
        double sign = x < 0 ? -1.0 : 1.0;
        double ax = Math.Abs(x);
        double tt = 1.0 / (1.0 + 0.3275911 * ax);
        double y = 1.0 - (((((1.061405429 * tt - 1.453152027) * tt) + 1.421413741) * tt
                            - 0.284496736) * tt + 0.254829592) * tt * Math.Exp(-ax * ax);
        return sign * y;
    }
}
