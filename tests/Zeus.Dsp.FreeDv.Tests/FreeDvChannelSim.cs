// SPDX-License-Identifier: GPL-2.0-or-later
using System;

namespace Zeus.Dsp.FreeDv.Tests;

/// <summary>
/// A small, DETERMINISTIC HF-channel simulator for the FreeDV/RADE objective
/// test harness. It degrades a clean 48 kHz modem waveform to a target SNR with
/// calibrated AWGN and an optional 2-ray multipath, so the REAL decoder can be
/// swept across an SNR range and its sync-acquisition / decoded-energy behaviour
/// asserted as a stable regression gate.
///
/// This is the managed analogue of the stored-file channel sims radae's upstream
/// ("Testing RADE") uses to publish its SNR thresholds (V1 ≈ −2 dB AWGN, ~0 dB
/// MPP). It is intentionally test-scoped (not shipped in a DSP lib): a faithful
/// Watterson/ITU-R channel model is NOT the goal — a reproducible, documented
/// degradation good enough to gate "does the modem still acquire above threshold"
/// is.
///
/// ── SNR DEFINITION ────────────────────────────────────────────────────────────
/// SNR here is the IN-BAND signal-to-noise ratio the demodulator sees: signal
/// power and noise power are both measured over the MODEM PASSBAND, not the full
/// 48 kHz Nyquist. Zeus carries the narrowband modem (RADE ≈ 8 kHz-rate OFDM;
/// codec2 700-series ≈ 8 kHz-rate) as the real lane of a 48 kHz block, so all of
/// the modem energy lives below ~4 kHz. <see cref="AddAwgn"/> therefore band-
/// limits the injected Gaussian noise to <see cref="PassbandHz"/> (0..4 kHz at
/// 48 kHz) and scales it so that, measured inside that band, noise_power =
/// signal_power / 10^(snrDb/10). This matches how an SSB-passband SNR is defined
/// on air (noise measured in the occupied bandwidth, not the whole soundcard
/// Nyquist) and makes the swept SNR numbers directly comparable to radae's
/// upstream AWGN thresholds. It is validated independently of any decoder by
/// <c>FreeDvChannelSimTests</c>, which round-trips a known SNR back out within
/// tolerance.
///
/// All randomness comes from a caller-supplied seeded <see cref="Random"/>, so a
/// given (seed, snrDb, multipath) triple produces byte-identical output — no
/// wall-clock, no flakiness.
/// </summary>
internal static class FreeDvChannelSim
{
    public const int Fs = 48000;
    // The modem occupied band. Everything Zeus's FreeDV/RADE modems emit lives in
    // the real lane below the 8 kHz-rate Nyquist (4 kHz); 4 kHz at the 48 kHz host
    // rate cleanly contains it while still being a small fraction of the 24 kHz
    // soundcard Nyquist (so full-band white noise would badly mis-state the SNR).
    public const double PassbandHz = 4000.0;

    /// <summary>
    /// Add calibrated, band-limited AWGN to <paramref name="signal"/> in place so
    /// that the IN-BAND (0..<see cref="PassbandHz"/>) SNR equals
    /// <paramref name="snrDb"/>. Deterministic for a given seeded
    /// <paramref name="rng"/>. See the class remarks for the exact SNR definition.
    /// </summary>
    public static void AddAwgn(float[] signal, double snrDb, Random rng)
    {
        if (signal is null) throw new ArgumentNullException(nameof(signal));
        if (signal.Length == 0) return;

        // 1. In-band signal power: measure the RMS of the signal after the SAME
        //    band-limit we apply to the noise, so the ratio is a true in-band SNR
        //    (out-of-band signal spill, of which there is little, is not counted as
        //    "signal" the demod uses). Filter into scratch; the signal itself is
        //    left untouched by this measurement.
        var lp = new BandLimiter(Fs, PassbandHz);
        double sigSumSq = 0;
        for (int i = 0; i < signal.Length; i++)
        {
            float f = lp.Process(signal[i]);
            sigSumSq += (double)f * f;
        }
        double sigPower = sigSumSq / signal.Length; // mean-square (power) in-band
        if (sigPower <= 0) return; // silent signal — nothing to set an SNR against

        // 2. Target in-band noise power for the requested SNR.
        double targetNoisePower = sigPower / Math.Pow(10.0, snrDb / 10.0);

        // 3. Generate white Gaussian noise, band-limit it to the passband, and
        //    measure the ACTUAL in-band power of a unit-sigma draw through this
        //    exact filter (a brick-wall assumption would be wrong — the one-pole
        //    band-limit only passes a fraction of white-noise power). Then scale
        //    the whole noise vector so its in-band power lands on target. Using the
        //    empirical post-filter power makes the calibration exact regardless of
        //    the filter's noise bandwidth.
        var noise = new float[signal.Length];
        var noiseLp = new BandLimiter(Fs, PassbandHz);
        double noiseSumSq = 0;
        for (int i = 0; i < signal.Length; i++)
        {
            float w = (float)NextGaussian(rng);      // unit-variance white sample
            float bn = noiseLp.Process(w);           // band-limited noise sample
            noise[i] = bn;
            noiseSumSq += (double)bn * bn;
        }
        double noisePower = noiseSumSq / signal.Length;
        if (noisePower <= 0) return;
        double scale = Math.Sqrt(targetNoisePower / noisePower);

        // 4. Mix into the signal.
        for (int i = 0; i < signal.Length; i++)
            signal[i] = signal[i] + (float)(noise[i] * scale);
    }

