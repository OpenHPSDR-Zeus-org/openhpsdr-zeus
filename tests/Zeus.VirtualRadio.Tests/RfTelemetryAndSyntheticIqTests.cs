// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Server;          // public TxMetersService.ComputeMeters (Zeus's forward math)
using Zeus.VirtualRadio;
using Zeus.VirtualRadio.Rf; // internal RfTelemetryModel / SyntheticIqGenerator (needs InternalsVisibleTo)

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Phase-1 RF telemetry + synthetic-signal unit tests. All socketless / pure
/// compute — the calibration-inversion suite is the anti-drift round trip: feed
/// the ADC counts the emulator would put on the wire through Zeus's UNMODIFIED
/// <see cref="TxMetersService.ComputeMeters"/> and assert it recovers the watts
/// the emulator intended.
/// </summary>
public class RfTelemetryAndSyntheticIqTests
{
    // ANAN-10E impersonation: HermesII → RadioCalibration.Hermes
    // (bridge 0.09 / ref 3.3 / offset 6 / 10 W rated). The model inverts onto
    // the calibration's MaxWatts (10 W) so full drive reads the board's real
    // rated output, not the 100 W PA-gain-bracket reference.
    private static readonly RadioCalibration HermesIICal = RadioCalibration.Hermes;
    private const double RatedWatts = 10.0;   // == HermesIICal.MaxWatts
    private const long Band20mHz = 14_074_000;

