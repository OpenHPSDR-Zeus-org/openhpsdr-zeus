// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Persistence coverage for the TX leveling store (Phase 3 of the DSP controls
// Thetis-parity work). The nullable TX-leveling fields on DspSettingsEntry must
// round-trip through LiteDB so the operator's ALC/Leveler/Compressor config
// survives a backend restart.
//
// Contracts exercised:
//   1. Full round-trip: SetTxLeveling → reopen store → GetTxLeveling returns
//      equal config.
//   2. Default round-trip: the TxLevelingConfig defaults round-trip faithfully.
//   3. Fresh/legacy entry: GetTxLeveling on a store with no leveling row returns
//      null so RadioService can fall back to the defaults on first run.
//   4. Upsert overwrites an existing leveling row.
//
// Pattern mirrors AgcSettingsPersistenceTests / SquelchSettingsPersistenceTests
// — per-fixture temp DB so xUnit class-level serialization can't collide on the
// shared zeus-prefs.db.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public class TxLevelingSettingsPersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-txleveling-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DspSettingsStore BuildStore() =>
        new(NullLogger<DspSettingsStore>.Instance, _dbPath);

    // --- 1. Full round-trip with all fields populated ---------------------

    [Fact]
    public void SetTxLeveling_FullConfig_RoundTripsAllFields()
    {
        var cfg = new TxLevelingConfig(
            AlcMaxGainDb: 7.5,
            AlcDecayMs: 25,
            LevelerEnabled: false,
            LevelerDecayMs: 450,
            CompressorEnabled: true,
            CompressorGainDb: 12.0);

        using (var store = BuildStore())
            store.SetTxLeveling(cfg);

        // Reopen to prove persistence, not just in-memory cache.
        using var fresh = BuildStore();
        var back = fresh.GetTxLeveling();
        Assert.NotNull(back);
        Assert.Equal(7.5, back!.AlcMaxGainDb);
        Assert.Equal(25, back.AlcDecayMs);
        Assert.False(back.LevelerEnabled);
        Assert.Equal(450, back.LevelerDecayMs);
        Assert.True(back.CompressorEnabled);
        Assert.Equal(12.0, back.CompressorGainDb);
    }

    // --- 2. Defaults round-trip faithfully -------------------------------

    [Fact]
    public void SetTxLeveling_Defaults_RoundTrips()
    {
        var cfg = new TxLevelingConfig();

        using (var store = BuildStore())
            store.SetTxLeveling(cfg);

        using var fresh = BuildStore();
        var back = fresh.GetTxLeveling();
        Assert.NotNull(back);
        Assert.Equal(3.0, back!.AlcMaxGainDb);
        Assert.Equal(10, back.AlcDecayMs);
        Assert.True(back.LevelerEnabled);
        Assert.Equal(100, back.LevelerDecayMs);
        Assert.False(back.CompressorEnabled);
        Assert.Equal(0.0, back.CompressorGainDb);
    }

    // --- 3. Fresh / legacy entry: GetTxLeveling returns null --------------

    [Fact]
    public void GetTxLeveling_FreshStore_ReturnsNull()
    {
        // No leveling row ever written — RadioService falls back to defaults.
        using var store = BuildStore();
        Assert.Null(store.GetTxLeveling());
    }

    [Fact]
    public void GetTxLeveling_EntryExistsButNoLevelingFields_ReturnsNull()
    {
        // A legacy DB row (written by NR upsert before TX-leveling persistence
        // landed) has TxLevelingSet == null — GetTxLeveling must return null, not
        // a default config, so RadioService keeps its defaults baseline.
        using (var store = BuildStore())
            store.Upsert(new NrConfig(NrMode: NrMode.Off));

        using var fresh = BuildStore();
        Assert.Null(fresh.GetTxLeveling());
    }

    // --- 4. Upsert overwrites existing leveling row ----------------------

    [Fact]
    public void SetTxLeveling_UpsertOverwritesExistingConfig()
    {
        var first = new TxLevelingConfig(AlcMaxGainDb: 5.0, CompressorEnabled: false);
        var second = new TxLevelingConfig(AlcMaxGainDb: 9.0, CompressorEnabled: true, CompressorGainDb: 6.0);

        using var store = BuildStore();
        store.SetTxLeveling(first);
        store.SetTxLeveling(second);

        var back = store.GetTxLeveling();
        Assert.NotNull(back);
        Assert.Equal(9.0, back!.AlcMaxGainDb);
        Assert.True(back.CompressorEnabled);
        Assert.Equal(6.0, back.CompressorGainDb);
    }
}
