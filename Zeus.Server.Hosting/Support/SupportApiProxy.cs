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
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Support;

/// <summary>One tunnelled read request from a support session ({id, method, path}).</summary>
public readonly record struct SupportApiRequest(int Id, string Method, string Path);

/// <summary>The radio's reply for a <see cref="SupportApiRequest"/>.</summary>
public readonly record struct SupportApiReply(int Id, int Status, string? ContentType, string? Body);

/// <summary>
/// The read-only loopback proxy at the heart of a support session's "api" data
/// channel — factored out of the WebRTC plumbing so the safety-critical gating is
/// unit-testable without a live PeerConnection (mirrors how the operator tunnel's
/// guards are validated).
///
/// Every request runs the gauntlet, fail-closed at each step:
///   1. method not GET/HEAD          → 405 (no mutation EVER reaches loopback)
///   2. malformed / non-absolute path → 502
///   3. path traversal ("../", %2e)  → 403 (can't collapse onto a non-allowlisted path)
///   4. target not loopback           → 502 (SSRF guard — stay on 127.0.0.1)
///   5. path off the read allowlist   → 403 (<see cref="SupportSessionPolicy"/>)
///   6. otherwise                     → proxy to the radio's own Kestrel; 502 on
///                                       oversized reply or transport error.
/// Never throws out of <see cref="HandleAsync"/>; a failure becomes a 502 reply.
/// </summary>
public sealed class SupportApiProxy
{
    /// <summary>Named loopback HttpClient (shares the operator tunnel's registration).</summary>
    public const string LoopbackHttpClientName =
        Zeus.Server.Hosting.Remote.RemoteWebRtcSession.LoopbackHttpClientName;

    // Diagnostic dumps (a full diagnostics-v2 snapshot) can be larger than the
    // operator tunnel's 1 MiB chrome cap, but must still be bounded so the read
    // channel can't be turned into an unbounded exfiltration pipe.
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly string _loopbackBaseUrl;
    private readonly ILogger _log;

    public SupportApiProxy(IHttpClientFactory httpFactory, string loopbackBaseUrl, ILogger log)
    {
        _httpFactory = httpFactory;
        _loopbackBaseUrl = (loopbackBaseUrl ?? "").TrimEnd('/');
        _log = log;
    }

    public async Task<SupportApiReply> HandleAsync(SupportApiRequest req, CancellationToken ct = default)
    {
        int id = req.Id;
        try
        {
            var method = req.Method ?? "GET";
            var path = req.Path ?? "";

            // 1. Read-only methods only — a support session can never mutate the radio.
            if (!SupportSessionPolicy.IsReadOnlyMethod(method))
                return new SupportApiReply(id, 405, null, null);

            if (string.IsNullOrEmpty(_loopbackBaseUrl) || !path.StartsWith('/'))
                return new SupportApiReply(id, 502, null, null);

            // 3. Reject path traversal before canonicalisation can collapse "../"
            //    onto a non-allowlisted endpoint.
            if (path.Contains("..", StringComparison.Ordinal)
                || path.Contains("%2e", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning("support.api DENY (traversal) {Path}", path);
                return new SupportApiReply(id, 403, null, null);
            }

            // 4. Build the loopback target and confirm it stays on 127.0.0.1 (SSRF guard).
            if (!Uri.TryCreate(_loopbackBaseUrl + path, UriKind.Absolute, out var target)
                || !target.IsLoopback)
            {
                return new SupportApiReply(id, 502, null, null);
            }

            // 5. Allowlist the CANONICAL path that will actually be requested.
            if (!SupportSessionPolicy.IsAllowedPath(target.AbsolutePath))
            {
                _log.LogWarning("support.api DENY (not allowlisted) {Method} {Path}", method, target.AbsolutePath);
                return new SupportApiReply(id, 403, null, null);
            }

            // HEAD proxies as GET with the body discarded.
            var isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
            var client = _httpFactory.CreateClient(LoopbackHttpClientName);
            using var msg = new HttpRequestMessage(HttpMethod.Get, target);
            using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (resp.Content.Headers.ContentLength is long len && len > MaxResponseBytes)
                return new SupportApiReply(id, 502, null, null);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > MaxResponseBytes)
                return new SupportApiReply(id, 502, null, null);

            var respContentType = resp.Content.Headers.ContentType?.ToString();
            var body = isHead ? "" : Encoding.UTF8.GetString(bytes);
            return new SupportApiReply(id, (int)resp.StatusCode, respContentType, body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "support.api request failed — replying 502");
            return new SupportApiReply(id, 502, null, null);
        }
    }
}
