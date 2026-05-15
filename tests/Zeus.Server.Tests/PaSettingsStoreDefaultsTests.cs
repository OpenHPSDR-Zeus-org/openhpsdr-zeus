// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Defaults have to be correct on first connect — operator sees them before
// any calibration. Pin the per-board seeds so a "cosmetic" refactor in
// PaDefaults doesn't silently flip HL2 back to the old piHPSDR 40.5 dB
// mis-interpretation (see docs/lessons/hl2-drive-model.md).
//
// These tests use a per-class temp DB to stay hermetic — pre-isolation they
// shared zeus-prefs.db with production and any operator APPLY would break
// GetAll-based defaults tests by returning stored values ahead of
// PaDefaults.
public class PaSettingsStoreDefaultsTests : IDisposable
{
    private readonly string _dbPath;

    public PaSettingsStoreDefaultsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-pasettings-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private PaSettingsStore NewStore() =>
        new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath);

    // HL2 uses a PERCENTAGE model (mi0bot openhpsdr-thetis), not the dB
    // model every other HPSDR radio uses. PaGainDb on HL2 is output % 0..100
    // — HF bands default to 100 (no attenuation), 6 m drops to 38.8
    // (stock-PA-gain-limited; matches clsHardwareSpecific.cs:767-795 in the
    // mi0bot fork).
    [Fact]
    public void Hl2_Default_Is_100_Percent_On_HF_And_38_8_On_6m()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.HermesLite2);

        Assert.Equal(BandUtils.HfBands.Count, s.Bands.Count);
        foreach (var b in s.Bands.Where(x => x.Band != "6m"))
        {
            Assert.Equal(100.0, b.PaGainDb);
        }
        Assert.Equal(38.8, FindGain(s, "6m"));
    }

    [Fact]
    public void Hermes_Defaults_Match_Thetis_Table()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.Hermes);
        // Spot-check against Thetis clsHardwareSpecific.cs:482-513.
        Assert.Equal(41.0, FindGain(s, "160m"));
        Assert.Equal(40.5, FindGain(s, "20m"));
        Assert.Equal(38.8, FindGain(s, "10m"));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.Metis)]
    public void HermesClass_MaxPowerWatts_Matches_GainBracket_Assumption(HpsdrBoardKind board)
    {
        // The HermesGains bracket (HermesGains["10m"] = 38.8 dB) was lifted
        // from Thetis setup.cs:482-544, which calibrates for a 100 W output
        // target. Thetis's drive slider is 0..100 *watts*, so slider=100 →
        // 100 W target → byte=255, regardless of the radio's rated max (the
        // radio self-clamps). Zeus's slider is a percent of MaxWatts, so
        // MaxWatts must match the bracket assumption (100), not the rated
        // output, or 100 % drive asks the DAC for 10× too little and a
        // physically-10 W radio (ANAN-10 / ANAN-10E / Brick2) makes ~1 W at
        // max TUNE. Regression pin for the issue.
        using var store = NewStore();
        var d = store.GetDefaults(board);
        Assert.Equal(100, d.Global.PaMaxPowerWatts);
        Assert.Equal(38.8, FindGain(d, "10m"));
    }

    [Fact]
    public void OrionMkII_Uses_G2_Class_Defaults()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.OrionMkII);
        // ANAN7000/G1/G2/ANVELINAPRO3 bracket — Thetis clsHardwareSpecific.cs:696-728.
        Assert.Equal(47.9, FindGain(s, "160m"));
        Assert.Equal(50.9, FindGain(s, "20m"));
        Assert.Equal(44.6, FindGain(s, "6m"));
    }

    [Fact]
    public void HermesC10_Uses_G2_Class_Defaults()
    {
        // ANAN-G2E (Thetis HPSDRHW.HermesC10) shares the Saturn / G2 PA-gain
        // bracket per clsHardwareSpecific.cs:698-730 — bundled with
        // ANAN-7000D / ANAN-G2 / ANVELINAPRO3 / Red Pitaya. Same constants as
        // OrionMkII_Uses_G2_Class_Defaults; this test pins the dispatch.
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.HermesC10);
        Assert.Equal(47.9, FindGain(s, "160m"));
        Assert.Equal(50.9, FindGain(s, "20m"));
        Assert.Equal(44.6, FindGain(s, "6m"));
    }

    [Fact]
    public void GetDefaults_HermesC10_Uses_G2_Table_And_100W()
    {
        using var store = NewStore();
        var d = store.GetDefaults(HpsdrBoardKind.HermesC10);
        Assert.Equal(100, d.Global.PaMaxPowerWatts);
        Assert.Equal(47.9, FindGain(d, "160m"));
        Assert.Equal(50.9, FindGain(d, "20m"));
    }

    [Fact]
    public void Unknown_Board_Returns_Zero_Gain_For_Legacy_Path()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.Unknown);
        // 0 dB combined with maxW=0 in ComputeDriveByte short-circuits to the
        // pct×255/100 legacy mapping — first boot behaves as before PA Settings.
        foreach (var b in s.Bands)
        {
            Assert.Equal(0.0, b.PaGainDb);
        }
    }

    [Fact]
    public void GetAll_Returns_All_11_Hf_Bands_In_Canonical_Order()
    {
        using var store = NewStore();
        var s = store.GetAll(HpsdrBoardKind.HermesLite2);
        Assert.Equal(BandUtils.HfBands.ToArray(), s.Bands.Select(b => b.Band).ToArray());
    }

    [Fact]
    public void GetDefaults_Ignores_Stored_Calibration()
    {
        // Reset-to-defaults must stomp any saved per-band tweak. Asking for
        // pure HL2 defaults returns 100 % on HF / 38.8 % on 6 m regardless
        // of whatever is stored — percentage model, not dB (see
        // HermesLite2DriveProfile and docs/lessons/hl2-drive-model.md).
        using var store = NewStore();
        var d = store.GetDefaults(HpsdrBoardKind.HermesLite2);
        Assert.Equal(5, d.Global.PaMaxPowerWatts);
        Assert.True(d.Global.PaEnabled);
        foreach (var b in d.Bands.Where(x => x.Band != "6m"))
        {
            Assert.Equal(100.0, b.PaGainDb);
        }
        Assert.Equal(38.8, FindGain(d, "6m"));
    }

    [Fact]
    public void GetDefaults_OrionMkII_Uses_G2_Table()
    {
        using var store = NewStore();
        var d = store.GetDefaults(HpsdrBoardKind.OrionMkII);
        Assert.Equal(100, d.Global.PaMaxPowerWatts);
        Assert.Equal(47.9, FindGain(d, "160m"));
        Assert.Equal(50.9, FindGain(d, "20m"));
    }

    [Fact]
    public void OrionMkII_Variant_Anan8000DLE_Uses_Anan100_Gains_And_200W()
    {
        // Issue #218: ANAN-8000DLE has its own PA-gain bracket per Thetis
        // clsHardwareSpecific.cs:668-694 (50.0 dB 160 m, 50.5 dB 80 m,
        // 47.5 dB 15 m, 42.0 dB 10 m). Same numbers as the Anan100 bracket.
        // Rated 200 W.
        using var store = NewStore();
        var d = store.GetDefaults(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.Anan8000DLE);
        Assert.Equal(200, d.Global.PaMaxPowerWatts);
        Assert.Equal(50.0, FindGain(d, "160m"));
        Assert.Equal(48.5, FindGain(d, "20m"));
        Assert.Equal(42.0, FindGain(d, "10m"));
    }

    [Fact]
    public void OrionMkII_Variant_OrionMkIIOriginal_Uses_Hermes_Gains_And_100W()
    {
        // Apache OrionMkII original (Orion-MkII firmware) maps to the
        // Hermes-class PA-gain bracket per Thetis clsHardwareSpecific.cs:484
        // (41.0 dB 160 m, 38.8 dB 10 m). 100 W rated.
        using var store = NewStore();
        var d = store.GetDefaults(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.OrionMkII);
        Assert.Equal(100, d.Global.PaMaxPowerWatts);
        Assert.Equal(41.0, FindGain(d, "160m"));
        Assert.Equal(38.8, FindGain(d, "10m"));
    }

    [Fact]
    public void OrionMkII_Variant_G2_1K_Uses_G2_Gains_And_1000W()
    {
        // G2-1K shares G2's PA-gain table per Thetis line 732-758 (same
        // numbers as G2 / 7000DLE); rated watts 1000.
        using var store = NewStore();
        var d = store.GetDefaults(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2_1K);
        Assert.Equal(1000, d.Global.PaMaxPowerWatts);
        Assert.Equal(47.9, FindGain(d, "160m"));
        Assert.Equal(50.9, FindGain(d, "20m"));
    }

    [Fact]
    public void OrionMkII_Variant_G2_Default_Matches_PreIssue218_Behaviour()
    {
        // Default variant must dispatch identically to the no-variant
        // overload — operators who never touch the setting see no change.
        using var store1 = NewStore();
        var defaults = store1.GetDefaults(HpsdrBoardKind.OrionMkII);

        using var store2 = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"zeus-prefs-pasettings-default-{Guid.NewGuid():N}.db"));
        var explicitG2 = store2.GetDefaults(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2);

        Assert.Equal(defaults.Global.PaMaxPowerWatts, explicitG2.Global.PaMaxPowerWatts);
        Assert.Equal(
            defaults.Bands.Select(b => b.PaGainDb).ToArray(),
            explicitG2.Bands.Select(b => b.PaGainDb).ToArray());
    }

    [Theory]
    [MemberData(nameof(BoardsWithDefaults))]
    public void Every_Recognised_Board_Returns_All_HfBands_With_Sensible_Gains(HpsdrBoardKind board)
    {
        // Exhaustiveness pin for PaDefaults: every enum value with a known
        // PA bucket must yield 11 HF bands and either a non-zero seed (boards
        // we have a Thetis-sourced table for) or a zero seed (Unknown
        // fallback to legacy mode). This catches a future enum addition that
        // forgets to wire PaDefaults.TableFor.
        using var store = NewStore();
        var s = store.GetAll(board);
        Assert.Equal(BandUtils.HfBands.Count, s.Bands.Count);
        Assert.Equal(BandUtils.HfBands.ToArray(), s.Bands.Select(b => b.Band).ToArray());
    }

    public static IEnumerable<object[]> BoardsWithDefaults() =>
        Enum.GetValues<HpsdrBoardKind>().Select(b => new object[] { b });

    private static double FindGain(PaSettingsDto s, string band) =>
        s.Bands.First(b => b.Band == band).PaGainDb;
}
