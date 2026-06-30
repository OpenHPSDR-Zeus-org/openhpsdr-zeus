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
using System.Reflection;
using Xunit;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// Issue #1204 regression: a non-TimedOut SocketException on the RX socket
/// (NetworkReset / ConnectionReset / Shutdown / NetworkDown, as Windows
/// surfaces when NIC power-management resets a link-local APIPA adapter
/// after a long session) previously fell through the inner try/catch in
/// RxLoop, killing the RX thread silently via the outer finally. RadioService
/// was never notified, so the backend sat phantom-Connected — audio dead,
/// waterfall frozen, buttons drew but did nothing. The fix adds a broad
/// SocketException catch that logs the error and fires Disconnected before
/// returning so RadioService tears down cleanly.
/// </summary>
public class Protocol1Client_RxLoopSocketErrorTests
{
    [Fact]
    public async Task RxLoop_FiresDisconnected_OnNonTimedOutSocketException()
    {
        using var fakeRadio = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeRadio.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        fakeRadio.ReceiveTimeout = 500;
        var fakeRadioEp = (IPEndPoint)fakeRadio.LocalEndPoint!;

        using var client = new Protocol1Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var disconnectedGate = new TaskCompletionSource();
        client.Disconnected += () => disconnectedGate.TrySetResult();

        await client.ConnectAsync(fakeRadioEp, cts.Token);
        await client.StartAsync(
            new StreamConfig(HpsdrSampleRate.Rate192k, PreampOn: false, Atten: HpsdrAtten.Zero),
            cts.Token);

        // Drain the Metis-start so we know RxLoop is sitting in ReceiveFrom.
        var startBuf = new byte[64];
        EndPoint startRemote = new IPEndPoint(IPAddress.Any, 0);
        fakeRadio.ReceiveFrom(startBuf, ref startRemote);

        // Force a non-TimedOut SocketException on the RX socket without
        // disposing it (the bug we are guarding against does NOT dispose the
        // socket — Windows raises NetworkReset / ConnectionReset against a
        // still-live socket when NIC power-management cycles the adapter).
        //
        // Approach: Connect the bound RX socket to a closed local port and
        // SendTo it. Linux delivers ICMP Port Unreachable back to the same
        // socket; the next ReceiveFrom on that socket throws
        // SocketException(ConnectionRefused). Windows surfaces this as
        // ConnectionReset on the connected UDP socket via WSAECONNRESET.
        // Either way it lands in the new broad SocketException catch.
        var sockField = typeof(Protocol1Client).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sockField);
        var sock = (Socket?)sockField!.GetValue(client);
        Assert.NotNull(sock);
        // Loopback:1 is reserved (tcpmux) — almost never bound for UDP.
        var unreachable = new IPEndPoint(IPAddress.Loopback, 1);
        sock!.Connect(unreachable);
        try { sock.Send(new byte[1]); } catch (SocketException) { /* ICMP-driven Send may itself surface the error */ }

        // Without the fix the RX thread dies silently and Disconnected never
        // fires — this await would time out. With the fix the broad
        // SocketException catch fires Disconnected before returning. The
        // 1-second budget stays below the consecutive-timeout fallback
        // (10 × 100 ms ReceiveTimeout = 1 s) so a pass cannot be attributed
        // to the existing TimedOut catch.
        await disconnectedGate.Task.WaitAsync(TimeSpan.FromMilliseconds(800), cts.Token);
    }
}
