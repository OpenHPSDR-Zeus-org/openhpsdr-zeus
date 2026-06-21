// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
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

        Assert.Equal(80.0, radio.Snapshot().AgcTopDb);
    }

    [Fact]
    public void AutoAgc_NoBoostWhenBaselineAlreadyAboveTarget()
    {
        // Default baseline AgcTopDb is 80; a -105 dBm floor wants effective ~65,
        // which is BELOW the baseline, so the noise-floor path never boosts
        // (offset stays 0 — Auto-AGC only adds gain, never lowers the baseline).
        using var radio = NewRadio();
        radio.SetAutoAgc(true);

        for (int i = 0; i < 11; i++)
            radio.HandleRxMeterForAutoAgc(-105.0, i * 500);

        Assert.Equal(0.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_DoesNotRaiseGainOnSingleQuietDip()
    {
        using var radio = NewRadio();
        radio.SetAgcTop(60.0);
        radio.SetAutoAgc(true);

        var samples = new[] { -120.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0, -90.0 };
        for (int i = 0; i < samples.Length; i++)
            radio.HandleRxMeterForAutoAgc(samples[i], i * 500);

        Assert.True(radio.Snapshot().AgcOffsetDb <= 0.0);
    }

    [Fact]
    public void AutoAgc_JumpsToNoiseFloorTarget_FastNotSlewed()
    {
        // baseline 45; a -100 dBm floor wants effective = clamp(-40-(-100),20,100)
        // = 60, so the offset target is 60-45 = 15 dB. The loop JUMPS to it once
        // it has a few samples (~1.5 s) — it does NOT crawl there 0.5 dB/tick.
        using var radio = NewRadio();
        radio.SetAgcTop(45.0);
        radio.SetAutoAgc(true);

        // Just past the min-sample warm-up: by now a slew would still be only a
        // couple dB in; the jump is already at the full target.
        for (int i = 0; i < 4; i++)
            radio.HandleRxMeterForAutoAgc(-100.0, i * 500);

        Assert.Equal(15.0, radio.Snapshot().AgcOffsetDb);
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
        Assert.Equal(15.0, radio.Snapshot().AgcOffsetDb); // quiet band: +15

        radio.SetVfo(7_100_000); // > 0.5 MHz move => fast-attack re-seed
        for (int i = 4; i < 8; i++)
            radio.HandleRxMeterForAutoAgc(-70.0, i * 500); // noisier band

        // New floor -70 -> effective clamp(-40-(-70),20,100)=30 -> offset 30-45<0
        // -> 0. It re-seeded to the new band and dropped the boost, fast.
        Assert.Equal(0.0, radio.Snapshot().AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_DoesNotPullEffectiveGainBelowUserBaseline()
    {
        using var radio = NewRadio();
        radio.SetAgcTop(80.0);
        radio.SetAutoAgc(true);

        for (int i = 0; i < 12; i++)
            radio.HandleRxMeterForAutoAgc(-90.0, i * 500);

        var snap = radio.Snapshot();
        Assert.Equal(80.0, snap.AgcTopDb);
        Assert.Equal(0.0, snap.AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_LowersEffectiveGainWhenWdspAgcIsCuttingAndAdcIsNearFullScale()
    {
        using var radio = NewRadio();
        radio.SetAgcTop(60.0);
        radio.SetAutoAgc(true);

        radio.HandleRxMetersForAutoAgc(signalDbm: -72.0, adcPkDbfs: -5.0, agcGainDb: -28.0, nowMs: 0);

        // ADC overload protection now JUMPS the cut (fast protection) instead of
        // crawling -0.5 dB/tick: effective target = clamp(60 + (-28-(-8)) - 1, 20,
        // 100) = 39, so the offset snaps to 39-60 = -21 on the first event.
        var snap = radio.Snapshot();
        Assert.Equal(60.0, snap.AgcTopDb);
        Assert.Equal(-21.0, snap.AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_DoesNotLowerEffectiveGainWhenWdspAgcCutsWithCleanAdcHeadroom()
    {
        using var radio = NewRadio();
        radio.SetAgcTop(52.0);
        radio.SetAutoAgc(true);

        for (int i = 0; i < 20; i++)
            radio.HandleRxMetersForAutoAgc(signalDbm: -75.0, adcPkDbfs: -60.0, agcGainDb: -24.5, nowMs: i * 500);

        var snap = radio.Snapshot();
        Assert.Equal(52.0, snap.AgcTopDb);
        Assert.Equal(0.0, snap.AgcOffsetDb);
    }

    [Fact]
    public void AutoAgc_RecoversNegativeOffsetWhenAgcCutClears()
    {
        using var radio = NewRadio();
        radio.SetAgcTop(60.0);
        radio.SetAutoAgc(true);

        // Sustained overload drives the protective cut negative (jumps fast).
        for (int i = 0; i < 4; i++)
            radio.HandleRxMetersForAutoAgc(signalDbm: -72.0, adcPkDbfs: -5.0, agcGainDb: -28.0, nowMs: i * 500);

        Assert.True(radio.Snapshot().AgcOffsetDb < 0.0, "overload should pull the offset negative");

        // ADC pressure clears and WDSP is no longer cutting: the offset recovers
        // (jumps) back to the noise-floor target — here 0 (baseline already high
        // enough for the -92 dBm floor).
        radio.HandleRxMetersForAutoAgc(signalDbm: -92.0, adcPkDbfs: -18.0, agcGainDb: 0.0, nowMs: 2_000);

        Assert.Equal(0.0, radio.Snapshot().AgcOffsetDb);
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
    }

    [Fact]
    public void SetAgcTop_ClampsBaselineToRange()
    {
        using var radio = NewRadio();
        // Operator baseline range is 30..80 dB (loudest 80 / quietest 30).
        Assert.Equal(80.0, radio.SetAgcTop(200.0).AgcTopDb);
        Assert.Equal(30.0, radio.SetAgcTop(-50.0).AgcTopDb);
        // Just outside each rail clamps to the rail; in-range passes through.
        Assert.Equal(80.0, radio.SetAgcTop(80.5).AgcTopDb);
        Assert.Equal(30.0, radio.SetAgcTop(29.5).AgcTopDb);
        Assert.Equal(55.0, radio.SetAgcTop(55.0).AgcTopDb);
    }

    [Fact]
    public void HydratedBaseline_ClampsLegacyAboveMaxIntoRange()
    {
        // A legacy persisted value from the old -20..120 slider must not park
        // the thumb off the new 30..80 rail on restart.
        using (var seed = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _basePath + ".dsp.db"))
        {
            seed.SetAgcTopDb(120.0);
        }
        using var radio = NewRadio(); // reopens the same dsp.db, hydrates on construct
        Assert.Equal(80.0, radio.Snapshot().AgcTopDb);
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
