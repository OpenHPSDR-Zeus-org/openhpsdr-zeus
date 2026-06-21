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

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// End-to-end test for the HardwarePttChanged event. Spins up a real
/// Protocol1Client against a loopback UDP socket pretending to be a radio,
/// sends EP6 packets with C0[0] toggled, and asserts the client raises the
/// event exactly once per level change (edge-triggered, ignores no-op
/// packets that keep the level the same).
/// </summary>
public class Protocol1Client_HardwarePttEventTests
{
    [Fact]
    public async Task HardwarePttChanged_FiresOncePerLevelChange()
    {
        using var fakeRadio = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeRadio.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        fakeRadio.ReceiveTimeout = 500;
        var fakeRadioEp = (IPEndPoint)fakeRadio.LocalEndPoint!;

        using var client = new Protocol1Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var observed = new List<bool>();
        var firstEdgeGate = new TaskCompletionSource();
        client.HardwarePttChanged += level =>
        {
            lock (observed)
            {
                observed.Add(level);
                if (observed.Count == 1) firstEdgeGate.TrySetResult();
            }
        };

        await client.ConnectAsync(fakeRadioEp, cts.Token);
        await client.StartAsync(
            new StreamConfig(HpsdrSampleRate.Rate192k, PreampOn: false, Atten: HpsdrAtten.Zero),
            cts.Token);

        // Drain Metis-start to learn the client's local port.
        IPEndPoint clientEp = ReceiveFirstFromClient(fakeRadio);

        // Send: off, off, on, on, off, on — expect events at levels 3, 5, 6
        // (positions where the level *changed*). The initial run of "off"
        // matches the client's startup state (_hardwarePtt=0) so the first
        // event is the rise to "on".
        var sequence = new[] { false, false, true, true, false, true };
        for (uint seq = 0; seq < sequence.Length; seq++)
        {
            var packet = BuildEp6Packet(seq, hardwarePtt: sequence[seq]);
            fakeRadio.SendTo(packet, clientEp);
        }

        await firstEdgeGate.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        // Grace for the remaining packets to be drained by the RX loop.
        await Task.Delay(200, cts.Token);

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);

        List<bool> captured;
        lock (observed) captured = new(observed);

        // Exactly three edges from the input sequence.
        Assert.Equal(new[] { true, false, true }, captured);
        Assert.True(client.HardwarePtt, "final level read should match the last packet (PTT on)");
    }

    [Fact]
    public async Task HardwarePttChanged_FiresOnEitherUsbFrameSettingTheBit()
    {
        using var fakeRadio = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeRadio.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        fakeRadio.ReceiveTimeout = 500;
        var fakeRadioEp = (IPEndPoint)fakeRadio.LocalEndPoint!;

        using var client = new Protocol1Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var gate = new TaskCompletionSource<bool>();
        client.HardwarePttChanged += level => gate.TrySetResult(level);

        await client.ConnectAsync(fakeRadioEp, cts.Token);
        await client.StartAsync(
            new StreamConfig(HpsdrSampleRate.Rate192k, PreampOn: false, Atten: HpsdrAtten.Zero),
            cts.Token);

        IPEndPoint clientEp = ReceiveFirstFromClient(fakeRadio);

        // First USB frame clear, second carries the PTT bit — combined OR
        // should still raise the rising-edge event.
        var packet = new byte[1032];
        packet[0] = 0xEF; packet[1] = 0xFE; packet[2] = 0x01; packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), 0u);
        for (int f = 0; f < 2; f++)
        {
            int fs = 8 + f * 512;
            packet[fs + 0] = 0x7F; packet[fs + 1] = 0x7F; packet[fs + 2] = 0x7F;
            packet[fs + 3] = 0x00;
        }
        // Set C0[0] on the second USB frame only.
        packet[8 + 512 + 3] = 0x01;
        fakeRadio.SendTo(packet, clientEp);

        var observed = await gate.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

        await client.StopAsync(CancellationToken.None);
        await client.DisconnectAsync(CancellationToken.None);

        Assert.True(observed);
    }

    private static IPEndPoint ReceiveFirstFromClient(Socket fakeRadio)
    {
        var buf = new byte[2048];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        fakeRadio.ReceiveFrom(buf, ref remote);
        return (IPEndPoint)remote;
    }

    private static byte[] BuildEp6Packet(uint seq, bool hardwarePtt)
    {
        var packet = new byte[1032];
        packet[0] = 0xEF; packet[1] = 0xFE; packet[2] = 0x01; packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), seq);

        byte c0 = hardwarePtt ? (byte)0x01 : (byte)0x00;
        for (int f = 0; f < 2; f++)
        {
            int fs = 8 + f * 512;
            packet[fs + 0] = 0x7F; packet[fs + 1] = 0x7F; packet[fs + 2] = 0x7F;
            packet[fs + 3] = c0; // C0[0] = PTT echo (bits 7:3 = 0 → non-AIN address)
        }
        return packet;
    }
}
