// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// DxClusterConfigStore — single-row LiteDB persistence via the shared lease.
// Mirrors PttSettingsStoreTests' temp-DB pattern (explicit dbPathOverride so the
// suite never collides on the shared zeus-prefs.db). Round-trips every field,
// confirms the default-off (no row → null) contract, and that values survive a
// store re-open on the same path.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class DxClusterConfigStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-dxcluster-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + "-log")) File.Delete(_dbPath + "-log"); } catch { }
    }

    [Fact]
    public void Get_FreshDb_ReturnsNull()
    {
        using var store = new DxClusterConfigStore(NullLogger<DxClusterConfigStore>.Instance, _dbPath);
        Assert.Null(store.Get());
    }

    [Fact]
    public void SetGet_RoundTripsAllFields()
    {
        using var store = new DxClusterConfigStore(NullLogger<DxClusterConfigStore>.Instance, _dbPath);
        var cfg = new DxClusterConfig(
            Enabled: true,
            Host: "dxc.example.org",
            Port: 7300,
            Callsign: "K1ABC",
            Password: "s3cret",
            LoginCommands: "set/filter on\nsh/dx",
            AutoConnect: true);

        store.Set(cfg);
        var got = store.Get();

        Assert.NotNull(got);
        Assert.True(got!.Enabled);
        Assert.Equal("dxc.example.org", got.Host);
        Assert.Equal(7300, got.Port);
        Assert.Equal("K1ABC", got.Callsign);
        Assert.Equal("s3cret", got.Password);
        Assert.Equal("set/filter on\nsh/dx", got.LoginCommands);
        Assert.True(got.AutoConnect);
    }

    [Fact]
    public void Set_Twice_UpdatesSingleRow()
    {
        using var store = new DxClusterConfigStore(NullLogger<DxClusterConfigStore>.Instance, _dbPath);
        store.Set(new DxClusterConfig(Enabled: true, Host: "a", Port: 1, Callsign: "K1A"));
        store.Set(new DxClusterConfig(Enabled: false, Host: "b", Port: 2, Callsign: "K2B"));

        var got = store.Get();
        Assert.NotNull(got);
        Assert.False(got!.Enabled);
        Assert.Equal("b", got.Host);
        Assert.Equal(2, got.Port);
        Assert.Equal("K2B", got.Callsign);
    }

    [Fact]
    public void Config_PersistsAcrossReopen()
    {
        using (var store = new DxClusterConfigStore(NullLogger<DxClusterConfigStore>.Instance, _dbPath))
        {
            store.Set(new DxClusterConfig(
                Enabled: true, Host: "host", Port: 7373, Callsign: "K1ABC", AutoConnect: true));
        }

        using (var reopened = new DxClusterConfigStore(NullLogger<DxClusterConfigStore>.Instance, _dbPath))
        {
            var got = reopened.Get();
            Assert.NotNull(got);
            Assert.True(got!.Enabled);
            Assert.Equal("host", got.Host);
            Assert.True(got.AutoConnect);
        }
    }
}
