// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Owns the live config for the WSJT-X logged-QSO broadcaster. Unlike CAT/TCI
/// (which hold a TcpListener and need a restart to rebind), the broadcaster only
/// SENDS UDP, so config changes apply immediately — there is no current/pending
/// split and no RequiresRestart. Persists through <see cref="WsjtxConfigStore"/>.
/// </summary>
public sealed class WsjtxManagementService
{
    private readonly ILogger<WsjtxManagementService> _log;
    private readonly WsjtxConfigStore _store;
    private readonly object _sync = new();
    private WsjtxRuntimeConfig _config;

    public WsjtxManagementService(ILogger<WsjtxManagementService> log, WsjtxConfigStore store)
    {
        _log = log;
        _store = store;
        // Default OFF / loopback when nothing is persisted — new network egress
        // is opt-in only.
        _config = _store.Get() ?? new WsjtxRuntimeConfig();
    }

    public WsjtxRuntimeConfig GetConfig()
    {
        lock (_sync) return _config;
    }

    public WsjtxStatus GetStatus()
    {
        var c = GetConfig();
        return new WsjtxStatus(c.Enabled, c.Host, c.Port, c.InstanceId);
    }

    public WsjtxStatus SetConfig(WsjtxRuntimeConfig config)
    {
        var normalized = new WsjtxRuntimeConfig(
            Enabled: config.Enabled,
            Host: string.IsNullOrWhiteSpace(config.Host) ? "127.0.0.1" : config.Host.Trim(),
            Port: config.Port is > 0 and < 65536 ? config.Port : 2237,
            InstanceId: string.IsNullOrWhiteSpace(config.InstanceId) ? "WSJT-X" : config.InstanceId.Trim());

        lock (_sync) _config = normalized;

        try
        {
            _store.Set(normalized);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wsjtx.config.persist failed");
        }

        _log.LogInformation(
            "wsjtx.config.updated enabled={Enabled} host={Host} port={Port}",
            normalized.Enabled, normalized.Host, normalized.Port);

        return GetStatus();
    }
}
