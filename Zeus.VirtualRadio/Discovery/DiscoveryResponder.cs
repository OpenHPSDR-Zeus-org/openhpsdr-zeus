// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.VirtualRadio.Discovery;

/// <summary>
/// Listens on the radio port for Protocol-1 discovery requests
/// (<c>0xEF 0xFE 0x02 …</c>) and replies to the SENDER (an ephemeral host port,
/// not a fixed port) with a <see cref="P1DiscoveryReply"/> for the configured
/// board. Discovery skips loopback, so a profile bound to <c>127.0.0.1</c> is
/// direct-connect only and this responder never fires; bind a LAN IP to appear
/// in Zeus's discovery panel.
///
/// The wire-format work lives in the pure, socketless <see cref="TryBuildReply"/>
/// helper so it is unit-testable; <see cref="RunAsync"/> is the thin socket loop
/// around it. The Protocol-1 engine may instead call <see cref="TryBuildReply"/>
/// directly on its own shared port-1024 socket to avoid a second bind.
/// </summary>
public sealed class DiscoveryResponder
{
    /// <summary>The well-known HPSDR Protocol-1 discovery / command UDP port.</summary>
    public const int DiscoveryPort = 1024;

    /// <summary>
    /// Default gateware/code version advertised in the reply (firmware "7.3").
    /// For board byte 0x02 (HermesII / ANAN-10E) the value is informational and
    /// never affects board classification.
    /// </summary>
    public const byte DefaultCodeVersion = 73;

    // Locally-administered MAC (bit 0x02 set in the first octet) so the emulator
    // can never collide with a real HPSDR unit's burned-in OUI on the LAN.
    private static readonly byte[] DefaultMacBytes = { 0x02, 0x00, 0x00, 0x5A, 0xC0, 0x01 };

    /// <summary>The MAC the emulator advertises by default in discovery replies.</summary>
    public static PhysicalAddress DefaultMac => new(DefaultMacBytes);

    private readonly VirtualRadioProfile _profile;
    private readonly ILogger<DiscoveryResponder> _logger;
    private readonly PhysicalAddress _mac;
    private readonly byte _codeVersion;

    public DiscoveryResponder(VirtualRadioProfile profile, ILogger<DiscoveryResponder>? logger = null)
        : this(profile, DefaultMac, DefaultCodeVersion, logger)
    {
    }

    public DiscoveryResponder(
        VirtualRadioProfile profile,
        PhysicalAddress mac,
        byte codeVersion,
        ILogger<DiscoveryResponder>? logger = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _mac = mac ?? throw new ArgumentNullException(nameof(mac));
        _codeVersion = codeVersion;
        _logger = logger ?? NullLogger<DiscoveryResponder>.Instance;
    }

    /// <summary>
    /// Pure, socketless reply builder. Given the bytes of a received datagram,
    /// returns <see langword="true"/> and the discovery reply to send back to the
    /// sender when the datagram is a Protocol-1 discovery probe
    /// (<c>0xEF 0xFE 0x02 …</c>); otherwise returns <see langword="false"/>
    /// (e.g. an EP2 command frame or junk). The engine can call this on its own
    /// socket; tests call it directly with byte buffers.
    /// </summary>
    public bool TryBuildReply(ReadOnlySpan<byte> probe, out byte[] reply)
    {
        reply = Array.Empty<byte>();
        if (!IsDiscoveryProbe(probe))
            return false;

        var buffer = new byte[P1DiscoveryReply.ReplyLength];
        int written = P1DiscoveryReply.Build(buffer, _profile, _mac, _codeVersion, busy: false);
        reply = written == buffer.Length ? buffer : buffer[..written];
        return true;
    }

    /// <summary>
    /// True when <paramref name="datagram"/> is a Protocol-1 discovery probe:
    /// the <c>0xEF 0xFE</c> preamble followed by the <c>0x02</c> discovery
    /// command byte. A connect/start frame uses <c>0xEF 0xFE 0x04</c>, so the
    /// third byte discriminates discovery from a command.
    /// </summary>
    public static bool IsDiscoveryProbe(ReadOnlySpan<byte> datagram) =>
        datagram.Length >= 3 && datagram[0] == 0xEF && datagram[1] == 0xFE && datagram[2] == 0x02;

    /// <summary>
    /// Bind the discovery socket and serve replies until cancelled. Returns
    /// immediately (after logging) when the profile is bound to a loopback
    /// address, because Zeus's discovery never probes loopback — a loopback
    /// profile is direct-connect only and the engine owns port 1024 there.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (IPAddress.IsLoopback(_profile.BindAddress))
        {
            _logger.LogInformation(
                "vradio.discovery skipped: bind {Bind} is loopback (direct-connect only; discovery does not probe loopback).",
                _profile.BindAddress);
            return;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(_profile.BindAddress, DiscoveryPort));
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex,
                "vradio.discovery bind failed on {Bind}:{Port} — another HPSDR app may be running.",
                _profile.BindAddress, DiscoveryPort);
            return;
        }

        _logger.LogInformation(
            "vradio.discovery listening on {Bind}:{Port} as board {Board} (mac {Mac}, ver {Ver}).",
            _profile.BindAddress, DiscoveryPort, _profile.Board, _mac, _codeVersion);

        var buffer = new byte[2048];
        var any = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult rx;
            try
            {
                rx = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "vradio.discovery receive error (continuing).");
                continue;
            }

            if (!TryBuildReply(buffer.AsSpan(0, rx.ReceivedBytes), out byte[] reply))
                continue; // not a discovery probe (e.g. a command frame) — ignore here.

            try
            {
                // Reply to the SENDER's ephemeral port, never a fixed port.
                await socket.SendToAsync(reply, SocketFlags.None, rx.RemoteEndPoint, ct).ConfigureAwait(false);
                _logger.LogInformation("vradio.discovery replied to {Peer} as {Board}.",
                    rx.RemoteEndPoint, _profile.Board);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "vradio.discovery send error to {Peer} (continuing).", rx.RemoteEndPoint);
            }
        }
    }
}
