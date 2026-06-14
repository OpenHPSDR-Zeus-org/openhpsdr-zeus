// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-ports plan, Phase 2 — per-band antenna persistence round-trip.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Protocol1;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AntennaSettingsStoreTests : IDisposable
{
    private readonly string _dbPath;

    public AntennaSettingsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-antenna-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private AntennaSettingsStore NewStore() =>
        new AntennaSettingsStore(NullLogger<AntennaSettingsStore>.Instance, _dbPath);

    [Fact]
    public void Unset_Band_Defaults_To_Ant1()
    {
        using var store = NewStore();
        var sel = store.GetBand("20m");
        Assert.Equal(HpsdrAntenna.Ant1, sel.TxAnt);
        Assert.Equal(HpsdrAntenna.Ant1, sel.RxAnt);
    }

    [Fact]
    public void SetBand_RoundTrips_PerBand()
    {
        using (var store = NewStore())
        {
            store.SetBand("20m", HpsdrAntenna.Ant2, HpsdrAntenna.Ant3);
            store.SetBand("40m", HpsdrAntenna.Ant3, HpsdrAntenna.Ant1);
        }

        // Reopen to prove it survives a backend restart (the whole point of
        // server-authoritative persistence).
        using var reopened = NewStore();
        var b20 = reopened.GetBand("20m");
        Assert.Equal(HpsdrAntenna.Ant2, b20.TxAnt);
        Assert.Equal(HpsdrAntenna.Ant3, b20.RxAnt);

        var b40 = reopened.GetBand("40m");
        Assert.Equal(HpsdrAntenna.Ant3, b40.TxAnt);
        Assert.Equal(HpsdrAntenna.Ant1, b40.RxAnt);

        // An untouched band is still ANT1 — selections are per-band, not global.
        var b15 = reopened.GetBand("15m");
        Assert.Equal(HpsdrAntenna.Ant1, b15.TxAnt);
        Assert.Equal(HpsdrAntenna.Ant1, b15.RxAnt);
    }

    [Fact]
    public void SetBand_Upsert_Updates_Existing()
    {
        using var store = NewStore();
        store.SetBand("20m", HpsdrAntenna.Ant2, HpsdrAntenna.Ant2);
        store.SetBand("20m", HpsdrAntenna.Ant3, HpsdrAntenna.Ant1);

        var sel = store.GetBand("20m");
        Assert.Equal(HpsdrAntenna.Ant3, sel.TxAnt);
        Assert.Equal(HpsdrAntenna.Ant1, sel.RxAnt);
    }

    [Fact]
    public void Changed_Fires_On_Save()
    {
        using var store = NewStore();
        int fired = 0;
        store.Changed += () => fired++;
        store.SetBand("20m", HpsdrAntenna.Ant2, HpsdrAntenna.Ant1);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void GetAll_Returns_Every_HfBand()
    {
        using var store = NewStore();
        store.SetBand("20m", HpsdrAntenna.Ant2, HpsdrAntenna.Ant3);
        var all = store.GetAll();
        Assert.All(BandUtils.HfBands, b => Assert.Contains(all, x => x.Band == b));
        var b20 = all.First(x => x.Band == "20m");
        Assert.Equal(HpsdrAntenna.Ant2, b20.TxAnt);
        Assert.Equal(HpsdrAntenna.Ant3, b20.RxAnt);
    }
}
