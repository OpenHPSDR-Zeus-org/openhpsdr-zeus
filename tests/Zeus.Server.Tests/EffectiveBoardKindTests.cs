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

// Two-axis invariant for the radio-selection header:
//
//   1. Before connect, the operator's preference drives defaults.
//   2. Once something IS on the wire, discovery overrides preference —
//      we will not seed a G2's 51 dB table into an HL2 that's actually
//      plugged in because the operator forgot to flip the dropdown.
//
// The drive-byte profile stays on ConnectedBoardKind separately (proven
// in RadioDriveProfileTests) — those are two different concerns.
public class EffectiveBoardKindTests : IDisposable
{
    private readonly string _dbPath;

    public EffectiveBoardKindTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-effboard-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
        try { if (File.Exists(_dbPath + ".dsp")) File.Delete(_dbPath + ".dsp"); } catch { }
    }

    // Isolated stores so tests stay hermetic against the prod zeus-prefs.db.
    private PaSettingsStore NewPaStore() =>
        new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");

    private DspSettingsStore NewDspStore() =>
        new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");

    private RadioService BuildRadio(PreferredRadioStore prefs)
    {
        var dspStore = NewDspStore();
        var paStore = NewPaStore();
        return new RadioService(NullLoggerFactory.Instance, dspStore, paStore, preferredRadioStore: prefs);
    }

    [Fact]
    public void Disconnected_No_Preference_Is_Unknown()
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio = BuildRadio(prefs);

        Assert.Equal(HpsdrBoardKind.Unknown, radio.ConnectedBoardKind);
        Assert.Equal(HpsdrBoardKind.Unknown, radio.EffectiveBoardKind);
    }

    [Fact]
    public void Disconnected_With_Preference_Uses_Preferred()
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        prefs.Set(HpsdrBoardKind.HermesLite2);
        using var radio = BuildRadio(prefs);

        Assert.Equal(HpsdrBoardKind.Unknown, radio.ConnectedBoardKind);
        Assert.Equal(HpsdrBoardKind.HermesLite2, radio.EffectiveBoardKind);
    }

    [Fact]
    public void Preference_Defaults_PA_Table_Before_Connect()
    {
        // End-to-end: selecting "ANAN G2 (OrionMkII)" before connect must
        // surface the OrionG2Gains table through GetAll, so the PA panel
        // loads with 47.9 dB / 50.9 dB / … seeds instead of HL2's flat 40.5.
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        prefs.Set(HpsdrBoardKind.OrionMkII);
        using var radio = BuildRadio(prefs);

        var paStore = NewPaStore();
        var cfg = paStore.GetAll(radio.EffectiveBoardKind);

        var m160 = cfg.Bands.First(b => b.Band == "160m");
        var m20 = cfg.Bands.First(b => b.Band == "20m");
        Assert.Equal(47.9, m160.PaGainDb);
        Assert.Equal(50.9, m20.PaGainDb);
    }

    [Fact]
    public void Null_PreferredStore_Falls_Back_To_Connected_Unknown()
    {
        // RadioService is constructed without the preferred-radio store
        // in older callers; EffectiveBoardKind must degrade to
        // ConnectedBoardKind rather than NRE.
        var dspStore = NewDspStore();
        var paStore = NewPaStore();
        using var radio = new RadioService(NullLoggerFactory.Instance, dspStore, paStore);

        Assert.Equal(HpsdrBoardKind.Unknown, radio.EffectiveBoardKind);
    }
}
