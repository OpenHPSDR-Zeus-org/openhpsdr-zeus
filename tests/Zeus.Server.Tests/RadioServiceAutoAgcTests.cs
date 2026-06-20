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
    public void AutoAgc_WaitsForFullNoiseWindow()
    {
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
    public void AutoAgc_SlewsOffsetHalfDbPerTick()
    {
        using var radio = NewRadio();
        radio.SetAgcTop(45.0);
        radio.SetAutoAgc(true);

        for (int i = 0; i < 12; i++)
            radio.HandleRxMeterForAutoAgc(-100.0, i * 500);

        Assert.Equal(0.5, radio.Snapshot().AgcOffsetDb);

        radio.HandleRxMeterForAutoAgc(-100.0, 12 * 500);

        Assert.Equal(1.0, radio.Snapshot().AgcOffsetDb);
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

        var snap = radio.Snapshot();
        Assert.Equal(60.0, snap.AgcTopDb);
        Assert.Equal(-0.5, snap.AgcOffsetDb);
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

        for (int i = 0; i < 4; i++)
            radio.HandleRxMetersForAutoAgc(signalDbm: -72.0, adcPkDbfs: -5.0, agcGainDb: -28.0, nowMs: i * 500);

        Assert.Equal(-2.0, radio.Snapshot().AgcOffsetDb);

        radio.HandleRxMetersForAutoAgc(signalDbm: -92.0, adcPkDbfs: -18.0, agcGainDb: 0.0, nowMs: 2_000);

        Assert.Equal(-1.5, radio.Snapshot().AgcOffsetDb);
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
        Assert.Equal(120.0, radio.SetAgcTop(200.0).AgcTopDb);
        Assert.Equal(-20.0, radio.SetAgcTop(-50.0).AgcTopDb);
    }
}
