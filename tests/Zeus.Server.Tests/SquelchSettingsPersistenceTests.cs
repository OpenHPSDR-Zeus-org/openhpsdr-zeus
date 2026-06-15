// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Persistence coverage for the RX squelch store (Phase 2 of the DSP controls
// Thetis-parity work). Nullable squelch fields on DspSettingsEntry must
// round-trip through LiteDB so the operator's squelch configuration survives a
// backend restart.
//
// Contracts exercised:
//   1. Full round-trip: SetSquelch → reopen store → GetSquelch returns equal config.
//   2. Default-off round-trip: an off/level-0 config round-trips faithfully.
//   3. Fresh/legacy entry: GetSquelch on a store with no squelch row returns
//      null so RadioService can fall back to the off default on first run.
//   4. Upsert overwrites an existing squelch row.
//
// Pattern mirrors AgcSettingsPersistenceTests — per-fixture temp DB so xUnit
// class-level serialization can't collide on the shared zeus-prefs.db.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public class SquelchSettingsPersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-squelch-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DspSettingsStore BuildStore() =>
        new(NullLogger<DspSettingsStore>.Instance, _dbPath);

    // --- 1. Full round-trip with enabled + level populated -----------------

    [Fact]
    public void SetSquelch_EnabledConfig_RoundTripsAllFields()
    {
        var cfg = new SquelchConfig(Enabled: true, Level: 42, Adaptive: false, FixedSensitivity: 88);

        using (var store = BuildStore())
            store.SetSquelch(cfg);

        // Reopen to prove persistence, not just in-memory cache.
        using var fresh = BuildStore();
        var back = fresh.GetSquelch();
        Assert.NotNull(back);
        Assert.True(back!.Enabled);
        Assert.Equal(42, back.Level);
        Assert.False(back.Adaptive);
        Assert.Equal(88, back.FixedSensitivity);
    }

    // --- 2. Default off (level 0) round-trips faithfully -------------------

    [Fact]
    public void SetSquelch_DefaultOff_RoundTrips()
    {
        var cfg = new SquelchConfig(Enabled: false, Level: 0);

        using (var store = BuildStore())
            store.SetSquelch(cfg);

        using var fresh = BuildStore();
        var back = fresh.GetSquelch();
        Assert.NotNull(back);
        Assert.False(back!.Enabled);
        Assert.Equal(0, back.Level);
        Assert.True(back.Adaptive);
        Assert.Equal(SquelchConfig.DefaultFixedSensitivity, back.FixedSensitivity);
    }

    // --- 3. Fresh / legacy entry: GetSquelch returns null -----------------

    [Fact]
    public void GetSquelch_FreshStore_ReturnsNull()
    {
        // No squelch row ever written — RadioService falls back to off default.
        using var store = BuildStore();
        Assert.Null(store.GetSquelch());
    }

    [Fact]
    public void GetSquelch_EntryExistsButNoSquelchFields_ReturnsNull()
    {
        // A legacy DB row (written by NR upsert before squelch persistence
        // landed) has SquelchEnabled == null — GetSquelch must return null, not
        // a default config, so RadioService keeps its off baseline.
        using (var store = BuildStore())
            store.Upsert(new NrConfig(NrMode: NrMode.Off));

        using var fresh = BuildStore();
        Assert.Null(fresh.GetSquelch());
    }

    // --- 4. Upsert overwrites existing squelch row ------------------------

    [Fact]
    public void SetSquelch_UpsertOverwritesExistingConfig()
    {
        var first  = new SquelchConfig(Enabled: true, Level: 10);
        var second = new SquelchConfig(Enabled: true, Level: 75, Adaptive: false, FixedSensitivity: 35);

        using var store = BuildStore();
        store.SetSquelch(first);
        store.SetSquelch(second);

        var back = store.GetSquelch();
        Assert.NotNull(back);
        Assert.True(back!.Enabled);
        Assert.Equal(75, back.Level);
        Assert.False(back.Adaptive);
        Assert.Equal(35, back.FixedSensitivity);
    }
}
