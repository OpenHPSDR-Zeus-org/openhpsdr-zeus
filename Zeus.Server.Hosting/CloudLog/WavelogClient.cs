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
using System.Text;
using System.Text.Json;

namespace Zeus.Server.CloudLog;

/// <summary>
/// Per-QSO realtime uploader for Wavelog and Cloudlog (Wavelog is the Cloudlog
/// fork — one wire shape covers both). SEND-ONLY: it POSTs a single ADIF record
/// to {baseUrl}/api/qso and never reads the remote log back.
///
/// Wire contract (docs.wavelog.org / magicbug Cloudlog wiki, verified June 2026):
///   POST {baseUrl}/api/qso
///   Content-Type: application/json, Accept: application/json
///   body: {"key":"&lt;api key&gt;","station_profile_id":"&lt;int&gt;",
///          "type":"adif","string":"&lt;one ADIF record&gt;"}
///   On 404 retry ONCE at {baseUrl}/index.php/api/qso (installs without URL
///   rewriting expose the front controller path). The server dupe-checks on the
///   fly; API submissions never re-post to QRZ or do a callbook lookup.
///
/// Stateless and side-effect-free apart from the HTTP call, so it unit-tests
/// cleanly behind a fake <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class WavelogClient
{
    public const string ServiceName = "wavelog-apikey";

    private readonly HttpClient _http;
    private readonly ILogger<WavelogClient> _log;

    public WavelogClient(IHttpClientFactory httpClientFactory, ILogger<WavelogClient> log)
    {
        _http = httpClientFactory.CreateClient("Wavelog");
        _log = log;
    }

    public async Task<CloudLogResult> PublishAsync(
        WavelogConfig cfg, string apiKey, string adifRecord, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl))
            return CloudLogResult.Skipped("wavelog", "base URL not configured");
        if (string.IsNullOrWhiteSpace(apiKey))
            return CloudLogResult.Skipped("wavelog", "API key not configured");
        if (string.IsNullOrWhiteSpace(cfg.StationProfileId))
            return CloudLogResult.Skipped("wavelog", "station profile id not configured");

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["key"] = apiKey,
            ["station_profile_id"] = cfg.StationProfileId.Trim(),
            ["type"] = "adif",
            ["string"] = adifRecord,
        });

        var primary = BuildQsoUrl(cfg.BaseUrl, indexPhp: false);
        try
        {
            var (status, body) = await PostAsync(primary, payload, ct).ConfigureAwait(false);

            // Some installs only expose the front controller — retry once via index.php.
            if (status == HttpStatusCode.NotFound)
            {
                var fallback = BuildQsoUrl(cfg.BaseUrl, indexPhp: true);
                _log.LogDebug("wavelog 404 at {Url}, retrying {Fallback}", primary, fallback);
                (status, body) = await PostAsync(fallback, payload, ct).ConfigureAwait(false);
            }

            return MapResponse(status, body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "wavelog publish failed");
            return CloudLogResult.Fail("wavelog", ex.Message);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string url, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (resp.StatusCode, body);
    }

    private CloudLogResult MapResponse(HttpStatusCode status, string body)
    {
        // Wavelog returns 200 with a JSON object on success (the imported record)
        // and a JSON object carrying error/messages on failure. Be lenient: treat
        // a 2xx without an explicit error as success, and parse an "id" when present.
        if ((int)status is >= 200 and < 300)
        {
            string? id = null;
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("adif_count", out var c)) id = c.ToString();
                    if (root.TryGetProperty("id", out var idEl)) id = idEl.ToString();
                    if (root.TryGetProperty("messages", out var m)) message = m.ToString();
                    if (root.TryGetProperty("reason", out var r)) message = r.ToString();
                    if (root.TryGetProperty("status", out var st) &&
                        st.ValueKind == JsonValueKind.String &&
                        string.Equals(st.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
                    {
                        return CloudLogResult.Fail("wavelog", message ?? "rejected by server");
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON 2xx body — still treat as success (older Cloudlog builds
                // return a bare string).
            }
            return CloudLogResult.Ok("wavelog", id, message ?? "QSO uploaded");
        }

        return CloudLogResult.Fail("wavelog", $"HTTP {(int)status}: {Truncate(body)}");
    }

    // Joins baseUrl + the api path without doubling slashes; optional index.php
    // front-controller segment for installs without URL rewriting.
    internal static string BuildQsoUrl(string baseUrl, bool indexPhp)
    {
        var b = baseUrl.Trim().TrimEnd('/');
        return indexPhp ? $"{b}/index.php/api/qso" : $"{b}/api/qso";
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 200 ? s : s[..200]);
}
