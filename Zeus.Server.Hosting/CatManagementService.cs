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
using Microsoft.Extensions.Options;
using Zeus.Contracts;
using Zeus.Server.Cat;

namespace Zeus.Server;

/// <summary>
/// Management service for CAT server configuration and status. Mirrors
/// <see cref="TciManagementService"/>. CAT owns its own TcpListener, so a live
/// rebind is technically possible, but for parity (and to keep the surface
/// minimal) port/bind changes are persisted and applied on the next restart —
/// the UI reports RequiresRestart, exactly like TCI. Hot-rebind is a documented
/// low-risk follow-up.
/// </summary>
public sealed class CatManagementService
{
    private readonly ILogger<CatManagementService> _log;
    private readonly CatServer _catServer;
    private readonly CatOptions _startupOptions;
    private readonly CatConfigStore _store;

    private CatRuntimeConfig _pendingConfig;

    public CatManagementService(
        ILogger<CatManagementService> log,
        CatServer catServer,
        IOptions<CatOptions> options,
        CatConfigStore store)
    {
        _log = log;
        _catServer = catServer;
        _startupOptions = options.Value;
        _store = store;

        _pendingConfig = _store.Get() ?? new CatRuntimeConfig(
            Enabled: _startupOptions.Enabled,
            BindAddress: _startupOptions.BindAddress,
            Port: _startupOptions.Port);
    }

    public CatStatus GetStatus()
    {
        var currentlyEnabled = _startupOptions.Enabled;
        var currentPort = _startupOptions.Port;
        var currentBindAddress = _startupOptions.BindAddress;
        var clientCount = _catServer.ClientCount;

        // The running CatServer is the listener for this port — probe-binding
        // the same port here always fails with "address already in use" and
        // surfaces a false-positive in the UI. Skip it; "is this port free to
        // switch to?" is answered by TestPort, which the panel calls before a
        // bind/port change. (Same lesson as TciManagementService.)
        bool portAvailable = true;
        string? error = null;

        var requiresRestart = _pendingConfig.Enabled != currentlyEnabled
                            || _pendingConfig.Port != currentPort
                            || _pendingConfig.BindAddress != currentBindAddress;

        return new CatStatus(
            CurrentlyEnabled: currentlyEnabled,
            CurrentPort: currentPort,
            CurrentBindAddress: currentBindAddress,
            PendingEnabled: _pendingConfig.Enabled,
            PendingPort: _pendingConfig.Port,
            PendingBindAddress: _pendingConfig.BindAddress,
            ClientCount: clientCount,
            PortAvailable: portAvailable,
            RequiresRestart: requiresRestart,
            Error: error);
    }

    public CatStatus SetConfig(CatRuntimeConfig config)
    {
        var normalized = new CatRuntimeConfig(
            Enabled: config.Enabled,
            BindAddress: string.IsNullOrWhiteSpace(config.BindAddress)
                ? "127.0.0.1"
                : config.BindAddress.Trim(),
            Port: config.Port is > 0 and < 65536 ? config.Port : 19090);

        _pendingConfig = normalized;
        try
        {
            _store.Set(normalized);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cat.config.persist failed");
        }

        _log.LogInformation(
            "cat.config.updated enabled={Enabled} bind={Bind} port={Port} (restart required)",
            normalized.Enabled, normalized.BindAddress, normalized.Port);

        return GetStatus();
    }

    public CatTestResult TestPort(string bindAddress, int port)
    {
        if (port is <= 0 or >= 65536)
            return new CatTestResult(Ok: false, Error: "Port must be between 1 and 65535");

        var addr = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1" : bindAddress.Trim();

        if (!IsPortAvailable(addr, port))
            return new CatTestResult(Ok: false, Error: $"Port {port} is already in use on {addr}");

        return new CatTestResult(Ok: true, Error: null);
    }

    private static bool IsPortAvailable(string bindAddress, int port)
    {
        try
        {
            IPAddress ip;
            if (bindAddress is "0.0.0.0" or "*" or "")
                ip = IPAddress.Any;
            else if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
                ip = IPAddress.Loopback;
            else if (!IPAddress.TryParse(bindAddress, out var parsed))
                return false;
            else
                ip = parsed;

            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(ip, port));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
