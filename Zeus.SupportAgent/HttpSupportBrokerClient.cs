// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Zeus.Support.Contracts;

namespace Zeus.SupportAgent;

/// <summary>
/// Production <see cref="ISupportBrokerClient"/>: QRZ-authenticated POSTs to the
/// broker's <c>/presence/*</c> and <c>/crash</c> routes. Mirrors the host side of
/// signaling (<c>RemoteBrokerClient</c>): every request carries the
/// <c>X-QRZ-Callsign</c> + <c>X-QRZ-Session</c> headers the broker verifies before
/// trusting the callsign.
///
/// The QRZ session key is a short-lived secret. The host passes it to the sidecar
/// out of band via the <see cref="QrzSessionEnvVar"/> environment variable (NOT a
/// command-line argument, which would be visible in the process table), alongside
/// the operator callsign. A blank session key disables remote calls (all methods
/// return false) rather than sending an unauthenticated request.
/// </summary>
public sealed class HttpSupportBrokerClient : IMutableSupportBrokerClient
{
    /// <summary>Env var the host uses to hand the QRZ session key to the sidecar (kept off the command line).</summary>
    public const string QrzSessionEnvVar = SupportIpc.SidecarQrzSessionEnvVar;

    private readonly HttpClient _http;
    private readonly BrokerEndpoints _endpoints;

    // Static process identity for the presence body: the host's platform and the
    // app version. Set once at construction (they never change for a process).
    private readonly string _platform;
    private readonly string _appVersion;

    // Identity is mutable: the host hands the sidecar a launch-time seed (often
    // blank, because QRZ silent-login races process launch) and then refreshes it
    // over the IPC pipe once QRZ identity is available. Each is a lone reference
    // write, published with volatile semantics; a request that briefly observes a
    // new callsign with the previous key just earns one retried 401, never a crash.
    private volatile string _callsign;
    private volatile string _sessionKey;

    // Radio metadata for the presence body, refreshed over IPC as the operator
    // connects/disconnects a radio. Volatile reference/flag writes — a presence
    // POST that observes a transient mix just publishes slightly stale metadata.
    private volatile string? _radioBoard;
    private volatile string? _radioModel;
    private volatile bool _radioConnected;

    public HttpSupportBrokerClient(
        HttpClient http, BrokerEndpoints endpoints, string callsign, string sessionKey,
        string platform, string appVersion)
    {
        _http = http;
        _endpoints = endpoints;
        _callsign = callsign ?? "";
        _sessionKey = sessionKey ?? "";
        _platform = platform ?? "";
        _appVersion = appVersion ?? "";
    }

    /// <summary>
    /// Swap the operator identity used to authenticate broker calls. Called as
    /// QRZ identity arrives/changes over IPC. Blank callsign or key leaves the
    /// client <see cref="IsConfigured"/>=false (a no-op rather than an
    /// unauthenticated request).
    /// </summary>
    public void UpdateIdentity(string? callsign, string? sessionKey)
    {
        _callsign = callsign ?? "";
        _sessionKey = sessionKey ?? "";
    }

    /// <summary>True only when we have the identity needed to make an authenticated call.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_callsign) && !string.IsNullOrWhiteSpace(_sessionKey);

    /// <summary>
    /// Swap the operator's radio metadata advertised in the presence body. Called
    /// over IPC as the operator connects/disconnects a radio. A no-op on auth — only
    /// affects what register/heartbeat bodies report.
    /// </summary>
    public void UpdateMetadata(string? radioBoard, string? radioModel, bool radioConnected)
    {
        _radioBoard = radioBoard;
        _radioModel = radioModel;
        _radioConnected = radioConnected;
    }

    // Presence register/heartbeat carry a metadata body so the broker can show the
    // operator's platform / app version / radio. Drop stays body-less (it only needs
    // identity headers to clear presence).
    public Task<bool> RegisterAsync(CancellationToken ct) => PostAsync(_endpoints.PresenceRegister, BuildPresenceBody(), ct);
    public Task<bool> HeartbeatAsync(CancellationToken ct) => PostAsync(_endpoints.PresenceHeartbeat, BuildPresenceBody(), ct);
    public Task<bool> DropAsync(CancellationToken ct) => PostAsync(_endpoints.PresenceDrop, null, ct);

    public Task<bool> UploadCrashAsync(string crashRecordJson, CancellationToken ct) =>
        PostAsync(_endpoints.Crash, crashRecordJson, ct);

    private async Task<bool> PostAsync(Uri uri, string? jsonBody, CancellationToken ct)
    {
        if (!IsConfigured) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, uri);
            req.Headers.TryAddWithoutValidation("X-QRZ-Callsign", _callsign);
            req.Headers.TryAddWithoutValidation("X-QRZ-Session", _sessionKey);
            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            // Best-effort: a broker outage or transient network error must never
            // throw out of the sidecar's presence/crash path.
            return false;
        }
    }

    // Build the presence body the broker reads off register/heartbeat. Written with
    // a Utf8JsonWriter (no reflection, trim-safe) so every string is correctly
    // escaped and nulls are emitted as JSON null. Snapshot the volatile fields once
    // so the body is internally consistent.
    private string BuildPresenceBody()
    {
        var board = _radioBoard;
        var model = _radioModel;
        var connected = _radioConnected;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("platform", _platform);
            writer.WriteString("appVersion", _appVersion);
            if (board is null) writer.WriteNull("radioBoard"); else writer.WriteString("radioBoard", board);
            if (model is null) writer.WriteNull("radioModel"); else writer.WriteString("radioModel", model);
            writer.WriteBoolean("radioConnected", connected);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
