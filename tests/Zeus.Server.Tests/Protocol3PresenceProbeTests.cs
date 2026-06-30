// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

public sealed class Protocol3PresenceProbeTests
{
    [Fact]
    public async Task ProbeAsync_ReturnsCapabilitiesWhenP3AppAnswers()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var responder = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync(cts.Token);
            Assert.True(IsDiscoveryRequest(request.Buffer));
            await server.SendAsync(BuildCapabilitiesPacket(), request.RemoteEndPoint, cts.Token);
        }, cts.Token);

        var probe = new Protocol3PresenceProbe(NullLogger<Protocol3PresenceProbe>.Instance);
        var result = await probe.ProbeAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(1), cts.Token);

        await responder;
        Assert.NotNull(result);
        Assert.Equal(port, result.Port);
        Assert.Equal(10u, result.MaxRxStreams);
        Assert.Equal(1u, result.MaxTxStreams);
        Assert.Equal(0x00004000u, result.CapabilityFlags);
        Assert.Equal(0x20260629u, result.FirmwareVersion);
        Assert.Equal(0x00000001u, result.GatewareVersion);
    }

    [Fact]
    public async Task ProbeAsync_ReturnsNullWhenNoP3AppAnswers()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var probe = new Protocol3PresenceProbe(NullLogger<Protocol3PresenceProbe>.Instance);

        var result = await probe.ProbeAsync(IPAddress.Loopback, port, TimeSpan.FromMilliseconds(120));

        Assert.Null(result);
    }

    private static bool IsDiscoveryRequest(byte[] packet) =>
        packet.Length == 32 &&
        BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(0, 4)) == 0x4E395033u &&
        packet[4] == 1 &&
        packet[5] == 0 &&
        packet[6] == 1 &&
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2)) == 32 &&
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10, 2)) == 0;

    private static byte[] BuildCapabilitiesPacket()
    {
        var packet = new byte[72];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), 0x4E395033u);
        packet[4] = 1;
        packet[5] = 0;
        packet[6] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), 32);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), 40);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), 2);

        var payload = packet.AsSpan(32, 40);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(0, 4), 10);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(8, 4), 0x3f);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(12, 4), 0x04);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(16, 4), 0x01);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(20, 4), 0x01);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(24, 4), 1440);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(28, 4), 0x00004000u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(32, 4), 0x20260629u);
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(36, 4), 0x00000001u);
        return packet;
    }
}
