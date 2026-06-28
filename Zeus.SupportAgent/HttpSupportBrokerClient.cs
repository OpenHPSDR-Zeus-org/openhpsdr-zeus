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
public sealed class HttpSupportBrokerClient : ISupportBrokerClient
{
    /// <summary>Env var the host uses to hand the QRZ session key to the sidecar (kept off the command line).</summary>
    public const string QrzSessionEnvVar = SupportIpc.SidecarQrzSessionEnvVar;

    private readonly HttpClient _http;
    private readonly BrokerEndpoints _endpoints;
    private readonly string _callsign;
    private readonly string _sessionKey;

    public HttpSupportBrokerClient(HttpClient http, BrokerEndpoints endpoints, string callsign, string sessionKey)
    {
        _http = http;
        _endpoints = endpoints;
        _callsign = callsign;
        _sessionKey = sessionKey;
    }

    /// <summary>True only when we have the identity needed to make an authenticated call.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_callsign) && !string.IsNullOrWhiteSpace(_sessionKey);

    public Task<bool> RegisterAsync(CancellationToken ct) => PostAsync(_endpoints.PresenceRegister, null, ct);
    public Task<bool> HeartbeatAsync(CancellationToken ct) => PostAsync(_endpoints.PresenceHeartbeat, null, ct);
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
}