    /// <summary>
    /// Apply a deterministic 2-ray multipath: the output is the direct path plus a
    /// single delayed, attenuated echo. This is a DEFENSIBLE stand-in for the
    /// frequency-selective fading of an HF path (the two rays interfere and notch
    /// the spectrum), NOT a full Watterson model — there is no Doppler spread, so
    /// it is perfectly reproducible for a CI gate. Upstream radae quotes a ~0 dB
    /// MPP threshold; a fixed 2-ray with a few-ms delay and a strong second path is
    /// a conservative, repeatable proxy for that regime.
    /// </summary>
    /// <param name="signal">The clean 48 kHz waveform (not modified).</param>
    /// <param name="delayMs">Second-path delay in milliseconds (e.g. 2 ms — the
    /// classic MPP "moderate" delay is ~1–2 ms).</param>
    /// <param name="secondPathGain">Linear amplitude of the echo relative to the
    /// direct path (e.g. 0.5). Fixed, not faded, for determinism.</param>
    /// <returns>A new array: direct + delayed echo, energy-normalised so the added
    /// path doesn't itself change the broadband level (keeps the subsequent AWGN
    /// SNR calibration meaningful).</returns>
    public static float[] AddMultipath(float[] signal, double delayMs, double secondPathGain)
    {
        if (signal is null) throw new ArgumentNullException(nameof(signal));
        int delay = (int)Math.Round(delayMs * Fs / 1000.0);
        var outp = new float[signal.Length];
        // Normalise the two-tap sum so the total path power is unchanged: for two
        // (largely) uncorrelated taps the power adds, so divide by sqrt(1+g^2).
        double norm = 1.0 / Math.Sqrt(1.0 + secondPathGain * secondPathGain);
        for (int i = 0; i < signal.Length; i++)
        {
            double v = signal[i];
            if (i >= delay) v += secondPathGain * signal[i - delay];
            outp[i] = (float)(v * norm);
        }
        return outp;
    }

    /// <summary>
    /// Measure the in-band SNR of <paramref name="noisy"/> relative to a known
    /// clean reference <paramref name="clean"/> (same length). Used by the sim's
    /// own unit test to prove <see cref="AddAwgn"/> hit its target: the residual
    /// (noisy − clean) is the injected noise, and both signal and noise are
    /// measured through the same passband filter as the calibration used.
    /// </summary>
    public static double MeasureInBandSnrDb(float[] clean, float[] noisy)
    {
        if (clean is null) throw new ArgumentNullException(nameof(clean));
        if (noisy is null) throw new ArgumentNullException(nameof(noisy));
        if (clean.Length != noisy.Length)
            throw new ArgumentException("clean/noisy length mismatch");

        var sigLp = new BandLimiter(Fs, PassbandHz);
        var noiseLp = new BandLimiter(Fs, PassbandHz);
        double sigSumSq = 0, noiseSumSq = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            float s = sigLp.Process(clean[i]);
            float n = noiseLp.Process(noisy[i] - clean[i]);
            sigSumSq += (double)s * s;
            noiseSumSq += (double)n * n;
        }
        double sigPower = sigSumSq / clean.Length;
        double noisePower = noiseSumSq / clean.Length;
        return 10.0 * Math.Log10(sigPower / noisePower);
    }

    /// <summary>Standard normal (mean 0, variance 1) via Box–Muller.</summary>
    private static double NextGaussian(Random rng)
    {
        // Draw two uniforms in (0,1]; guard u1 away from 0 for the log.
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// A simple deterministic low-pass used both to define the SNR band and to
    /// band-limit the injected noise. A 4th-order Butterworth (two cascaded
    /// biquads) at <c>cutoffHz</c> — steep enough that "in-band" is a meaningful
    /// notion (white noise above the passband is well rejected) while staying
    /// fully deterministic and allocation-cheap. Not a linear-phase brick wall,
    /// which is fine: the SAME filter is applied to signal and noise for the SNR
    /// definition, so any passband ripple cancels in the ratio.
    /// </summary>
    private sealed class BandLimiter
    {
        private readonly Biquad _b0, _b1;

        public BandLimiter(double fs, double cutoffHz)
        {
            // Two Butterworth biquads with the standard 4th-order Q pair.
            _b0 = Biquad.LowPass(fs, cutoffHz, 0.54119610);
            _b1 = Biquad.LowPass(fs, cutoffHz, 1.30656296);
        }

        public float Process(float x) => _b1.Process(_b0.Process(x));
    }

    /// <summary>Direct-form-II transposed biquad (deterministic, per-sample).</summary>
    private sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        private Biquad(double b0, double b1, double b2, double a1, double a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
        }

        public static Biquad LowPass(double fs, double f0, double q)
        {
            double w0 = 2.0 * Math.PI * f0 / fs;
            double cw = Math.Cos(w0), sw = Math.Sin(w0);
            double alpha = sw / (2.0 * q);
            double b0 = (1.0 - cw) / 2.0;
            double b1 = 1.0 - cw;
            double b2 = (1.0 - cw) / 2.0;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cw;
            double a2 = 1.0 - alpha;
            return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        }

        public float Process(float x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return (float)y;
        }
    }
}
