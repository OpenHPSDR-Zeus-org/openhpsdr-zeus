// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Server-authoritative per-band mode recall (KB2UKA 2026-06-25). When the
/// operator's dial crosses into a different band, <see cref="RadioService.SetVfo"/>
/// restores that band's last-used demod mode from <see cref="BandMemoryStore"/>
/// so mode follows the band no matter how the band changed — the band-selector
/// UIs already did this; this closes the gap for the VFO knob / typed frequency.
///
/// Invariants pinned here:
///   1. Crossing into a band with a remembered mode recalls it.
///   2. A band with no remembered entry leaves the current mode untouched
///      (first visit → the current mode simply carries over).
///   3. Tuning within the same band never recalls.
///   4. External sources (CAT/TCI/calibration, fromExternal=true) keep their
///      own mode — no recall.
///   5. The full back-and-forth: each band remembers its own mode independently.
///   6. With no BandMemoryStore wired, SetVfo is unchanged (recall no-ops).
/// </summary>
public sealed class RadioServicePerBandModeTests : IDisposable
{
    // Server BandUtils.FreqToBand ranges — pick a comfortably in-band frequency
    // per band so the key matches the frontend's "40m"/"20m"/"17m" save key.
    private const long Hz40m = 7_100_000;   // FreqToBand -> "40m"
    private const long Hz20m = 14_200_000;  // FreqToBand -> "20m"
    private const long Hz17m = 18_100_000;  // FreqToBand -> "17m" (no seeded entry)

    private readonly string _dbPath;
    private readonly PaSettingsStore _paStore;
    private readonly DspSettingsStore _dspStore;
    private readonly BandMemoryStore _bandMemory;

    public RadioServicePerBandModeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-bandmode-{Guid.NewGuid():N}.db");
        _paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        _dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");
        _bandMemory = new BandMemoryStore(NullLogger<BandMemoryStore>.Instance, _dbPath + ".bm");
    }

    public void Dispose()
    {
        _paStore.Dispose();
        _dspStore.Dispose();
        _bandMemory.Dispose();
        foreach (var suffix in new[] { "", ".pa", ".dsp", ".bm" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private RadioService BuildRadio(BandMemoryStore? bandMemory) =>
        new RadioService(
            NullLoggerFactory.Instance, _dspStore, _paStore, bandMemoryStore: bandMemory);

    // Land the radio on a known band/mode with the store still empty, so the
    // positioning tune itself never recalls. Seed memory afterwards.
    private RadioService LandOn40mLsb(BandMemoryStore? bandMemory)
    {
        var radio = BuildRadio(bandMemory);
        radio.SetMode(RxMode.LSB);
        radio.SetVfo(Hz40m);
        Assert.Equal(RxMode.LSB, radio.Snapshot().Mode);
        return radio;
    }

    [Fact]
    public void CrossingIntoBand_WithRememberedMode_RecallsIt()
    {
        using var radio = LandOn40mLsb(_bandMemory);
        _bandMemory.Upsert("20m", Hz20m, RxMode.USB);

        var after = radio.SetVfo(Hz20m);

        Assert.Equal(Hz20m, after.VfoHz);
        Assert.Equal(RxMode.USB, after.Mode);
    }

    [Fact]
    public void CrossingIntoBand_WithNoEntry_CarriesModeOver()
    {
        using var radio = LandOn40mLsb(_bandMemory);
        // 17m has no seeded entry — the current mode must simply carry over.
        var after = radio.SetVfo(Hz17m);

        Assert.Equal(Hz17m, after.VfoHz);
        Assert.Equal(RxMode.LSB, after.Mode);
    }

    [Fact]
    public void TuningWithinSameBand_NeverRecalls()
    {
        using var radio = LandOn40mLsb(_bandMemory);
        // Memory disagrees with the live mode, but no band edge is crossed, so
        // the recall must not fire.
        _bandMemory.Upsert("40m", Hz40m, RxMode.USB);

        var after = radio.SetVfo(Hz40m + 50_000);

        Assert.Equal(RxMode.LSB, after.Mode);
    }

    [Fact]
    public void ExternalSource_DoesNotRecall()
    {
        using var radio = LandOn40mLsb(_bandMemory);
        _bandMemory.Upsert("20m", Hz20m, RxMode.USB);

        // CAT/TCI manage their own mode — a recall here would fight them.
        var after = radio.SetVfo(Hz20m, fromExternal: true);

        Assert.Equal(Hz20m, after.VfoHz);
        Assert.Equal(RxMode.LSB, after.Mode);
    }

    [Fact]
    public void BackAndForth_RemembersEachBandIndependently()
    {
        // The exact KB2UKA scenario: LSB on 40m, USB on 20m, hop back and forth.
        using var radio = LandOn40mLsb(_bandMemory);
        _bandMemory.Upsert("40m", Hz40m, RxMode.LSB);
        _bandMemory.Upsert("20m", Hz20m, RxMode.USB);

        Assert.Equal(RxMode.USB, radio.SetVfo(Hz20m).Mode);
        Assert.Equal(RxMode.LSB, radio.SetVfo(Hz40m).Mode);
        Assert.Equal(RxMode.USB, radio.SetVfo(Hz20m).Mode);
        Assert.Equal(RxMode.LSB, radio.SetVfo(Hz40m).Mode);
    }

    [Fact]
    public void NoBandMemoryStore_LeavesSetVfoUnchanged()
    {
        // Mirrors every existing RadioService test that constructs without a
        // BandMemoryStore: recall must no-op and mode must not change.
        using var radio = LandOn40mLsb(bandMemory: null);

        var after = radio.SetVfo(Hz20m);

        Assert.Equal(Hz20m, after.VfoHz);
        Assert.Equal(RxMode.LSB, after.Mode);
    }
}
