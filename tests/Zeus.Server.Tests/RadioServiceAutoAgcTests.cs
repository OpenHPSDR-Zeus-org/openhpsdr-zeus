// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Protocol1;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RadioServiceAutoAgcTests : IDisposable
{
    private readonly string _basePath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-auto-agc-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _owned = new();

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            try { d.Dispose(); } catch { }
        }

        foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(_basePath) + "*"))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private RadioService NewRadio()
    {
        var dsp = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _basePath + ".dsp.db");
        var pa = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _basePath + ".pa.db");
        _owned.Add(dsp);
        _owned.Add(pa);
        return new RadioService(NullLoggerFactory.Instance, dsp, pa);
    }

    [Fact]
    public void FreshRadio_UsesWdspMediumAgcTopBaseline()
    {
        using var radio = NewRadio();

        Assert.Equal(90.0, radio.Snapshot().AgcTopDb);
    }

    [Fact]
    public void AutoAgc_SeatsKneeAtNoiseFloor_OnNoisyBand()
    {
        // Thetis auto-AGC-T seats the AGC knee at the noise floor. Default
        // baseline is 90 dB; a noisy -80 dBm floor resolves (via the WDSP
        // threshold→top conversion) to an effective AGC-T below the baseline, so
        // AgcOffsetDb goes negative — the loop lowers gain on a noisy band. The
        // exact value comes from the servo helper (unit-tested separately), so
        // this asserts the loop WIRES it, not a magic constant.
        using var radio = NewRadio();
        radio.SetAutoAgc(true);

        for (int i = 0; i < 6; i++)
            radio.HandleRxMetersForAutoAgc(
                signalDbm: -50.0, spectrumFloorDbm: -80.0,
                adcPkDbfs: double.NaN, agcGainDb: double.NaN, nowMs: i * 500);

        double expectedTop = radio.AutoAgcTopFromNoiseFloor(-80.0); // 68 dB at default BW/rate
        var snap = radio.Snapshot();
        Assert.Equal(90.0, snap.AgcTopDb);
        Assert.Equal(expectedTop - 90.0, snap.AgcOffsetDb);       // offset = top − baseline
        Assert.Equal(expectedTop, snap.AgcTopDb + snap.AgcOffsetDb); // effective AGC-T = seated top
        Assert.True(snap.AgcOffsetDb < 0.0, "noisy band lowers gain below baseline");
    }

    [Fact]
    public void AutoAgc_RejectsSingleQuietDip_TracksSteadyFloor()
    {
        // A single deep dip (-120) must not spike the gain: the low-percentile
        // floor estimate rejects the outlier and tracks the steady -90 dBm floor.
        // So the seated top reflects -90 (autoTop≈78), NOT the -120 dip
        // (autoTop≈108). This proves the estimator's robustness to transients.
        using var radio = NewRadio();
        radio.SetAgcTop(60.0);
        radio.SetAutoAgc(true);

        var samples = new[] { -120.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0 };
        for (int i = 0; i < samples.Length; i++)
            radio.HandleRxMeterForAutoAgc(samples[i], i * 500);

        double steadyTop = radio.AutoAgcTopFromNoiseFloor(-90.0);
        Assert.Equal(steadyTop - 60.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_JumpsToNoiseFloorTarget_FastNotSlewed()
    {
        // baseline 45; a quiet -100 dBm floor seats the knee high (autoTop≈88 at
        // default BW/rate) ⇒ offset ≈ +43. The loop JUMPS to the target once it
        // has a few samples (~1.5 s) — it does NOT crawl there 0.5 dB/tick.
        using var radio = NewRadio();
        radio.SetAgcTop(45.0);
        radio.SetAutoAgc(true);

        // Just past the min-sample warm-up: by now a slew would still be only a
        // couple dB in; the jump is already at the full target.
        for (int i = 0; i < 4; i++)
            radio.HandleRxMeterForAutoAgc(-100.0, i * 500);

        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-100.0) - 45.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_ReSeedsOnBandChange_AndReconvergesFast()
    {
        // Converge on a quiet band (low floor -> positive offset), then make a
        // band-scale VFO jump to a noisier band; the floor window re-seeds
        // (fast-attack) and the offset jumps to the new, lower target quickly.
        using var radio = NewRadio();
        radio.SetVfo(14_100_000);
        radio.SetAgcTop(45.0);
        radio.SetAutoAgc(true);
        for (int i = 0; i < 4; i++)
            radio.HandleRxMeterForAutoAgc(-100.0, i * 500);
        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-100.0) - 45.0, radio.Snapshot().AgcOffsetDb); // quiet band

        radio.SetVfo(7_100_000); // > 0.5 MHz move => fast-attack re-seed
        for (int i = 4; i < 8; i++)
            radio.HandleRxMeterForAutoAgc(-70.0, i * 500); // noisier band

        // Re-seeded to the new band: the seated top now follows the -70 floor
        // (autoTop≈58 ⇒ offset 58-45≈+13), lower than the quiet band. The re-seed
        // is what makes the jump fast and complete.
        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-70.0) - 45.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_TracksSpectrumFloor_WhenProvided_IgnoringContaminatedSignalDbm()
    {
        // The spectrum floor MUST win over signalDbm (#806). Feed a quiet real
        // floor (-105) alongside a loud, contaminated signalDbm (-50, the kind of
        // post-AGC RMS value that fooled the old loop). The seated top must
        // reflect the quiet -105 floor (positive offset), NOT the loud -50
        // (which would give a large negative offset) — proving spectrum is
        // authoritative.
        using var radio = NewRadio();
        radio.SetAgcTop(60.0);
        radio.SetAutoAgc(true);

        for (int i = 0; i < 4; i++)
            radio.HandleRxMetersForAutoAgc(
                signalDbm: -50.0, spectrumFloorDbm: -105.0,
                adcPkDbfs: double.NaN, agcGainDb: double.NaN, nowMs: i * 500);

        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-105.0) - 60.0, radio.Snapshot().AgcOffsetDb);
        Assert.True(radio.Snapshot().AgcOffsetDb > 0.0, "tracks the quiet spectrum floor, not loud signalDbm");
    }

    [Fact]
    public void AutoAgc_FallsBackToSignalDbm_WhenSpectrumFloorIsNaN()
    {
        // No spectrum available (synthetic engine / stale frame): the loop falls
        // back to signalDbm and behaves exactly as the S-meter-only path. baseline
        // 45, floor -100 ⇒ seated top ≈ 88 ⇒ offset ≈ +43.
        using var radio = NewRadio();
        radio.SetAgcTop(45.0);
        radio.SetAutoAgc(true);

        for (int i = 0; i < 4; i++)
            radio.HandleRxMetersForAutoAgc(
                signalDbm: -100.0, spectrumFloorDbm: double.NaN,
                adcPkDbfs: double.NaN, agcGainDb: double.NaN, nowMs: i * 500);

        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-100.0) - 45.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_ReengagesSignalFallback_AfterSustainedSpectrumOutage()
    {
        // Regression for the latch-freeze bug: once seeded from the spectrum, a
        // SUSTAINED spectrum outage (NaN) under steady RX must NOT freeze the
        // loop. It holds briefly (transient), then re-engages the S-meter
        // fallback and resumes tracking.
        using var radio = NewRadio();
        radio.SetAgcTop(45.0);
        radio.SetAutoAgc(true);

        // Converge on the spectrum floor: -100 ⇒ seated top ≈ 88 ⇒ offset ≈ +43.
        for (int i = 0; i < 4; i++)
            radio.HandleRxMetersForAutoAgc(
                signalDbm: -50.0, spectrumFloorDbm: -100.0,
                adcPkDbfs: double.NaN, agcGainDb: double.NaN, nowMs: i * 500);
        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-100.0) - 45.0, radio.Snapshot().AgcOffsetDb);

        // Spectrum goes dark for several seconds while a noisier S-meter floor
        // (-70) keeps arriving. After the transient hold (< 1.5 s) the fallback
        // re-engages, re-seeds, and converges to the -70 floor (lower gain).
        // Before the latch-freeze fix the offset stayed frozen at the quiet value.
        for (int i = 4; i < 16; i++)
            radio.HandleRxMetersForAutoAgc(
                signalDbm: -70.0, spectrumFloorDbm: double.NaN,
                adcPkDbfs: double.NaN, agcGainDb: double.NaN, nowMs: i * 500);

        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-70.0) - 45.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_IgnoresAdcPressureAndAgcCut_TracksNoiseFloorOnly()
    {
        // Thetis parity (console.cs tmrAutoAGC_Tick): the auto-AGC-T servo seats the
        // knee at the noise floor and does NOTHING else. A hot ADC (-5 dBfs) with
        // WDSP cutting hard (-28 dB) must NOT pull the effective gain below the
        // floor-path target. The old Zeus-only ADC-overload cut did exactly that and
        // manufactured loudness pumping on strong signals ("low then high"); it has
        // been removed. So the offset equals the pure noise-floor value regardless
        // of ADC peak / WDSP AGC gain.
        using var radio = NewRadio();
        radio.SetAgcTop(60.0);
        radio.SetAutoAgc(true);

        for (int i = 0; i < 20; i++)
            radio.HandleRxMetersForAutoAgc(signalDbm: -72.0, adcPkDbfs: -5.0, agcGainDb: -28.0, nowMs: i * 500);

        var snap = radio.Snapshot();
        Assert.Equal(60.0, snap.AgcTopDb);
        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-72.0) - 60.0, snap.AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_DoesNotLowerEffectiveGainWhenWdspAgcCutsWithCleanAdcHeadroom()
    {
        // WDSP AGC cutting (agcGain -24.5) with clean ADC headroom (-60 dBfs) must
        // track the pure noise-floor servo target — no extra cut. (With the ADC cut
        // removed this is the same path as the hot-ADC case above; kept as a
        // regression guard that AGC-gain alone never moves the offset.)
        using var radio = NewRadio();
        radio.SetAgcTop(52.0);
        radio.SetAutoAgc(true);

        for (int i = 0; i < 20; i++)
            radio.HandleRxMetersForAutoAgc(signalDbm: -92.0, adcPkDbfs: -60.0, agcGainDb: -24.5, nowMs: i * 500);

        var snap = radio.Snapshot();
        Assert.Equal(52.0, snap.AgcTopDb);
        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-92.0) - 52.0, snap.AgcOffsetDb);
    }

    // ── issue #733: AGC-T slider must be authoritative ────────────────────────
    [Fact]
    public void SetAgcTop_TakesManualControl_DisablesAutoAndZeroesOffset()
    {
        using var radio = NewRadio();
        radio.SetAgcTop(45.0); // low baseline leaves the loop headroom to raise gain
        radio.SetAutoAgc(true);
        // Drive the auto-AGC loop to a non-zero positive offset (a weak -100 dBm
        // signal makes the loop raise gain — same scenario as the step-raise test).
        for (int i = 0; i < 13; i++)
            radio.HandleRxMeterForAutoAgc(-100.0, i * 500);
        Assert.True(radio.Snapshot().AgcOffsetDb > 0.0,
            "precondition: auto-AGC accrued a positive offset");

        var snap = radio.SetAgcTop(55.0);

        // Grabbing the slider takes manual control: slider authoritative, offset
        // cleared, auto disabled — so EFFECTIVE AGC-T (= AgcTopDb + AgcOffsetDb,
        // the value pushed to WDSP) equals the slider exactly: no offset stacking
        // (the "blast on adjust") and no loop re-target ("sits too low/high").
        Assert.Equal(55.0, snap.AgcTopDb);
        Assert.Equal(0.0, snap.AgcOffsetDb);
        Assert.False(snap.AutoAgcEnabled);
        Assert.Equal(55.0, snap.AgcTopDb + snap.AgcOffsetDb);

        // HARD SAFETY GATE (#806): once manual control is taken, NO subsequent
        // metering — spectrum floor or signalDbm, quiet or noisy, ADC pressure or
        // not — may move the effective AGC-T off the slider. Hammer the loop with
        // wildly varying inputs and assert the slider stays authoritative.
        for (int i = 0; i < 20; i++)
            radio.HandleRxMetersForAutoAgc(
                signalDbm: -50.0 - i,
                spectrumFloorDbm: -140.0 + (i * 4),
                adcPkDbfs: -3.0,
                agcGainDb: -30.0,
                nowMs: i * 500);

        var after = radio.Snapshot();
        Assert.Equal(55.0, after.AgcTopDb);
        Assert.Equal(0.0, after.AgcOffsetDb);
        Assert.False(after.AutoAgcEnabled);
        Assert.Equal(55.0, after.AgcTopDb + after.AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_ReSeedsOnAttenuatorChange()
    {
        // An operator attenuator step shifts the noise floor; the floor window
        // must re-seed (Thetis fast-attack) instead of averaging the pre-step
        // band in. Converge on a quiet floor, step the attenuator, then feed a
        // noisier floor: with the re-seed the offset jumps to the new band's
        // target (-15); WITHOUT it the stale quiet samples would still dominate
        // the percentile and hold the offset positive (+15).
        using var radio = NewRadio();
        radio.SetAgcTop(45.0);
        radio.SetAutoAgc(true);
        for (int i = 0; i < 4; i++)
            radio.HandleRxMeterForAutoAgc(-100.0, i * 500);
        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-100.0) - 45.0, radio.Snapshot().AgcOffsetDb);

        radio.SetAttenuator(new HpsdrAtten(20)); // operator step => re-seed
        for (int i = 4; i < 8; i++)
            radio.HandleRxMeterForAutoAgc(-70.0, i * 500);

        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-70.0) - 45.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_ReSeedsOnPreampChange()
    {
        // Same fast-attack contract for a preamp/LNA toggle.
        using var radio = NewRadio();
        radio.SetAgcTop(45.0);
        radio.SetAutoAgc(true);
        for (int i = 0; i < 4; i++)
            radio.HandleRxMeterForAutoAgc(-100.0, i * 500);
        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-100.0) - 45.0, radio.Snapshot().AgcOffsetDb);

        radio.SetPreamp(true); // toggle => re-seed
        for (int i = 4; i < 8; i++)
            radio.HandleRxMeterForAutoAgc(-70.0, i * 500);

        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(-70.0) - 45.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void SetAgcTop_ClampsBaselineToRange()
    {
        using var radio = NewRadio();
        // Operator baseline range is 30..90 dB (loudest 90 / quietest 30).
        Assert.Equal(90.0, radio.SetAgcTop(200.0).AgcTopDb);
        Assert.Equal(30.0, radio.SetAgcTop(-50.0).AgcTopDb);
        // Just outside each rail clamps to the rail; in-range passes through.
        Assert.Equal(90.0, radio.SetAgcTop(90.5).AgcTopDb);
        Assert.Equal(30.0, radio.SetAgcTop(29.5).AgcTopDb);
        Assert.Equal(55.0, radio.SetAgcTop(55.0).AgcTopDb);
    }

    [Fact]
    public void HydratedBaseline_ClampsLegacyAboveMaxIntoRange()
    {
        // A legacy persisted value from the old -20..120 slider must not park
        // the thumb off the new 30..90 rail on restart.
        using (var seed = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _basePath + ".dsp.db"))
        {
            seed.SetAgcTopDb(120.0);
        }
        using var radio = NewRadio(); // reopens the same dsp.db, hydrates on construct
        Assert.Equal(90.0, radio.Snapshot().AgcTopDb);
    }

    [Fact]
    public void HydratedBaseline_ClampsLegacyBelowMinIntoRange()
    {
        using (var seed = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _basePath + ".dsp.db"))
        {
            seed.SetAgcTopDb(10.0);
        }
        using var radio = NewRadio();
        Assert.Equal(30.0, radio.Snapshot().AgcTopDb);
    }

    [Fact]
    public void HydratedBaseline_PreservesInRangePersistedValue()
    {
        using (var seed = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _basePath + ".dsp.db"))
        {
            seed.SetAgcTopDb(45.0);
        }
        using var radio = NewRadio();
        Assert.Equal(45.0, radio.Snapshot().AgcTopDb);
    }

    // ── Thetis auto-AGC-T servo math (AutoAgcTopFromNoiseFloor) ───────────────
    [Fact]
    public void AutoAgcTopFromNoiseFloor_SeatsKneeAtFloor_ThetisMath()
    {
        // WDSP SetRXAAGCThresh→GetRXAAGCTop at the default RX config (filter
        // 100..2850 ⇒ BW 2750, FFT 1024, 192 kSps):
        //   noiseOffset = 10·log10(2750·1024/192000) = 11.66 dB
        //   top         = round(20·log10(out_target) − (floor + noiseOffset))
        //               = round(-0.1615 − floor − 11.66) = round(-11.83 − floor)
        // A 10 dB quieter floor ⇒ ~10 dB more gain (1:1), the Thetis contract.
        using var radio = NewRadio();
        Assert.Equal(88.0, radio.AutoAgcTopFromNoiseFloor(-100.0));
        Assert.Equal(78.0, radio.AutoAgcTopFromNoiseFloor(-90.0));
        Assert.Equal(68.0, radio.AutoAgcTopFromNoiseFloor(-80.0));
    }

    [Fact]
    public void AutoAgcTopFromNoiseFloor_AppliesThetisClamps()
    {
        using var radio = NewRadio();
        // Resulting AGC top clamps to Thetis's [-20, +120] dB. A very low floor
        // would compute > 120 dB of gain; it pegs at 120.
        Assert.Equal(120.0, radio.AutoAgcTopFromNoiseFloor(-140.0));
        // Threshold clamps to [-160, +2] dBm: floors at/above +2 dBm produce the
        // same seated top (the upper threshold rail).
        Assert.Equal(radio.AutoAgcTopFromNoiseFloor(2.0), radio.AutoAgcTopFromNoiseFloor(50.0));
    }

    // ── AGC knee removed: AGC-T is the single manual AGC control ───────────────
    [Fact]
    public void FreshRadio_HasNoAgcThreshold()
    {
        // The manual knee was removed (threshold and AGC-T are the same WDSP
        // register); the threshold is never operator-driven, so it stays null.
        using var radio = NewRadio();
        Assert.Null(radio.Snapshot().AgcThresholdDbm);
    }
}
