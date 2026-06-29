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
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Zeus.Protocol1.Discovery;

public sealed class RadioDiscoveryService : IRadioDiscovery
{
    private const int HpsdrPort = 1024;
    private const int DiscoveryPacketLength = 63;
    private const int ReceiveBufferSize = 2048;
    private const int MacOsSendAttempts = 3;
    private static readonly TimeSpan SendGap = TimeSpan.FromMilliseconds(50);

    private readonly ILogger<RadioDiscoveryService> _log;

    public RadioDiscoveryService(ILogger<RadioDiscoveryService> log)
    {
        _log = log;
    }

    public async Task<IReadOnlyList<DiscoveredRadio>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var broadcastTargets = GetBroadcastTargets();

        _log.LogDebug("discovery.start interfaces={Count}", broadcastTargets.Count);
        foreach (var (ifaceAddr, bcastAddr) in broadcastTargets)
        {
            _log.LogDebug("discovery.interface local={Local} broadcast={Broadcast}", ifaceAddr, bcastAddr);
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };
        DisableUdpConnReset(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        var packet = BuildDiscoveryPacket();
        await SendProbesAsync(socket, packet, broadcastTargets, ct).ConfigureAwait(false);

        var byMac = new Dictionary<PhysicalAddress, DiscoveredRadio>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var receiveBuffer = new byte[ReceiveBufferSize];
        var any = new IPEndPoint(IPAddress.Any, 0);

        try
        {
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
                    // Windows WSAECONNRESET (10054): stray ICMP port-unreachable.
                    // Keep collecting replies until the timeout fires.
                    continue;
                }
                catch (SocketException ex)
                {
                    _log.LogWarning(ex, "discovery.socket.error");
                    break;
                }

                var fromIp = ((IPEndPoint)res.RemoteEndPoint).Address;
                var slice = new ReadOnlySpan<byte>(receiveBuffer, 0, res.ReceivedBytes);

                if (!ReplyParser.TryParse(slice, fromIp, out var radio))
                {
                    _log.LogDebug(
                        "discovery.reply.invalid from={Ip} len={Len}",
                        fromIp,
                        res.ReceivedBytes);
                    continue;
                }

                byMac[radio.Mac] = radio;
                _log.LogInformation(
                    "discovery.reply from={Ip} board={Board} mac={Mac} fw={Firmware}",
                    radio.Ip,
                    radio.Board,
                    radio.Mac,
                    radio.FirmwareString);
            }
        }
        finally
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
        }

        ct.ThrowIfCancellationRequested();

        return byMac.Values.OrderBy(IpSortKey).ToList();
    }

    private async Task SendProbesAsync(
        Socket socket,
        ReadOnlyMemory<byte> packet,
        IReadOnlyList<(IPAddress ifaceAddr, IPAddress bcastAddr)> broadcastTargets,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < MacOsSendAttempts; attempt++)
        {
            // Send to each interface's broadcast address
            foreach (var (_, bcastAddr) in broadcastTargets)
            {
                var endpoint = new IPEndPoint(bcastAddr, HpsdrPort);
                try
                {
                    await socket.SendToAsync(packet, SocketFlags.None, endpoint, ct).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    _log.LogWarning(ex, "discovery.send.error broadcast={Broadcast}", bcastAddr);
                }
            }

            if (attempt < MacOsSendAttempts - 1)
            {
                try
                {
                    await Task.Delay(SendGap, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private IReadOnlyList<(IPAddress ifaceAddr, IPAddress bcastAddr)> GetBroadcastTargets()
    {
        var targets = new List<(IPAddress, IPAddress)>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var iface in interfaces)
            {
                // Skip inactive, loopback, and tunnel interfaces
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                var ipProps = iface.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    // Only IPv4 addresses
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    // Calculate broadcast address from IP and netmask
                    var ip = unicast.Address;
                    var mask = unicast.IPv4Mask;

                    // Skip if no valid netmask
                    if (mask == null || mask.Equals(IPAddress.Any))
                        continue;

                    var ipBytes = ip.GetAddressBytes();
                    var maskBytes = mask.GetAddressBytes();
                    var bcastBytes = new byte[4];

                    for (var i = 0; i < 4; i++)
                    {
                        bcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                    }

                    var bcastAddr = new IPAddress(bcastBytes);
                    targets.Add((ip, bcastAddr));
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "discovery.enumerate.error");
        }

        // Fallback to global broadcast if no interfaces found
        if (targets.Count == 0)
        {
            _log.LogWarning("discovery.enumerate.empty, using global broadcast");
            targets.Add((IPAddress.Any, IPAddress.Broadcast));
        }

        return targets;
    }

    private static byte[] BuildDiscoveryPacket()
    {
        var buf = new byte[DiscoveryPacketLength];
        buf[0] = 0xEF;
        buf[1] = 0xFE;
        buf[2] = 0x02;
        return buf;
    }

    private static uint IpSortKey(DiscoveredRadio r)
    {
        var bytes = r.Ip.GetAddressBytes();
        if (bytes.Length != 4) return uint.MaxValue;
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    // Private copy — Protocol1 is a separate assembly from Protocol2.
    // Windows surfaces ICMP port-unreachable as SocketError 10054 on the next recv;
    // this ioctl disables that behaviour at the OS level.
    private const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

    private static void DisableUdpConnReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { s.IOControl(SIO_UDP_CONNRESET, new byte[4], null); }
        catch (SocketException) { /* best effort */ }
    }
}