    private static RfTelemetryModel NewModel() =>
        new(VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P1));

    // ---- RF telemetry / calibration inversion -----------------------------

    [Fact]
    public void Compute_NotKeyed_ReturnsZeroPower()
    {
        var model = NewModel();

        // Even at full drive, an unkeyed radio reports nothing (FWD/REF are
        // PTT-gated in the firmware).
        var tel = model.Compute(driveByte: 255, Band20mHz, mox: false);

        Assert.Equal(0, tel.FwdAdc);
        Assert.Equal(0, tel.RefAdc);
        Assert.Equal(0.0, tel.FwdWatts);
        Assert.Equal(0.0, tel.RefWatts);
        Assert.Equal(1.0, tel.Swr);
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)32)]
    [InlineData((byte)64)]
    [InlineData((byte)128)]
    [InlineData((byte)200)]
    [InlineData((byte)255)]
    public void Compute_ForwardWatts_RoundTripsThroughZeusMeterMath(byte driveByte)
    {
        var model = NewModel();

        var tel = model.Compute(driveByte, Band20mHz, mox: true);

        // The curve the emulator intends: quadratic in drive byte.
        double drive = driveByte / 255.0;
        double targetWatts = RatedWatts * drive * drive;

        // Feed the wire ADC counts through Zeus's OWN forward math.
        var (fwdW, _, _) = TxMetersService.ComputeMeters(tel.FwdAdc, tel.RefAdc, HermesIICal);

        // Round trip recovers the intended watts (loose at µW levels where the
        // single-count ADC offset rounding dominates, tight at real power).
        double tolerance = 0.02 * targetWatts + 0.01;
        Assert.True(
            Math.Abs(fwdW - targetWatts) <= tolerance,
            $"drive={driveByte}: target={targetWatts:G6} W recovered={fwdW:G6} W (tol {tolerance:G3})");

        // The model reports exactly what the client will render (same ADC, same
        // calibration, same ComputeMeters).
        Assert.Equal(fwdW, tel.FwdWatts, 9);
        Assert.InRange(tel.FwdAdc, 0, 4095);
        Assert.InRange(tel.RefAdc, 0, 4095);
    }

    [Fact]
    public void Compute_FullDrive_ReadsRatedWatts()
    {
        var model = NewModel();

        var tel = model.Compute(driveByte: 255, Band20mHz, mox: true);

        // 100 % drive on the ANAN-10E lands on its ~10 W rated output.
        Assert.Equal(RatedWatts, tel.FwdWatts, 1);
    }

    [Fact]
    public void Compute_ForwardWatts_IsMonotoneInDriveByte()
    {
        var model = NewModel();

        double previous = -1.0;
        for (int d = 0; d <= 255; d += 5)
        {
            var tel = model.Compute((byte)d, Band20mHz, mox: true);
            Assert.True(tel.FwdWatts >= previous,
                $"watts must be monotone: drive={d} gave {tel.FwdWatts:G6} W after {previous:G6} W");
            previous = tel.FwdWatts;
        }
    }

    [Fact]
    public void Compute_Swr_MatchesRequestedReflection()
    {
        var model = NewModel();

        // Full drive (10 W, above the 2 W SWR floor) with a 2.0:1 mismatch.
        var tel = model.Compute(driveByte: 255, Band20mHz, mox: true, swr: 2.0);

        Assert.Equal(2.0, tel.Swr, 1);
        Assert.True(tel.RefWatts > 0.0 && tel.RefWatts < tel.FwdWatts);
    }

    [Fact]
    public void Compute_MatchedLoad_KeepsSwrNearUnity()
    {
        var model = NewModel();

        var tel = model.Compute(driveByte: 255, Band20mHz, mox: true, swr: RfTelemetryModel.DefaultSwr);

        // ~1.1:1 default, well below any trip threshold.
        Assert.InRange(tel.Swr, 1.0, 1.3);
    }

    // ---- Synthetic IQ generator -------------------------------------------

    private const double TwoPi = 2.0 * Math.PI;

    private static VirtualRadioProfile SignalProfile(
        IReadOnlyList<ToneSpec> tones, double noiseDbc, int rateKhz = 48) =>
        VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P1) with
        {
            SampleRateKhz = rateKhz,
            Tones = tones,
            NoiseFloorDbc = noiseDbc,
        };

    /// <summary>|Σ z[n]·e^{-j2π f n/fs}| / N — magnitude of the complex IQ at f.</summary>
    private static double MagnitudeAt(ReadOnlySpan<double> iq, int complexN, double freqHz, double fs)
    {
        double re = 0.0, im = 0.0;
        for (int n = 0; n < complexN; n++)
        {
            double i = iq[2 * n];
            double q = iq[2 * n + 1];
            double theta = TwoPi * freqHz * n / fs;
            double c = Math.Cos(theta), s = Math.Sin(theta);
            // z·e^{-jθ} = (i+jq)(cosθ − j sinθ)
            re += i * c + q * s;
            im += q * c - i * s;
        }
        return Math.Sqrt(re * re + im * im) / complexN;
    }

    [Fact]
    public void Generate_PlacesToneAtExpectedBasebandOffset()
    {
        const long tuned = 14_074_000;
        const long toneHz = tuned + 6_000;       // +6 kHz offset, inside 24 kHz Nyquist
        const double fs = 48_000.0;
        const int n = 4_800;

        var gen = new SyntheticIqGenerator(
            SignalProfile(new[] { new ToneSpec(toneHz, 0.0) }, noiseDbc: -110.0));

        var iq = new double[2 * n];
        gen.Generate(iq, n, tuned);

        // Scan candidate offsets; the argmax must be the +6 kHz bin.
        double bestMag = -1.0;
        long bestOffset = long.MinValue;
        for (long off = -20_000; off <= 20_000; off += 500)
        {
            double mag = MagnitudeAt(iq, n, off, fs);
            if (mag > bestMag) { bestMag = mag; bestOffset = off; }
        }

        Assert.Equal(6_000, bestOffset);
    }

    [Fact]
    public void Generate_RespectsNoiseFloor()
    {
        const double noiseDbc = -40.0;            // RMS 0.01 full-scale
        const int n = 40_000;
        double expectedRms = Math.Pow(10.0, noiseDbc / 20.0);

        var gen = new SyntheticIqGenerator(
            SignalProfile(Array.Empty<ToneSpec>(), noiseDbc));

        var iq = new double[2 * n];
        gen.Generate(iq, n, tunedHz: 14_074_000);

        double power = 0.0;
        for (int k = 0; k < n; k++)
        {
            double i = iq[2 * k];
            double q = iq[2 * k + 1];
            power += i * i + q * q;
        }
        double rms = Math.Sqrt(power / n);

        Assert.InRange(rms, expectedRms * 0.85, expectedRms * 1.15);
    }

    [Fact]
    public void Generate_ToneRisesWellAboveNoiseFloor()
    {
        const long tuned = 14_074_000;
        const long toneHz = tuned + 3_000;
        const double fs = 48_000.0;
        const int n = 8_192;

        var gen = new SyntheticIqGenerator(
            SignalProfile(new[] { new ToneSpec(toneHz, 0.0) }, noiseDbc: -60.0));

        var iq = new double[2 * n];
        gen.Generate(iq, n, tuned);

        double toneMag = MagnitudeAt(iq, n, 3_000, fs);
        double offToneMag = MagnitudeAt(iq, n, -9_000, fs);

        Assert.True(toneMag > offToneMag * 20.0,
            $"tone {toneMag:G4} should dominate off-tone {offToneMag:G4}");
    }

    [Fact]
    public void Generate_IsDeterministicAndPhaseContinuousAcrossCalls()
    {
        const long tuned = 14_074_000;
        const int n = 1_024;
        var tones = new[] { new ToneSpec(tuned + 5_000, -10.0) };

        // One 2N call.
        var whole = new double[4 * n];
        new SyntheticIqGenerator(SignalProfile(tones, -90.0)).Generate(whole, 2 * n, tuned);

        // Two N calls on a fresh generator: phase + noise stream persist, so the
        // concatenation must be bit-identical (continuity + determinism).
        var split = new double[4 * n];
        var gen2 = new SyntheticIqGenerator(SignalProfile(tones, -90.0));
        gen2.Generate(split.AsSpan(0, 2 * n), n, tuned);
        gen2.Generate(split.AsSpan(2 * n, 2 * n), n, tuned);

        Assert.Equal(whole, split);
    }

    [Fact]
    public void Generate_ThrowsWhenBufferTooSmall()
    {
        var gen = new SyntheticIqGenerator(
            SignalProfile(Array.Empty<ToneSpec>(), -110.0));

        Assert.Throws<ArgumentException>(() => gen.Generate(new double[3], complexSamples: 4, tunedHz: 0));
    }
}
