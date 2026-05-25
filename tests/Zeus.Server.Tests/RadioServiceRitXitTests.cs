// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Simone Fabris (IU3QEZ), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class RadioServiceRitXitTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PaSettingsStore _paStore;
    private readonly DspSettingsStore _dspStore;

    public RadioServiceRitXitTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-rit-{Guid.NewGuid():N}.db");
        _paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        _dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");
    }

    public void Dispose()
    {
        _paStore.Dispose();
        _dspStore.Dispose();
        foreach (var suffix in new[] { "", ".pa", ".dsp" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private RadioService BuildRadio() =>
        new RadioService(NullLoggerFactory.Instance, _dspStore, _paStore);

    // ---- Basic state transitions ----

    [Fact]
    public void SetIncrementalTuning_Rit_SetsMode()
    {
        using var radio = BuildRadio();
        var snap = radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        Assert.Equal(IncrementalTuningMode.Rit, snap.ItMode);
        Assert.Equal(250, snap.RitOffsetHz);
    }

    [Fact]
    public void SetIncrementalTuning_Xit_SetsMode()
    {
        using var radio = BuildRadio();
        var snap = radio.SetIncrementalTuning(IncrementalTuningMode.Xit, -300);
        Assert.Equal(IncrementalTuningMode.Xit, snap.ItMode);
        Assert.Equal(-300, snap.XitOffsetHz);
    }

    [Fact]
    public void SetIncrementalTuning_Off_PreservesOffsets()
    {
        using var radio = BuildRadio();
        radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        var snap = radio.SetIncrementalTuning(IncrementalTuningMode.Off, 0);
        Assert.Equal(IncrementalTuningMode.Off, snap.ItMode);
        Assert.Equal(250, snap.RitOffsetHz);
    }

    // ---- clear flag zeroes only the departing mode ----

    [Fact]
    public void Clear_From_Rit_ZerosRitOnly()
    {
        using var radio = BuildRadio();
        radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        radio.SetIncrementalTuning(IncrementalTuningMode.Xit, -300);
        radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        var snap = radio.SetIncrementalTuning(IncrementalTuningMode.Off, 0, clear: true);
        Assert.Equal(IncrementalTuningMode.Off, snap.ItMode);
        Assert.Equal(0, snap.RitOffsetHz);
        Assert.Equal(-300, snap.XitOffsetHz);
    }

    [Fact]
    public void Clear_From_Xit_ZerosXitOnly()
    {
        using var radio = BuildRadio();
        radio.SetIncrementalTuning(IncrementalTuningMode.Xit, -300);
        var snap = radio.SetIncrementalTuning(IncrementalTuningMode.Off, 0, clear: true);
        Assert.Equal(IncrementalTuningMode.Off, snap.ItMode);
        Assert.Equal(0, snap.XitOffsetHz);
    }

    // ---- Cycle preserves both offsets ----

    [Fact]
    public void Cycle_Rit_Xit_Off_Rit_PreservesOffsets()
    {
        using var radio = BuildRadio();
        radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        radio.SetIncrementalTuning(IncrementalTuningMode.Xit, -300);
        radio.SetIncrementalTuning(IncrementalTuningMode.Off, 0);
        var snap = radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        Assert.Equal(250, snap.RitOffsetHz);
        Assert.Equal(-300, snap.XitOffsetHz);
    }

    // ---- Clamp ----

    [Fact]
    public void SetIncrementalTuning_ClampsToMaxOffset()
    {
        using var radio = BuildRadio();
        var snap = radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 50_000);
        Assert.Equal(3000, snap.RitOffsetHz);
    }

    [Fact]
    public void SetIncrementalTuning_ClampsNegative()
    {
        using var radio = BuildRadio();
        var snap = radio.SetIncrementalTuning(IncrementalTuningMode.Xit, -50_000);
        Assert.Equal(-3000, snap.XitOffsetHz);
    }

    // ---- Auto-clear on mode change ----

    [Fact]
    public void ModeChange_ClearsIncrementalTuning()
    {
        using var radio = BuildRadio();
        radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        var snap = radio.SetMode(RxMode.CWU);
        Assert.Equal(IncrementalTuningMode.Off, snap.ItMode);
        Assert.Equal(0, snap.RitOffsetHz);
        Assert.Equal(0, snap.XitOffsetHz);
    }

    // ---- Auto-clear on band change (via SetVfo crossing band edge) ----

    [Fact]
    public void BandChange_ClearsIncrementalTuning()
    {
        using var radio = BuildRadio();
        radio.SetVfo(14_074_000); // 20m
        radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        var snap = radio.SetVfo(7_074_000); // 40m — band changed
        Assert.Equal(IncrementalTuningMode.Off, snap.ItMode);
        Assert.Equal(0, snap.RitOffsetHz);
        Assert.Equal(0, snap.XitOffsetHz);
    }

    [Fact]
    public void SameBandTune_PreservesIncrementalTuning()
    {
        using var radio = BuildRadio();
        radio.SetVfo(14_074_000);
        radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        var snap = radio.SetVfo(14_076_000); // same band
        Assert.Equal(IncrementalTuningMode.Rit, snap.ItMode);
        Assert.Equal(250, snap.RitOffsetHz);
    }

    // ---- Auto-clear on disconnect ----

    [Fact]
    public async Task Disconnect_ClearsIncrementalTuning()
    {
        using var radio = BuildRadio();
        radio.SetIncrementalTuning(IncrementalTuningMode.Rit, 250);
        var snap = await radio.DisconnectAsync();
        Assert.Equal(IncrementalTuningMode.Off, snap.ItMode);
        Assert.Equal(0, snap.RitOffsetHz);
        Assert.Equal(0, snap.XitOffsetHz);
    }
}
