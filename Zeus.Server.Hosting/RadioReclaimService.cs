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
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Forcibly frees an OpenHPSDR radio that discovery reports as <c>Busy</c>
/// (status byte 0x03) — i.e. one currently streaming to another client.
///
/// <para>Neither HPSDR protocol authenticates the host: the radio streams to
/// whoever last commanded it to run. To take a busy radio over we send it a
/// <em>stop</em> command, which drops the previous owner, before connecting
/// normally. This deliberately reverses the historical "Zeus will not fight
/// for it" behaviour and is gated behind an explicit operator confirmation in
/// the Connect panel — it can kick another operator off a live, possibly
/// transmitting, radio.</para>
///
/// <list type="bullet">
///   <item>Protocol 1 — Metis start/stop command <c>EF FE 04 00</c> to UDP
///   port 1024 (the same frame <see cref="ControlFrame.BuildStartStop"/>
///   builds with <c>start: false</c>).</item>
///   <item>Protocol 2 — a high-priority "to radio" datagram with the run/PTT
///   byte cleared, sent to UDP port 1027. An all-zero buffer is exactly
///   <c>run=0, ptt=0, seq=0</c> — see Protocol2Client.SendCmdHighPriority.</item>
/// </list>
///
/// Each command is sent a few times because the first UDP datagram to a
/// freshly-ARPed host is frequently dropped (the same first-UDP-drop quirk
/// Protocol1Client works around on start). The service is stateless and opens
/// its own transient socket so it never disturbs an active connection.
/// </summary>
public sealed class RadioReclaimService
{
    // Fixed protocol command ports. These are NOT the data port carried in the
    // discovery endpoint string — P1 control frames always go to 1024 and P2
    // high-priority always to 1027, regardless of the data port.
    private const int Protocol1CommandPort = 1024;
    private const int Protocol2HighPriorityPort = 1027;

    // Matches Protocol2Client.BufLen — the size Zeus's own high-priority
    // sender uses on the wire.
    private const int Protocol2HighPriorityLen = 1444;

    private const int SendCount = 3;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(350);

    private readonly ILogger<RadioReclaimService> _log;

    public RadioReclaimService(ILogger<RadioReclaimService> log) => _log = log;

    /// <summary>
    /// Sends a stop/free command to <paramref name="radioIp"/> so the radio
    /// drops its current owner, then waits briefly for the radio to settle
    /// before the caller re-discovers or connects.
    /// </summary>
    public async Task ReclaimAsync(IPAddress radioIp, bool isProtocol2, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(radioIp);

        if (isProtocol2) SendProtocol2Stop(radioIp);
        else SendProtocol1Stop(radioIp);

        // Give the radio a moment to drop the previous owner and republish an
        // idle discovery status before the caller's connect/start sequence.
        await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
    }

    private void SendProtocol1Stop(IPAddress radioIp) =>
        SendUdp(radioIp, Protocol1CommandPort, BuildProtocol1StopFrame(), "P1");

    private void SendProtocol2Stop(IPAddress radioIp) =>
        SendUdp(radioIp, Protocol2HighPriorityPort, BuildProtocol2StopFrame(), "P2");

    /// <summary>Metis stop frame (<c>EF FE 04 00</c>, 64 bytes). Internal for tests.</summary>
    internal static byte[] BuildProtocol1StopFrame()
    {
        var frame = new byte[64];
        ControlFrame.BuildStartStop(frame, start: false);
        return frame;
    }

    /// <summary>
    /// P2 high-priority stop: an all-zero <see cref="Protocol2HighPriorityLen"/>-byte
    /// buffer (run/PTT cleared at byte 4, sequence 0 at bytes 0..3). Internal for tests.
    /// </summary>
    internal static byte[] BuildProtocol2StopFrame() => new byte[Protocol2HighPriorityLen];

    private void SendUdp(IPAddress radioIp, int port, byte[] payload, string proto)
    {
        var target = new IPEndPoint(radioIp, port);
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Bind(new IPEndPoint(IPAddress.Any, 0));
            for (int i = 0; i < SendCount; i++)
            {
                try { sock.SendTo(payload, target); }
                catch (SocketException ex)
                {
                    _log.LogWarning(ex, "reclaim {Proto} stop send {I}/{N} to {Target} failed", proto, i + 1, SendCount, target);
                }
            }
            _log.LogInformation("reclaim {Proto} stop sent to {Target} ({N}×)", proto, target, SendCount);
        }
        catch (SocketException ex)
        {
            _log.LogWarning(ex, "reclaim {Proto} socket setup for {Target} failed", proto, target);
        }
    }
}
