// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net;
using System.Net.Sockets;
using Zeus.Contracts;

namespace Zeus.Server.Wsjtx;

/// <summary>
/// Sends WSJT-X NetworkMessage datagrams so third-party loggers (JTAlert / Log4OM
/// / GridTracker / N1MM / JTDX) pick up Zeus's native FT8/FT4/WSPR activity.
///
/// Two egress paths share one transport:
///   * <see cref="BroadcastLoggedQsoAsync"/> — a LoggedADIF (type 12), and
///     optionally a structured QSOLogged (type 5), on each logged QSO. Wired ONLY
///     at /api/log/entry (live + manual QSOs); ADIF bulk import does NOT route
///     here, so re-importing a log never re-broadcasts.
///   * <see cref="SendDatagramAsync"/> — used by <see cref="WsjtxLiveEmitter"/>
///     for the live Heartbeat/Status/Decode/WSPRDecode stream.
///
/// SEND-ONLY: there is no inbound socket anywhere in this namespace. Zeus never
/// honours Reply(4)/HaltTx(8)/FreeText(9) — those are network TX-triggers into a
/// real PA. No-ops when disabled (the default) — new network egress is opt-in.
/// Transport is unicast by default, or multicast (group + TTL) when configured so
/// MULTIPLE loggers can receive the same stream. Cross-platform: pure
/// <see cref="UdpClient"/>, no native deps.
///
/// One long-lived send socket is cached for the broadcaster's lifetime (registered
/// singleton). <see cref="UdpClient.SendAsync(byte[],int,string,int)"/> takes the
/// destination per call, so the SAME socket serves unicast and multicast and every
/// host/port change without re-allocation — the only per-config state is the
/// multicast TTL, which is (re)applied lazily only when it actually changes. Sends
/// are serialised through a semaphore so the fire-and-forget live stream (tens of
/// Decode datagrams per FT8 slot, plus Status/Heartbeat) never issues concurrent
/// SendAsync on a single socket. This removes the per-datagram ephemeral-socket
/// churn the old throwaway-per-send model incurred on the live hot path.
/// </summary>
public sealed class WsjtxUdpBroadcaster : IDisposable
{
    private readonly ILogger<WsjtxUdpBroadcaster> _log;
    private readonly WsjtxManagementService _mgmt;
    private readonly LogService _logService;
    private readonly SpottingManagementService? _operator;

    // One cached send socket for the broadcaster's lifetime. Created lazily on the
    // first enabled send so the disabled-default path allocates no socket at all.
    // _sendGate serialises sends (a shared socket must not see concurrent SendAsync
    // from the fire-and-forget live stream); _appliedTtl tracks the multicast TTL
    // currently set on the socket so we only call SetSocketOption on change.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private UdpClient? _udp;
    private int _appliedTtl = -1;
    private bool _disposed;

    public WsjtxUdpBroadcaster(
        ILogger<WsjtxUdpBroadcaster> log,
        WsjtxManagementService mgmt,
        LogService logService,
        // Optional so the existing broadcaster tests (which exercise the type-12
        // path only) keep their 3-arg construction. Supplies operator call/grid for
        // the MY_* fields of the optional QSOLogged (type 5) message.
        SpottingManagementService? operatorIdentity = null)
    {
        _log = log;
        _mgmt = mgmt;
        _logService = logService;
        _operator = operatorIdentity;
    }

