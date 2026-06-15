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
}
