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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Zeus.Contracts;
using Zeus.Server.Tci;

namespace Zeus.Server;

/// <summary>
/// Management service for TCI server configuration and status reporting.
/// Since TCI uses Kestrel port binding (configured at startup), runtime
/// changes require app restart. This service persists config changes via
/// TciConfigStore (LiteDB) and reports current status including port
/// availability.
/// </summary>
public sealed class TciManagementService
{
    private readonly ILogger<TciManagementService> _log;
    private readonly TciServer _tciServer;
    private readonly TciOptions _startupOptions;
    private readonly TciConfigStore _store;

    // In-memory pending config (what the user wants for next restart)
    private TciRuntimeConfig _pendingConfig;

    public TciManagementService(
        ILogger<TciManagementService> log,
        TciServer tciServer,
        IOptions<TciOptions> options,
        TciConfigStore store)
    {
        _log = log;
        _tciServer = tciServer;
        _startupOptions = options.Value;
        _store = store;

        // Load or initialize pending config from the persisted store. When
        // nothing is persisted yet, mirror the startup options so a "no
        // change" GetStatus() reports RequiresRestart=false.
        _pendingConfig = _store.Get() ?? new TciRuntimeConfig(
            Enabled: _startupOptions.Enabled,
            BindAddress: _startupOptions.BindAddress,
            Port: _startupOptions.Port);
    }

    public TciStatus GetStatus()
    {
        var currentlyEnabled = _startupOptions.Enabled;
        var currentPort = _startupOptions.Port;
        var currentBindAddress = _startupOptions.BindAddress;
        var clientCount = _tciServer.ClientCount;

        // The (single) TciServer is the listener for this port — when it is
        // running, probe-binding the same port from this status method always
        // fails with "address already in use" and surfaces a false-positive
        // warning in the UI. Skip the probe; report port-availability as true
        // whenever startup is configured to enable TCI. The "is this port free
        // to switch to?" question is handled separately by TestPort, which the
        // settings panel calls before saving a new bindAddress/port.
        bool portAvailable = true;
        string? error = null;

        // Check if pending config differs from startup config (requires restart)
        var requiresRestart = _pendingConfig.Enabled != currentlyEnabled
                            || _pendingConfig.Port != currentPort
                            || _pendingConfig.BindAddress != currentBindAddress;

        return new TciStatus(
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

    public TciStatus SetConfig(TciRuntimeConfig config)
    {
        // Validate and normalize
        var normalized = new TciRuntimeConfig(
            Enabled: config.Enabled,
            BindAddress: string.IsNullOrWhiteSpace(config.BindAddress)
                ? "127.0.0.1"
                : config.BindAddress.Trim(),
            Port: config.Port is > 0 and < 65536 ? config.Port : 40001);

        _pendingConfig = normalized;
        try
        {
            _store.Set(normalized);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tci.config.persist failed");
        }

        _log.LogInformation(
            "tci.config.updated enabled={Enabled} bind={Bind} port={Port} (restart required)",
            normalized.Enabled, normalized.BindAddress, normalized.Port);

        return GetStatus();
    }

    public TciTestResult TestPort(string bindAddress, int port)
    {
        if (port is <= 0 or >= 65536)
            return new TciTestResult(Ok: false, Error: "Port must be between 1 and 65535");

        var addr = string.IsNullOrWhiteSpace(bindAddress) ? "127.0.0.1" : bindAddress.Trim();

        if (!IsPortAvailable(addr, port))
        {
            return new TciTestResult(
                Ok: false,
                Error: $"Port {port} is already in use on {addr}");
        }

        return new TciTestResult(Ok: true, Error: null);
    }

    private bool IsPortAvailable(string bindAddress, int port)
    {
        try
        {
            // Normalize bind address
            IPAddress ip;
            if (bindAddress is "0.0.0.0" or "*" or "")
            {
                ip = IPAddress.Any;
            }
            else if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                ip = IPAddress.Loopback;
            }
            else if (!IPAddress.TryParse(bindAddress, out var parsed))
            {
                return false; // Invalid address
            }
            else
            {
                ip = parsed;
            }

            // Try to bind to the port
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
