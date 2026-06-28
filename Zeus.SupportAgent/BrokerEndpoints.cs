// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.SupportAgent;

/// <summary>
/// Resolves the broker's HTTPS origin and the presence/crash REST endpoints from
/// whatever broker URL the host hands the sidecar. The host passes the SAME value
/// the backend's RemoteBrokerClient uses — a WebSocket signaling URL such as
/// <c>wss://remote.openhpsdrzeus.com/signal?role=host</c> (or the
/// <c>ZEUS_REMOTE_BROKER_URL</c> override) — so this maps the <c>ws/wss</c> scheme
/// to <c>http/https</c> and drops the signaling path, yielding the plain origin
/// the <c>/presence/*</c> and <c>/crash</c> routes live under.
///
/// A null/blank/unparseable input yields null endpoints, which disables all
/// remote presence/crash sharing (Phase-1 local-only fallback) rather than
/// guessing a URL.
/// </summary>
public sealed class BrokerEndpoints
{
    private BrokerEndpoints(Uri origin)
    {
        Origin = origin;
        PresenceRegister = new Uri(origin, "/presence/register");
        PresenceHeartbeat = new Uri(origin, "/presence/heartbeat");
        PresenceDrop = new Uri(origin, "/presence/drop");
        Crash = new Uri(origin, "/crash");
    }

    /// <summary>The broker's HTTPS (or HTTP, for local dev) origin, e.g. <c>https://remote.openhpsdrzeus.com/</c>.</summary>
    public Uri Origin { get; }

    public Uri PresenceRegister { get; }
    public Uri PresenceHeartbeat { get; }
    public Uri PresenceDrop { get; }
    public Uri Crash { get; }

    /// <summary>
    /// Derive endpoints from a broker URL, or null if it is missing/unparseable.
    /// Accepts ws/wss/http/https; ws→http and wss→https. The path and query are
    /// discarded — only scheme + authority define the origin the REST routes hang
    /// off.
    /// </summary>
    public static BrokerEndpoints? FromBrokerUrl(string? brokerUrl)
    {
        if (string.IsNullOrWhiteSpace(brokerUrl)) return null;
        if (!Uri.TryCreate(brokerUrl.Trim(), UriKind.Absolute, out var uri)) return null;

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "wss" or "https" => "https",
            "ws" or "http" => "http",
            _ => null,
        };
        if (scheme is null) return null;

        var builder = new UriBuilder(scheme, uri.Host)
        {
            Path = "/",
            Query = string.Empty,
        };
        // Preserve a non-default port (e.g. a local `wrangler dev` on 8787).
        if (!uri.IsDefaultPort) builder.Port = uri.Port;

        return new BrokerEndpoints(builder.Uri);
    }
}
