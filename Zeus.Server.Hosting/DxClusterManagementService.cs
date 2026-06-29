// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Server.DxCluster;

namespace Zeus.Server;

/// <summary>
/// Config + status facade and lifecycle owner for the native Telnet DX-cluster
/// client. Mirrors TciManagementService's shape (config persistence + status
/// reporting) but, because the cluster is an OUTBOUND connection (not a Kestrel
/// listener), it can connect/disconnect at runtime with no restart.
///
/// IHostedService: on startup it auto-connects only when the operator persisted
/// Enabled AND AutoConnect — otherwise it stays idle until an explicit
/// POST /api/dxcluster/connect. On shutdown it tears the client down cleanly.
/// </summary>
public sealed class DxClusterManagementService : IHostedService, IDisposable
{
    private readonly ILogger<DxClusterManagementService> _log;
    private readonly DxClusterConfigStore _store;
    private readonly DxClusterClient _client;
    private readonly object _sync = new();
    private DxClusterConfig _config;

    public DxClusterManagementService(
        ILogger<DxClusterManagementService> log,
        DxClusterConfigStore store,
        DxClusterClient client)
    {
        _log = log;
        _store = store;
        _client = client;
        // Default OFF when nothing persisted — new network egress is opt-in.
        _config = store.Get() ?? new DxClusterConfig();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var cfg = GetConfig();
        if (cfg.Enabled && cfg.AutoConnect && CanConnect(cfg))
        {
            _log.LogInformation("dxcluster.autoconnect host={Host} port={Port}", cfg.Host, cfg.Port);
            _client.Start(cfg);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync().ConfigureAwait(false);
    }

    public DxClusterConfig GetConfig()
    {
        lock (_sync) return _config;
    }

    public DxClusterStatus GetStatus()
    {
        var c = GetConfig();
        return new DxClusterStatus(
            Enabled: c.Enabled,
            Host: c.Host,
            Port: c.Port,
            Callsign: c.Callsign,
            HasPassword: !string.IsNullOrEmpty(c.Password),
            LoginCommands: c.LoginCommands,
            AutoConnect: c.AutoConnect,
            State: _client.State,
            SpotsReceived: _client.SpotsReceived,
            LastSpotCallsign: _client.LastSpotCallsign,
            Error: _client.LastError);
    }

    /// <summary>Persist a new config. If the client is running, restart it so the
    /// new host/callsign/etc. take effect immediately (no restart needed).</summary>
    public DxClusterStatus SetConfig(DxClusterConfig config)
    {
        var normalized = Normalize(config);
        lock (_sync) _config = normalized;

        try { _store.Set(normalized); }
        catch (Exception ex) { _log.LogWarning(ex, "dxcluster.config.persist failed"); }

        _log.LogInformation(
            "dxcluster.config.updated enabled={En} host={Host} port={Port} call={Call} auto={Auto}",
            normalized.Enabled, normalized.Host, normalized.Port, normalized.Callsign, normalized.AutoConnect);

        // Reconcile: if it was running, restart on the new settings; if it is now
        // disabled, stop. We do NOT auto-start here on a mere enable — connecting
        // is an explicit operator action (POST /connect) unless AutoConnect drove
        // it at startup.
        if (_client.IsRunning)
        {
            if (!normalized.Enabled || !CanConnect(normalized))
            {
                _ = _client.StopAsync();
            }
            else
            {
                _ = RestartAsync(normalized);
            }
        }

        return GetStatus();
    }

    /// <summary>Explicit operator connect. No-op if already running or not
    /// connectable.</summary>
    public DxClusterStatus Connect()
    {
        var cfg = GetConfig();
        if (!cfg.Enabled)
        {
            _log.LogInformation("dxcluster.connect ignored — not enabled");
            return GetStatus();
        }
        if (!CanConnect(cfg))
        {
            _log.LogInformation("dxcluster.connect ignored — host/callsign not set");
            return GetStatus();
        }
        _client.Start(cfg);
        return GetStatus();
    }

    /// <summary>Explicit operator disconnect.</summary>
    public DxClusterStatus Disconnect()
    {
        _ = _client.StopAsync();
        return GetStatus();
    }

    private async Task RestartAsync(DxClusterConfig cfg)
    {
        try
        {
            await _client.StopAsync().ConfigureAwait(false);
            _client.Start(cfg);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dxcluster.restart failed");
        }
    }

    private static bool CanConnect(DxClusterConfig c) =>
        !string.IsNullOrWhiteSpace(c.Host)
        && c.Port is > 0 and < 65536
        && !string.IsNullOrWhiteSpace(c.Callsign);

    private static DxClusterConfig Normalize(DxClusterConfig c) => new(
        Enabled: c.Enabled,
        Host: (c.Host ?? "").Trim(),
        Port: c.Port is > 0 and < 65536 ? c.Port : 7373,
        Callsign: (c.Callsign ?? "").Trim().ToUpperInvariant(),
        Password: c.Password ?? "",
        LoginCommands: (c.LoginCommands ?? "").Trim(),
        AutoConnect: c.AutoConnect);

    public void Dispose() => _client.Dispose();
}
