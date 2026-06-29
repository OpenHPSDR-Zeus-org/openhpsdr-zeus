// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Round-trip the preferred-radio preference. Tests use a per-test tmp DB
// path so the production zeus-prefs.db isn't mutated.
public class PreferredRadioStoreTests : IDisposable
{
    private readonly string _dbPath;

    public PreferredRadioStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-radio-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* test-only cleanup */ }
    }

    [Fact]
    public void Empty_Store_Returns_Null_Meaning_Auto()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.Null(store.Get());
    }

    [Fact]
    public void Set_And_Get_Round_Trip_HermesLite2()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.HermesLite2);
        Assert.Equal(HpsdrBoardKind.HermesLite2, store.Get());
    }

    [Fact]
    public void Set_Auto_Clears_Override()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.OrionMkII);
        Assert.Equal(HpsdrBoardKind.OrionMkII, store.Get());
        store.Set(null);
        Assert.Null(store.Get());
    }

    [Fact]
    public void Setting_Unknown_Is_Treated_As_Auto()
    {
        // Unknown in the enum is a sentinel for "no radio" — persisting it
        // would just seed junk defaults, so the store collapses it to Auto.
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.Hermes);
        store.Set(HpsdrBoardKind.Unknown);
        Assert.Null(store.Get());
    }

    [Fact]
    public void Persists_Across_Instances()
    {
        using (var s1 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath))
        {
            s1.Set(HpsdrBoardKind.OrionMkII);
        }
        using var s2 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.Equal(HpsdrBoardKind.OrionMkII, s2.Get());
    }

    [Fact]
    public void Changed_Fires_On_Set()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        int fired = 0;
        store.Changed += () => fired++;
        store.Set(HpsdrBoardKind.Hermes);
        store.Set(null);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Empty_Store_Returns_G2_For_OrionMkIIVariant()
    {
        // Issue #218: shipping default for the 0x0A wire-byte alias family is
        // G2. An untouched store must report G2 so dispatch keeps Zeus'
        // pre-#218 behaviour for operators who never visit the variant UI.
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.Equal(OrionMkIIVariant.G2, store.GetOrionMkIIVariant());
    }

    [Fact]
    public void OrionMkIIVariant_Round_Trips()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.SetOrionMkIIVariant(OrionMkIIVariant.Anan8000DLE);
        Assert.Equal(OrionMkIIVariant.Anan8000DLE, store.GetOrionMkIIVariant());
    }

    [Fact]
    public void OrionMkIIVariant_Persists_Across_Instances()
    {
        using (var s1 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath))
        {
            s1.SetOrionMkIIVariant(OrionMkIIVariant.G2_1K);
        }
        using var s2 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.Equal(OrionMkIIVariant.G2_1K, s2.GetOrionMkIIVariant());
    }

    [Fact]
    public void OrionMkIIVariant_Coexists_With_Board_Selection()
    {
        // Setting board and variant must persist independently; old code
        // paths that touch only the board field must not stomp the variant.
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.OrionMkII);
        store.SetOrionMkIIVariant(OrionMkIIVariant.Anan8000DLE);
        Assert.Equal(HpsdrBoardKind.OrionMkII, store.Get());
        Assert.Equal(OrionMkIIVariant.Anan8000DLE, store.GetOrionMkIIVariant());

        // Re-setting only the board must not clobber the variant.
        store.Set(HpsdrBoardKind.OrionMkII, overrideDetection: true);
        Assert.Equal(OrionMkIIVariant.Anan8000DLE, store.GetOrionMkIIVariant());
    }

    // ---- HL2 Band Volts PWM (issue #279) ----

    [Fact]
    public void Empty_Store_Returns_False_For_Hl2BandVolts()
    {
        // Shipping default: off. An untouched store must report false so
        // the next outgoing HL2 Config frame keeps C3 bit 3 cleared.
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.False(store.GetEnableHl2BandVolts());
    }

    [Fact]
    public void Hl2BandVolts_Round_Trips()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.SetEnableHl2BandVolts(true);
        Assert.True(store.GetEnableHl2BandVolts());
        store.SetEnableHl2BandVolts(false);
        Assert.False(store.GetEnableHl2BandVolts());
    }

    [Fact]
    public void Hl2BandVolts_Persists_Across_Instances()
    {
        using (var s1 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath))
        {
            s1.SetEnableHl2BandVolts(true);
        }
        using var s2 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.True(s2.GetEnableHl2BandVolts());
    }

    [Fact]
    public void Hl2BandVolts_Coexists_With_Board_And_Variant()
    {
        // Independent fields must persist on the single shared row; setting
        // one must not stomp the others.
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.HermesLite2);
        store.SetOrionMkIIVariant(OrionMkIIVariant.G2_1K);
        store.SetEnableHl2BandVolts(true);

        Assert.Equal(HpsdrBoardKind.HermesLite2, store.Get());
        Assert.Equal(OrionMkIIVariant.G2_1K, store.GetOrionMkIIVariant());
        Assert.True(store.GetEnableHl2BandVolts());

        // Re-setting the board (no variant arg) must not clobber Band Volts.
        store.Set(HpsdrBoardKind.HermesLite2, overrideDetection: true);
        Assert.True(store.GetEnableHl2BandVolts());
        Assert.Equal(OrionMkIIVariant.G2_1K, store.GetOrionMkIIVariant());
    }

    [Fact]
    public void Setting_Auto_Preserves_Sibling_Hardware_Options()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.OrionMkII);
        store.SetOrionMkIIVariant(OrionMkIIVariant.G2_1K);
        store.SetEnableHl2BandVolts(true);
        store.SetG2AdcOptions(ditherEnabled: false, randomEnabled: false, rx1AttenuatorDb: 12);

        store.Set(null);

        Assert.Null(store.Get());
        Assert.Equal(OrionMkIIVariant.G2_1K, store.GetOrionMkIIVariant());
        Assert.True(store.GetEnableHl2BandVolts());
        Assert.False(store.GetG2AdcDitherEnabled());
        Assert.False(store.GetG2AdcRandomEnabled());
        Assert.Equal(12, store.GetG2Rx1AttenuatorDb());
    }

    [Fact]
    public void Hl2BandVolts_Changed_Fires_On_Set()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        int fired = 0;
        store.Changed += () => fired++;
        store.SetEnableHl2BandVolts(true);
        store.SetEnableHl2BandVolts(false);
        Assert.Equal(2, fired);
    }

    // ---- G2 ADC dither/random defaults ----

    [Fact]
    public void Empty_Store_Returns_True_For_G2_Adc_Dither_And_Random()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.True(store.GetG2AdcDitherEnabled());
        Assert.True(store.GetG2AdcRandomEnabled());
        Assert.Equal(0, store.GetG2Rx1AttenuatorDb());
    }

    [Fact]
    public void Empty_Store_Returns_Null_For_Raw_Adc_Options()
    {
        // The raw getters preserve "operator never set this" as null so the
        // resolver can apply a protocol-specific default (P2 on / P1 off). The
        // shipped non-raw getters still collapse null → true for the P2 path.
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.Null(store.GetG2AdcDitherEnabledRaw());
        Assert.Null(store.GetG2AdcRandomEnabledRaw());
    }

    [Fact]
    public void Setting_One_Adc_Option_Leaves_The_Other_Raw_Null()
    {
        // Partial PUT (only dither) must not materialize the P2 default-on for
        // the untouched random field — that would wrongly force random on for a
        // Protocol-1 board, whose default is off.
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.SetG2AdcOptions(ditherEnabled: true);

        Assert.True(store.GetG2AdcDitherEnabledRaw());
        Assert.Null(store.GetG2AdcRandomEnabledRaw());
        // P2 getter still defaults the untouched field on.
        Assert.True(store.GetG2AdcRandomEnabled());
    }

    [Fact]
    public void G2_Adc_Options_Round_Trip_And_Persist()
    {
        using (var s1 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath))
        {
            s1.SetG2AdcOptions(ditherEnabled: false, randomEnabled: true, rx1AttenuatorDb: 17);
        }

        using var s2 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        Assert.False(s2.GetG2AdcDitherEnabled());
        Assert.True(s2.GetG2AdcRandomEnabled());
        Assert.Equal(17, s2.GetG2Rx1AttenuatorDb());
    }

    [Fact]
    public void G2_Adc_Options_Coexist_With_Board_Variant_And_Hl2()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.Set(HpsdrBoardKind.OrionMkII);
        store.SetOrionMkIIVariant(OrionMkIIVariant.G2_1K);
        store.SetEnableHl2BandVolts(true);
        store.SetG2AdcOptions(ditherEnabled: false, randomEnabled: false, rx1AttenuatorDb: 31);

        Assert.Equal(HpsdrBoardKind.OrionMkII, store.Get());
        Assert.Equal(OrionMkIIVariant.G2_1K, store.GetOrionMkIIVariant());
        Assert.True(store.GetEnableHl2BandVolts());
        Assert.False(store.GetG2AdcDitherEnabled());
        Assert.False(store.GetG2AdcRandomEnabled());
        Assert.Equal(31, store.GetG2Rx1AttenuatorDb());
    }

    [Fact]
    public void G2_Rx1_Attenuator_Clamps_To_Hardware_Range()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        store.SetG2AdcOptions(rx1AttenuatorDb: -5);
        Assert.Equal(0, store.GetG2Rx1AttenuatorDb());

        store.SetG2AdcOptions(rx1AttenuatorDb: 44);
        Assert.Equal(31, store.GetG2Rx1AttenuatorDb());
    }

    [Fact]
    public void G2_Adc_Options_Changed_Fires_On_Set()
    {
        using var store = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        int fired = 0;
        store.Changed += () => fired++;
        store.SetG2AdcOptions(ditherEnabled: false);
        store.SetG2AdcOptions(randomEnabled: false);
        Assert.Equal(2, fired);
    }
}
