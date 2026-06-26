// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the network-egress safety guarantees of the WSJT-X broadcaster:
//   - DISABLED (the default) ⇒ no datagram leaves the box;
//   - ENABLED ⇒ a well-formed type-12 LoggedADIF datagram is emitted;
//   - a send to a dead endpoint never throws (a broken listener must not break
//     the /api/log/entry POST).

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Wsjtx;

namespace Zeus.Server.Tests;

public sealed class WsjtxUdpBroadcasterTests : IDisposable
{
    private readonly string _prefsDbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-wsjtxbc-{Guid.NewGuid():N}.db");
    private readonly string _logDbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-log-wsjtxbc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_prefsDbPath)) File.Delete(_prefsDbPath); } catch { }
        try { if (File.Exists(_logDbPath)) File.Delete(_logDbPath); } catch { }
    }

    private async Task<(WsjtxUdpBroadcaster bc, WsjtxManagementService mgmt, LogEntry entry, LogService log)>
        BuildAsync(WsjtxConfigStore store)
    {
        var mgmt = new WsjtxManagementService(NullLogger<WsjtxManagementService>.Instance, store);
        var log = new LogService(NullLogger<LogService>.Instance, _logDbPath);
        var entry = await log.CreateLogEntryAsync(new CreateLogEntryRequest(
            Callsign: "K1ABC",
            Name: null,
            FrequencyMhz: 14.074,
            Band: "20M",
            Mode: "FT8",
            RstSent: "-12",
            RstRcvd: "-07"));
        var bc = new WsjtxUdpBroadcaster(NullLogger<WsjtxUdpBroadcaster>.Instance, mgmt, log);
        return (bc, mgmt, entry, log);
    }

    [Fact]
    public async Task Disabled_Broadcast_Sends_Nothing()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var store = new WsjtxConfigStore(NullLogger<WsjtxConfigStore>.Instance, _prefsDbPath);
        var (bc, mgmt, entry, _) = await BuildAsync(store);
        mgmt.SetConfig(new WsjtxRuntimeConfig(Enabled: false, Host: "127.0.0.1", Port: port));

        await bc.BroadcastLoggedQsoAsync(entry);

        // Nothing should arrive on the listener when egress is disabled.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await listener.ReceiveAsync(cts.Token));
    }

    [Fact]
    public async Task Enabled_Broadcast_Emits_LoggedAdif_Datagram()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        using var store = new WsjtxConfigStore(NullLogger<WsjtxConfigStore>.Instance, _prefsDbPath);
        var (bc, mgmt, entry, _) = await BuildAsync(store);
        mgmt.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Host: "127.0.0.1", Port: port, InstanceId: "Zeus"));

        await bc.BroadcastLoggedQsoAsync(entry);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await listener.ReceiveAsync(cts.Token);

        // Magic header + the call we logged in the ADIF payload.
        Assert.Equal(new byte[] { 0xAD, 0xBC, 0xCB, 0xDA }, result.Buffer[..4]);
        Assert.Contains("K1ABC", System.Text.Encoding.UTF8.GetString(result.Buffer));
    }

    [Fact]
    public async Task Enabled_Send_To_Dead_Endpoint_Never_Throws()
    {
        // Reserve a loopback port, then close it so nothing is listening. A UDP
        // send must not surface an exception into the log POST path.
        int deadPort;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            deadPort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        using var store = new WsjtxConfigStore(NullLogger<WsjtxConfigStore>.Instance, _prefsDbPath);
        var (bc, mgmt, entry, _) = await BuildAsync(store);
        mgmt.SetConfig(new WsjtxRuntimeConfig(Enabled: true, Host: "127.0.0.1", Port: deadPort));

        // Must complete without throwing.
        await bc.BroadcastLoggedQsoAsync(entry);
    }
}
