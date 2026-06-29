// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Protocol2.Discovery;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Covers the targeted unicast busy-probe that the P2 connect path uses to
/// refuse becoming a SECOND master on a radio another controller already drives
/// (relay-chatter / brown-out guard). Drives the probe against a loopback fake
/// radio so the Busy/Idle/no-reply verdicts are pinned without real hardware.
/// </summary>
public class BusyProbeTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(800);

    [Fact]
    public async Task Probe_Returns_Busy_When_Radio_Reports_Status_0x03()
    {
        using var radio = new FakeRadio(status: 0x03);
        var svc = new RadioDiscoveryService(NullLogger<RadioDiscoveryService>.Instance);

        var reply = await svc.ProbeEndpointAsync(radio.Endpoint, ProbeTimeout);

        Assert.NotNull(reply);
        Assert.True(reply!.Details.Busy);
    }

    [Fact]
    public async Task Probe_Returns_Idle_When_Radio_Reports_Status_0x02()
    {
        using var radio = new FakeRadio(status: 0x02);
        var svc = new RadioDiscoveryService(NullLogger<RadioDiscoveryService>.Instance);

        var reply = await svc.ProbeEndpointAsync(radio.Endpoint, ProbeTimeout);

        Assert.NotNull(reply);
        Assert.False(reply!.Details.Busy);
    }

    [Fact]
    public async Task Probe_Fails_Open_With_Null_When_Radio_Does_Not_Answer()
    {
        // No fake radio bound at this port → nothing answers. The guard must fail
        // OPEN (null, not a thrown exception) so a non-answering radio is never
        // blocked from connecting.
        var dead = new IPEndPoint(IPAddress.Loopback, FindFreeUdpPort());
        var svc = new RadioDiscoveryService(NullLogger<RadioDiscoveryService>.Instance);

        var reply = await svc.ProbeEndpointAsync(dead, TimeSpan.FromMilliseconds(250));

        Assert.Null(reply);
    }

    private static int FindFreeUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Minimal loopback "radio": binds a UDP socket on 127.0.0.1 and answers any
    /// datagram with a 24-byte P2 discovery reply carrying the requested status
    /// byte (0x02 idle / 0x03 busy).
    /// </summary>
    private sealed class FakeRadio : IDisposable
    {
        private readonly Socket _sock;
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _reply;

        public FakeRadio(byte status)
        {
            _reply = BuildReply(status);
            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Endpoint = (IPEndPoint)_sock.LocalEndPoint!;
            _ = Task.Run(RespondLoop);
        }

        public IPEndPoint Endpoint { get; }

        private async Task RespondLoop()
        {
            var buf = new byte[2048];
            var any = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var res = await _sock.ReceiveFromAsync(buf, SocketFlags.None, any, _cts.Token)
                        .ConfigureAwait(false);
                    await _sock.SendToAsync(_reply, SocketFlags.None, res.RemoteEndPoint, _cts.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private static byte[] BuildReply(byte status)
        {
            var reply = new byte[24];
            // bytes 0..3 = 0x00 P2 reply marker; byte 4 = status.
            reply[4] = status;
            // MAC bytes 5..10.
            reply[5] = 0x00; reply[6] = 0x1C; reply[7] = 0xC0;
            reply[8] = 0xDE; reply[9] = 0xCA; reply[10] = 0xFE;
            reply[11] = 0x0A; // board id (Saturn / ANAN-G2)
            reply[12] = 38;   // protocol supported
            reply[13] = 21;   // code version
            reply[20] = 2;    // num receivers
            return reply;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _sock.Dispose();
            _cts.Dispose();
        }
    }
}
