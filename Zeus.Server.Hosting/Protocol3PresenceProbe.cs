// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Zeus.Server;

public sealed record Protocol3PresenceResult(
    int Port,
    uint MaxRxStreams,
    uint MaxTxStreams,
    uint CapabilityFlags,
    uint FirmwareVersion,
    uint GatewareVersion);

public sealed class Protocol3PresenceProbe
{
    public const int DefaultPort = 1030;
    private const uint Magic = 0x4E395033u; // "N9P3"
    private const byte VersionMajor = 1;
    private const byte VersionMinor = 0;
    private const byte DiscoveryRequest = 1;
    private const byte Capabilities = 2;
    private const int HeaderBytes = 32;
    private const int CapabilitiesBytes = 40;
    private const int ReceiveBufferSize = 2048;
    private const int SendAttempts = 2;
    private static readonly TimeSpan SendGap = TimeSpan.FromMilliseconds(40);

    private readonly ILogger<Protocol3PresenceProbe> _log;

    public Protocol3PresenceProbe(ILogger<Protocol3PresenceProbe> log)
    {
        _log = log;
    }

    public async Task<Protocol3PresenceResult?> ProbeAsync(
        IPAddress radioIp,
        TimeSpan timeout,
        CancellationToken ct = default) =>
        await ProbeAsync(radioIp, DefaultPort, timeout, ct).ConfigureAwait(false);

    internal async Task<Protocol3PresenceResult?> ProbeAsync(
        IPAddress radioIp,
        int port,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(radioIp);
        if (port <= 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (radioIp.AddressFamily != AddressFamily.InterNetwork) return null;

        var endpoint = new IPEndPoint(radioIp, port);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        DisableUdpConnReset(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var packet = BuildDiscoveryPacket();
        for (var attempt = 0; attempt < SendAttempts; attempt++)
        {
            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, endpoint, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "p3.presence.send.error radio={Radio}", radioIp);
                return null;
            }

            if (attempt + 1 < SendAttempts)
            {
                try { await Task.Delay(SendGap, timeoutCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
        }

        var receiveBuffer = new byte[ReceiveBufferSize];
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!timeoutCts.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await socket.ReceiveFromAsync(
                    receiveBuffer,
                    SocketFlags.None,
                    any,
                    timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (SocketException ex)
            {
                _log.LogDebug(ex, "p3.presence.socket.error radio={Radio}", radioIp);
                break;
            }

            var fromIp = ((IPEndPoint)res.RemoteEndPoint).Address;
            if (!fromIp.Equals(radioIp)) continue;

            var slice = new ReadOnlySpan<byte>(receiveBuffer, 0, res.ReceivedBytes);
            if (TryParseCapabilities(slice, port, out var result))
            {
                _log.LogInformation(
                    "p3.presence.reply radio={Radio} maxRx={MaxRx} flags=0x{Flags:X8}",
                    radioIp,
                    result.MaxRxStreams,
                    result.CapabilityFlags);
                return result;
            }
        }

        return null;
    }

    private static byte[] BuildDiscoveryPacket()
    {
        var packet = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), Magic);
        packet[4] = VersionMajor;
        packet[5] = VersionMinor;
        packet[6] = DiscoveryRequest;
        packet[7] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), HeaderBytes);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), 1);
        return packet;
    }

    private static bool TryParseCapabilities(ReadOnlySpan<byte> packet, int port, out Protocol3PresenceResult result)
    {
        result = default!;
        if (packet.Length < HeaderBytes + CapabilitiesBytes) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[..4]) != Magic) return false;
        if (packet[4] != VersionMajor || packet[5] != VersionMinor) return false;
        if (packet[6] != Capabilities) return false;

        var headerBytes = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(8, 2));
        var payloadBytes = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(10, 2));
        if (headerBytes != HeaderBytes || payloadBytes < CapabilitiesBytes) return false;
        if (packet.Length < headerBytes + payloadBytes) return false;

        var payload = packet.Slice(headerBytes, CapabilitiesBytes);
        result = new Protocol3PresenceResult(
            Port: port,
            MaxRxStreams: BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4)),
            MaxTxStreams: BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
            CapabilityFlags: BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(28, 4)),
            FirmwareVersion: BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(32, 4)),
            GatewareVersion: BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(36, 4)));
        return true;
    }

    private const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

    private static void DisableUdpConnReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); }
        catch (SocketException) { }
    }
}
