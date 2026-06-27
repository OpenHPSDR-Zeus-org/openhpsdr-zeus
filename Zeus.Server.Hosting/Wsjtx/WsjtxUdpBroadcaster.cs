// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net.Sockets;
using Zeus.Contracts;

namespace Zeus.Server.Wsjtx;

/// <summary>
/// Emits a single WSJT-X NetworkMessage type 12 (LoggedADIF) datagram when a QSO
/// is logged, so third-party loggers (JTAlert / Log4OM / GridTracker / N1MM)
/// pick up Zeus's native FT8/FT4 QSOs. The ADIF for the single entry is reused
/// from <see cref="LogService.ExportToAdifAsync"/>.
///
/// Wired ONLY at /api/log/entry (live + manual QSOs); ADIF bulk import does NOT
/// route through here, so re-importing a log never re-broadcasts. No-ops when
/// disabled (the default) — new network egress is opt-in. Cross-platform: pure
/// <see cref="UdpClient"/>, no native deps.
/// </summary>
public sealed class WsjtxUdpBroadcaster
{
    private readonly ILogger<WsjtxUdpBroadcaster> _log;
    private readonly WsjtxManagementService _mgmt;
    private readonly LogService _logService;

    public WsjtxUdpBroadcaster(
        ILogger<WsjtxUdpBroadcaster> log,
        WsjtxManagementService mgmt,
        LogService logService)
    {
        _log = log;
        _mgmt = mgmt;
        _logService = logService;
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

            using var udp = new UdpClient();
            await udp.SendAsync(datagram, datagram.Length, cfg.Host, cfg.Port).ConfigureAwait(false);

            _log.LogInformation(
                "wsjtx.broadcast call={Call} -> {Host}:{Port} bytes={Bytes}",
                entry.Callsign, cfg.Host, cfg.Port, datagram.Length);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wsjtx.broadcast failed call={Call}", entry.Callsign);
        }
    }
}
