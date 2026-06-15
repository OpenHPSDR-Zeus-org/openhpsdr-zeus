// SPDX-License-Identifier: GPL-2.0-or-later
//
// SpotsSettingsStore — first-run defaults and round-trip of the persisted
// fields, with emphasis on the newer watchlist / alert / scan additions whose
// normalization (callsign upper-casing + dedup, scan-dwell clamp) is easy to
// regress when the record grows.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class SpotsSettingsStoreTests : IDisposable
{
    private readonly string _dbPath;

    public SpotsSettingsStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-spotssettings-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private SpotsSettingsStore NewStore() =>
        new SpotsSettingsStore(NullLogger<SpotsSettingsStore>.Instance, _dbPath);

    [Fact]
    public void FirstRun_ReturnsDefaults()
    {
        using var store = NewStore();
        var s = store.Get();
        Assert.True(s.Enabled);
        Assert.False(s.AlertsEnabled);
        Assert.True(s.AlertSound);
        Assert.False(s.HideWorked);
        Assert.False(s.EnrichQrz);
        Assert.Equal(8, s.ScanDwellSeconds);
        Assert.Null(s.Watchlist);
    }

    [Fact]
    public void Watchlist_IsUpperCasedAndDeduped()
    {
        using var store = NewStore();
        store.Set(new SpotsSettings(Watchlist: new[] { "w1aw", "K2ABC", "w1aw", "  k2abc  " }));
        var s = store.Get();
        Assert.NotNull(s.Watchlist);
        Assert.Equal(new[] { "W1AW", "K2ABC" }, s.Watchlist);
    }

    [Fact]
    public void ScanDwell_IsClampedToRange()
    {
        using var store = NewStore();
        store.Set(new SpotsSettings(ScanDwellSeconds: 1));   // below min
        Assert.Equal(SpotsSettings.MinScanDwellSeconds, store.Get().ScanDwellSeconds);

        store.Set(new SpotsSettings(ScanDwellSeconds: 9999)); // above max
        Assert.Equal(SpotsSettings.MaxScanDwellSeconds, store.Get().ScanDwellSeconds);
    }

    [Fact]
    public void AlertAndWorkedFlags_RoundTrip()
    {
        using var store = NewStore();
        store.Set(new SpotsSettings(
            AlertsEnabled: true,
            AlertSound: false,
            HideWorked: true,
            EnrichQrz: true,
            ScanDwellSeconds: 30));
        var s = store.Get();
        Assert.True(s.AlertsEnabled);
        Assert.False(s.AlertSound);
        Assert.True(s.HideWorked);
        Assert.True(s.EnrichQrz);
        Assert.Equal(30, s.ScanDwellSeconds);
    }

    [Fact]
    public void StatePersistsAcrossStoreInstances()
    {
        using (var first = NewStore())
        {
            first.Set(new SpotsSettings(Watchlist: new[] { "dl1abc" }, AlertsEnabled: true));
        }
        using var second = NewStore();
        var s = second.Get();
        Assert.Equal(new[] { "DL1ABC" }, s.Watchlist);
        Assert.True(s.AlertsEnabled);
    }
}
