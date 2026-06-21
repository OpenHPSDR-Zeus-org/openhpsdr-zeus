// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Persistence coverage for the AGC mode + custom params store (Phase 1 of
// the DSP controls Thetis-parity work). All six nullable AGC fields on
// DspSettingsEntry must round-trip through LiteDB so the operator's AGC
// configuration survives a backend restart.
//
// Three contracts exercised:
//   1. Full round-trip: SetAgc → reopen store → GetAgc returns equal config.
//   2. Null custom params: a Custom config with all params null round-trips
//      as nulls (null = "use engine default" lazy contract).
//   3. Fresh/legacy entry: GetAgc on a store with no AGC row returns null so
//      RadioService can fall back to the Med default on first run.
//
// Pattern mirrors Nr4SettingsPersistenceTests — per-fixture temp DB so xUnit
// class-level serialization (AssemblyAttributes.cs) can't collide on the
// shared zeus-prefs.db.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public class AgcSettingsPersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-agc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private DspSettingsStore BuildStore() =>
        new(NullLogger<DspSettingsStore>.Instance, _dbPath);

    // --- 1. Full round-trip with all scalar params populated ---------------

    [Fact]
    public void SetAgc_FullConfig_RoundTripsAllFields()
    {
        var cfg = new AgcConfig(
            Mode: AgcMode.Custom,
            Slope: 7,
            DecayMs: 1500,
            HangMs: 800,
            HangThreshold: 50,
            FixedGainDb: null);

        using (var store = BuildStore())
            store.SetAgc(cfg);

        // Reopen to prove persistence, not just in-memory cache.
        using var fresh = BuildStore();
        var back = fresh.GetAgc();
        Assert.NotNull(back);
        Assert.Equal(AgcMode.Custom, back!.Mode);
        Assert.Equal(7,      back.Slope);
        Assert.Equal(1500,   back.DecayMs);
        Assert.Equal(800,    back.HangMs);
        Assert.Equal(50,     back.HangThreshold);
        Assert.Null(back.FixedGainDb);
    }

    [Fact]
    public void SetAgc_FixedMode_RoundTripsFixedGainDb()
    {
        var cfg = new AgcConfig(
            Mode: AgcMode.Fixed,
            FixedGainDb: 35.0);

        using (var store = BuildStore())
            store.SetAgc(cfg);

        using var fresh = BuildStore();
        var back = fresh.GetAgc();
        Assert.NotNull(back);
        Assert.Equal(AgcMode.Fixed, back!.Mode);
        Assert.Equal(35.0, back.FixedGainDb);
    }

    [Fact]
    public void SetAgc_CannedMode_RoundTripsMode()
    {
        var cfg = new AgcConfig(Mode: AgcMode.Slow);

        using (var store = BuildStore())
            store.SetAgc(cfg);

        using var fresh = BuildStore();
        var back = fresh.GetAgc();
        Assert.NotNull(back);
        Assert.Equal(AgcMode.Slow, back!.Mode);
    }

    // --- 2. Null custom params stay null (lazy-default contract) -----------

    [Fact]
    public void SetAgc_NullCustomParams_RoundTripsAsNull()
    {
        // A Custom config with all optional params null — null means "engine
        // falls back to canned defaults at apply time". The store must not
        // substitute zeros or any other sentinel.
        var cfg = new AgcConfig(
            Mode: AgcMode.Custom,
            Slope: null,
            DecayMs: null,
            HangMs: null,
            HangThreshold: null,
            FixedGainDb: null);

        using (var store = BuildStore())
            store.SetAgc(cfg);

        using var fresh = BuildStore();
        var back = fresh.GetAgc();
        Assert.NotNull(back);
        Assert.Equal(AgcMode.Custom, back!.Mode);
        Assert.Null(back.Slope);
        Assert.Null(back.DecayMs);
        Assert.Null(back.HangMs);
        Assert.Null(back.HangThreshold);
        Assert.Null(back.FixedGainDb);
    }

    // --- 3. Fresh / legacy entry: GetAgc returns null ----------------------

    [Fact]
    public void GetAgc_FreshStore_ReturnsNull()
    {
        // No AGC row ever written — RadioService must fall back to Med default.
        using var store = BuildStore();
        Assert.Null(store.GetAgc());
    }

    [Fact]
    public void GetAgc_EntryExistsButNoAgcFields_ReturnsNull()
    {
        // A legacy DB row (written by NR upsert before AGC persistence landed)
        // has AgcMode == null — GetAgc must return null, not a default config,
        // so RadioService keeps its Med baseline rather than applying a zeroed
        // config.
        using (var store = BuildStore())
            store.Upsert(new NrConfig(NrMode: NrMode.Off));

        using var fresh = BuildStore();
        Assert.Null(fresh.GetAgc());
    }

    // --- 4. Upsert overwrites existing AGC row -----------------------------

    [Fact]
    public void SetAgc_UpsertOverwritesExistingConfig()
    {
        var first  = new AgcConfig(Mode: AgcMode.Fast);
        var second = new AgcConfig(Mode: AgcMode.Long, HangMs: 2000, DecayMs: 2000);

        using var store = BuildStore();
        store.SetAgc(first);
        store.SetAgc(second);

        var back = store.GetAgc();
        Assert.NotNull(back);
        Assert.Equal(AgcMode.Long, back!.Mode);
        Assert.Equal(2000, back.HangMs);
        Assert.Equal(2000, back.DecayMs);
    }
}
