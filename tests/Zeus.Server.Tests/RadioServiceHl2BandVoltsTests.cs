// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

// HL2 Band Volts PWM persistence & re-hydration (issue #279). Pins the
// promise of /api/radio/hl2-options PUT:
//
//   1. SetHl2BandVolts → PreferredRadioStore is written through immediately.
//   2. A second RadioService built on the SAME PreferredRadioStore (which
//      is what a server restart / process recycle looks like) sees the
//      same value via GetHl2BandVolts — i.e. the next Connect to an HL2
//      will re-hydrate the bit onto the fresh Protocol1Client.
//
// We deliberately don't drive RadioService.ConnectAsync here — that opens
// a real UDP socket and waits on the wire. The Connect-time rehydration
// itself (Protocol1Client.EnableHl2BandVolts = store.GetEnableHl2BandVolts())
// is asserted indirectly: PreferredRadioStore round-trips correctly
// (covered by PreferredRadioStoreTests) and the Connect path reads the
// same store, so any value the store reports IS what the next live
// client will get.
public class RadioServiceHl2BandVoltsTests : IDisposable
{
    private readonly string _dbPath;

    public RadioServiceHl2BandVoltsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-hl2bv-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
        try { if (File.Exists(_dbPath + ".dsp")) File.Delete(_dbPath + ".dsp"); } catch { }
    }

    private PaSettingsStore NewPaStore() =>
        new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");

    private DspSettingsStore NewDspStore() =>
        new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp");

    private RadioService BuildRadio(PreferredRadioStore prefs) =>
        new RadioService(
            NullLoggerFactory.Instance,
            NewDspStore(),
            NewPaStore(),
            preferredRadioStore: prefs);

    [Fact]
    public void Default_Is_False()
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio = BuildRadio(prefs);
        Assert.False(radio.GetHl2BandVolts());
    }

    [Fact]
    public void Set_Round_Trips_Through_Service()
    {
        using var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio = BuildRadio(prefs);

        var result = radio.SetHl2BandVolts(true);
        Assert.True(result);
        Assert.True(radio.GetHl2BandVolts());
        // Write-through assertion: the underlying store was poked, not
        // just the in-memory cache. This is the contract the next
        // RadioService instance relies on after a server restart.
        Assert.True(prefs.GetEnableHl2BandVolts());
    }

    [Fact]
    public void Persists_Across_Simulated_Reconnect()
    {
        // First "session": operator flips Band Volts on, then quits.
        using (var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath))
        using (var radio = BuildRadio(prefs))
        {
            radio.SetHl2BandVolts(true);
        }

        // Second "session": fresh stores, same on-disk DB. A live Connect
        // on this session will read the same GetHl2BandVolts result, so
        // the C3-bit-3 wire encoding picks up the operator's prior choice
        // without any explicit re-toggle.
        using var prefs2 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio2 = BuildRadio(prefs2);
        Assert.True(radio2.GetHl2BandVolts());
    }

    [Fact]
    public void Toggle_Off_Persists_Too()
    {
        // The "false" path needs the same persistence guarantee — once an
        // operator turns Band Volts off, the next session must not silently
        // resurrect the previous "true" from a stale row.
        using (var prefs = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath))
        using (var radio = BuildRadio(prefs))
        {
            radio.SetHl2BandVolts(true);
            radio.SetHl2BandVolts(false);
        }

        using var prefs2 = new PreferredRadioStore(NullLogger<PreferredRadioStore>.Instance, _dbPath);
        using var radio2 = BuildRadio(prefs2);
        Assert.False(radio2.GetHl2BandVolts());
    }

    [Fact]
    public void Null_PreferredStore_Returns_False_Default()
    {
        // Older callers that build RadioService without a preferred-radio
        // store must still answer GetHl2BandVolts without throwing — the
        // surface degrades to the shipping default (false) instead of NRE.
        using var radio = new RadioService(
            NullLoggerFactory.Instance,
            NewDspStore(),
            NewPaStore());
        Assert.False(radio.GetHl2BandVolts());
        // SetHl2BandVolts is also tolerant of the missing store: the
        // method swallows the no-op rather than throwing, because the
        // upstream API endpoint shouldn't 500 just because the test
        // harness omits a store.
        var result = radio.SetHl2BandVolts(true);
        Assert.True(result);
        // No persistent state was written (none could be), so subsequent
        // GetHl2BandVolts still reports the default.
        Assert.False(radio.GetHl2BandVolts());
    }
}
