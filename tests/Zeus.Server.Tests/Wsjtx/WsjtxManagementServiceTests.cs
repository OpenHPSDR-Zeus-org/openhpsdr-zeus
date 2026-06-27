// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the safety-critical invariants of the WSJT-X logged-QSO broadcaster
// config: it is OFF by default (new network egress is opt-in), SetConfig
// normalises hostile input, and the choice persists across store instances.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class WsjtxManagementServiceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-wsjtx-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private WsjtxConfigStore NewStore() =>
        new(NullLogger<WsjtxConfigStore>.Instance, _dbPath);

    private static WsjtxManagementService NewService(WsjtxConfigStore store) =>
        new(NullLogger<WsjtxManagementService>.Instance, store);

    [Fact]
    public void Defaults_To_Disabled_When_Nothing_Persisted()
    {
        using var store = NewStore();
        var svc = NewService(store);

        var cfg = svc.GetConfig();
        Assert.False(cfg.Enabled); // the whole point: opt-in egress, off by default
        Assert.Equal("127.0.0.1", cfg.Host);
        Assert.Equal(2237, cfg.Port);
        Assert.Equal("WSJT-X", cfg.InstanceId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(70000)]
    public void SetConfig_Clamps_OutOfRange_Port_To_Default(int port)
    {
        using var store = NewStore();
        var svc = NewService(store);

        var status = svc.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Port: port));
        Assert.Equal(2237, status.Port);
    }

    [Fact]
    public void SetConfig_Keeps_Valid_Port()
    {
        using var store = NewStore();
        var svc = NewService(store);

        var status = svc.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Port: 2333));
        Assert.Equal(2333, status.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SetConfig_Defaults_Blank_Host_To_Loopback(string? host)
    {
        using var store = NewStore();
        var svc = NewService(store);

        var status = svc.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Host: host!));
        Assert.Equal("127.0.0.1", status.Host);
    }

    [Fact]
    public void SetConfig_Trims_Host_And_InstanceId_And_Defaults_Blank_Id()
    {
        using var store = NewStore();
        var svc = NewService(store);

        var trimmed = svc.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Host: "  10.0.0.5  ", InstanceId: "  Shack  "));
        Assert.Equal("10.0.0.5", trimmed.Host);
        Assert.Equal("Shack", trimmed.InstanceId);

        var blankId = svc.SetConfig(new WsjtxRuntimeConfig(Enabled: true, InstanceId: "   "));
        Assert.Equal("WSJT-X", blankId.InstanceId);
    }

    [Fact]
    public void SetConfig_Persists_Across_Store_Instances()
    {
        using (var store = NewStore())
        {
            var svc = NewService(store);
            svc.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Host: "192.168.1.50", Port: 2333, InstanceId: "Shack"));
        }

        // A fresh store + service over the same DB must re-read the saved config.
        using var reopened = NewStore();
        var reloaded = NewService(reopened).GetConfig();
        Assert.True(reloaded.Enabled);
        Assert.Equal("192.168.1.50", reloaded.Host);
        Assert.Equal(2333, reloaded.Port);
        Assert.Equal("Shack", reloaded.InstanceId);
    }
}
