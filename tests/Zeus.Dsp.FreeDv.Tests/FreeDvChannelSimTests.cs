// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using Xunit;

namespace Zeus.Dsp.FreeDv.Tests;

/// <summary>
/// PURE (no native lib) validation of the channel simulator itself, independent
/// of any decoder. Before trusting an SNR-sweep result through the real modem we
/// must know the sim actually hits the SNR it claims — otherwise a decoder pass/
/// fail says nothing. These deterministic tests (seeded <see cref="Random"/>)
/// round-trip a KNOWN target SNR back out of <see cref="FreeDvChannelSim"/> and
/// check the 2-ray multipath is energy-preserving and correctly delayed.
///
/// Inspired by radae's "Testing RADE" discipline: the measurement instrument is
/// itself calibrated before it is used to grade the modem.
/// </summary>
public class FreeDvChannelSimTests
{
    private const int Fs = FreeDvChannelSim.Fs;

    // A band-limited test signal that lives inside the modem passband, so its
    // in-band power is essentially its full power (a fair stand-in for the real
    // narrowband modem waveform when validating the SNR math).
    private static float[] InBandSignal(int n, double amp = 0.3)
    {
        var b = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Fs;
            // Two tones inside 0..4 kHz.
            b[i] = (float)(amp * (Math.Sin(2 * Math.PI * 1200 * t)
                                + 0.5 * Math.Sin(2 * Math.PI * 2200 * t)));
        }
        return b;
    }

    [Theory]
    [InlineData(12.0)]
    [InlineData(6.0)]
    [InlineData(3.0)]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    public void AddAwgn_HitsTargetInBandSnr_WithinTolerance(double targetSnrDb)
    {
        // 4 s at 48 kHz — long enough that the empirical noise power is a tight
        // estimate of the ensemble power, so the round-tripped SNR is stable.
        int n = 4 * Fs;
        var clean = InBandSignal(n);
        var noisy = (float[])clean.Clone();

        var rng = new Random(0xC0FFEE);
        FreeDvChannelSim.AddAwgn(noisy, targetSnrDb, rng);

        double measured = FreeDvChannelSim.MeasureInBandSnrDb(clean, noisy);

        // ±0.75 dB: the calibration scales noise by its empirically-measured post-
        // filter power, so the only residual error is the finite-length variance
        // of the estimate. Tolerance is comfortably above that but tight enough to
        // catch a real math regression (e.g. a full-band instead of in-band SNR).
        Assert.True(Math.Abs(measured - targetSnrDb) <= 0.75,
            $"AddAwgn target {targetSnrDb:F1} dB but measured {measured:F2} dB in-band " +
            $"(err {measured - targetSnrDb:+0.00;-0.00} dB) — SNR calibration is off.");
    }

    [Fact]
    public void AddAwgn_IsDeterministic_ForAGivenSeed()
    {
        int n = Fs; // 1 s
        var a = InBandSignal(n);
        var b = InBandSignal(n);

        FreeDvChannelSim.AddAwgn(a, 3.0, new Random(1234));
        FreeDvChannelSim.AddAwgn(b, 3.0, new Random(1234));

        for (int i = 0; i < n; i++)
            Assert.Equal(a[i], b[i]); // byte-identical: no wall-clock, no flakiness
    }

    [Fact]
    public void AddAwgn_LowerSnr_AddsMoreNoise()
    {
        int n = 2 * Fs;
        var clean = InBandSignal(n);

        var hi = (float[])clean.Clone();
        var lo = (float[])clean.Clone();
        FreeDvChannelSim.AddAwgn(hi, 10.0, new Random(7));
        FreeDvChannelSim.AddAwgn(lo, 0.0, new Random(7));

        double snrHi = FreeDvChannelSim.MeasureInBandSnrDb(clean, hi);
        double snrLo = FreeDvChannelSim.MeasureInBandSnrDb(clean, lo);

        // Monotonic: a lower requested SNR must actually measure lower.
        Assert.True(snrLo < snrHi - 5.0,
            $"expected 0 dB request to measure well below 10 dB request, got lo={snrLo:F2} hi={snrHi:F2}");
    }

    [Fact]
    public void AddMultipath_IsEnergyPreserving_AndDelaysSecondPath()
    {
        // Use a BROADBAND (white) source for the energy check: the sqrt(1+g^2)
        // power-normalisation in AddMultipath is derived for uncorrelated taps,
        // which holds for a wideband modem waveform but NOT for a pure tone (whose
        // delayed copy interferes coherently and can notch the level). White noise
        // is the honest signal to validate the intended broadband model against.
        int n = Fs;
        var clean = new float[n];
        var rng = new Random(99);
        for (int i = 0; i < n; i++) clean[i] = (float)(0.3 * (rng.NextDouble() * 2 - 1));

        double rmsBefore = Rms(clean);
        var mp = FreeDvChannelSim.AddMultipath(clean, delayMs: 2.0, secondPathGain: 0.5);
        double rmsAfter = Rms(mp);

        // Energy-normalised: for uncorrelated taps power adds and the sqrt(1+g^2)
        // norm restores the original level to within a small margin.
        Assert.True(Math.Abs(rmsAfter - rmsBefore) / rmsBefore < 0.05,
            $"multipath changed broadband level too much: before={rmsBefore:F4} after={rmsAfter:F4}");

        // The first `delay` samples must equal the direct path scaled by the norm
        // only (no echo has arrived yet), proving the delay line is real.
        int delay = (int)Math.Round(2.0 * Fs / 1000.0);
        double norm = 1.0 / Math.Sqrt(1.0 + 0.5 * 0.5);
        for (int i = 0; i < delay; i++)
            Assert.Equal(clean[i] * norm, mp[i], 5);
    }

    private static double Rms(float[] x)
    {
        double s = 0;
        foreach (var v in x) s += (double)v * v;
        return Math.Sqrt(s / x.Length);
    }
}