    /// <summary>Broadcast one logged QSO. No-op when the broadcaster is disabled;
    /// never throws (a send failure must not break the log POST).</summary>
    public async Task BroadcastLoggedQsoAsync(LogEntry entry, CancellationToken ct = default)
    {
        var cfg = _mgmt.GetConfig();
        if (!cfg.Enabled) return;

        try
        {
            var adif = await _logService.ExportToAdifAsync(new[] { entry.Id }, ct);
            var datagram = WsjtxMessage.EncodeLoggedAdif(cfg.InstanceId, adif);
            await SendInternalAsync(cfg, datagram, ct).ConfigureAwait(false);

            if (cfg.SendQsoLogged)
            {
                var (call, grid) = _operator?.ResolveOperator() ?? ("", "");
                var qso = WsjtxMessage.EncodeQsoLogged(
                    instanceId: cfg.InstanceId,
                    dateTimeOffUtc: entry.QsoDateTimeUtc,
                    dxCall: entry.Callsign ?? "",
                    dxGrid: entry.Grid ?? "",
                    txFrequencyHz: entry.FrequencyMhz is { } mhz && mhz > 0
                        ? (ulong)Math.Round(mhz * 1_000_000.0)
                        : 0UL,
                    mode: entry.Mode ?? "",
                    reportSent: entry.RstSent ?? "",
                    reportReceived: entry.RstRcvd ?? "",
                    txPower: "",
                    comments: entry.Comment ?? "",
                    name: entry.Name ?? "",
                    dateTimeOnUtc: entry.QsoDateTimeUtc,
                    operatorCall: call,
                    myCall: call,
                    myGrid: grid,
                    exchangeSent: "",
                    exchangeReceived: "",
                    adifPropagationMode: "");
                await SendInternalAsync(cfg, qso, ct).ConfigureAwait(false);
            }

            _log.LogInformation(
                "wsjtx.broadcast call={Call} -> {Transport} {Host}:{Port} bytes={Bytes} type5={T5}",
                entry.Callsign, cfg.Transport, TargetHost(cfg), cfg.Port, datagram.Length, cfg.SendQsoLogged);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wsjtx.broadcast failed call={Call}", entry.Callsign);
        }
    }

    /// <summary>Send a pre-encoded datagram on the live stream (Heartbeat / Status
    /// / Decode / WSPRDecode). No-op when disabled; never throws. The caller has
    /// already gated on <see cref="WsjtxRuntimeConfig.SendLiveDecodes"/>.</summary>
    public async Task SendDatagramAsync(byte[] datagram, CancellationToken ct = default)
    {
        var cfg = _mgmt.GetConfig();
        if (!cfg.Enabled) return;
        try
        {
            await SendInternalAsync(cfg, datagram, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "wsjtx.live send failed bytes={Bytes}", datagram.Length);
        }
    }

    private static string TargetHost(WsjtxRuntimeConfig cfg) =>
        IsMulticast(cfg) ? cfg.MulticastGroup : cfg.Host;

    private static bool IsMulticast(WsjtxRuntimeConfig cfg) =>
        string.Equals(cfg.Transport, "multicast", StringComparison.OrdinalIgnoreCase);

    // One cached transport for both egress paths. The socket is destination-agnostic
    // (the endpoint is passed per SendAsync), so it is reused across unicast/multicast
    // and every host/port change; only the multicast TTL is reapplied on change. Sends
    // are serialised so a shared socket never sees concurrent SendAsync.
    private async Task SendInternalAsync(WsjtxRuntimeConfig cfg, byte[] datagram, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var udp = _udp ??= new UdpClient();
            if (IsMulticast(cfg))
            {
                // Scope the multicast TTL (hop limit). Send-only multicast needs no
                // JoinMulticastGroup — the group address is just the destination.
                // Apply only when it changes; the option persists on the socket.
                int ttl = Math.Clamp(cfg.MulticastTtl, 1, 255);
                if (ttl != _appliedTtl)
                {
                    udp.Client.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.MulticastTimeToLive,
                        ttl);
                    _appliedTtl = ttl;
                }
                await udp.SendAsync(datagram, datagram.Length, cfg.MulticastGroup, cfg.Port)
                    .WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            else
            {
                await udp.SendAsync(datagram, datagram.Length, cfg.Host, cfg.Port)
                    .WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _udp?.Dispose();
        _sendGate.Dispose();
    }
}
