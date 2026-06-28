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
        return new WsjtxStatus(
            c.Enabled, c.Host, c.Port, c.InstanceId,
            c.Transport, c.MulticastGroup, c.MulticastTtl, c.SendQsoLogged, c.SendLiveDecodes);
    }

    public WsjtxStatus SetConfig(WsjtxRuntimeConfig config)
    {
        bool multicast = string.Equals(config.Transport?.Trim(), "multicast", StringComparison.OrdinalIgnoreCase);

        // Validate the multicast group: must parse as an IPv4 address in
        // 224.0.0.0–239.255.255.255. A bad/empty group falls back to 224.0.0.73 so
        // a typo can never silently send to a unicast/garbage host as if multicast.
        string group = "224.0.0.73";
        if (!string.IsNullOrWhiteSpace(config.MulticastGroup) &&
            IsMulticastIPv4(config.MulticastGroup.Trim()))
        {
            group = config.MulticastGroup.Trim();
        }

        var normalized = new WsjtxRuntimeConfig(
            Enabled: config.Enabled,
            Host: string.IsNullOrWhiteSpace(config.Host) ? "127.0.0.1" : config.Host.Trim(),
            Port: config.Port is > 0 and < 65536 ? config.Port : 2237,
            InstanceId: string.IsNullOrWhiteSpace(config.InstanceId) ? "WSJT-X" : config.InstanceId.Trim(),
            Transport: multicast ? "multicast" : "unicast",
            MulticastGroup: group,
            MulticastTtl: Math.Clamp(config.MulticastTtl, 1, 255),
            SendQsoLogged: config.SendQsoLogged,
            SendLiveDecodes: config.SendLiveDecodes);

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
            "wsjtx.config.updated enabled={Enabled} transport={Transport} host={Host} port={Port} group={Group} ttl={Ttl} type5={T5} live={Live}",
            normalized.Enabled, normalized.Transport, normalized.Host, normalized.Port,
            normalized.MulticastGroup, normalized.MulticastTtl, normalized.SendQsoLogged, normalized.SendLiveDecodes);

        return GetStatus();
    }

    // True iff the string parses as an IPv4 address in the multicast range
    // 224.0.0.0–239.255.255.255 (first octet 224..239).
    internal static bool IsMulticastIPv4(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        byte first = ip.GetAddressBytes()[0];
        return first is >= 224 and <= 239;
    }
}
