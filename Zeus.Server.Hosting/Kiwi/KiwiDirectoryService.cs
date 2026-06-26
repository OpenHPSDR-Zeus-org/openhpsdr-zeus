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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>One public KiwiSDR from the kiwisdr.com directory, reshaped for the
/// map picker. <paramref name="Url"/> is the address Zeus connects to (the same
/// form <see cref="KiwiSdrService.TryParseEndpoint"/> accepts).</summary>
public sealed record KiwiDirectoryEntry(
    string Name,
    string Url,
    double Lat,
    double Lon,
    int Users,
    int UsersMax,
    bool Online,
    string? Location,
    string? Snr);

/// <summary>
/// Proxies the public KiwiSDR directory (the kiwisdr.com receiver list, mirrored
/// as <c>rx.linkfanel.net/kiwisdr_com.js</c> for the "dyatlov" map maker) so the
/// frontend can render a world map of selectable receivers. Server-side because
/// the source is plain HTTP — a desktop SPA served over LAN HTTPS would have the
/// browser block it as mixed content, and CORS would block it regardless. We
/// fetch, strip the <c>var kiwisdr_com = [...]</c> JS wrapper, parse, reshape to
/// <see cref="KiwiDirectoryEntry"/>, and cache for a few minutes (the upstream
/// regenerates ~once/minute; a short cache collapses repeated panel opens).
///
/// <para>Never throws to the caller: on any upstream failure it returns the last
/// good cache if present, else an empty list.</para>
/// </summary>
public sealed class KiwiDirectoryService
{
    // The kiwisdr.com list, mirrored as a JS array for the dyatlov map maker.
    private const string DirectoryUrl = "http://rx.linkfanel.net/kiwisdr_com.js";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<KiwiDirectoryService> _log;
    private readonly object _gate = new();
    private (DateTimeOffset At, IReadOnlyList<KiwiDirectoryEntry> List)? _cache;

    public KiwiDirectoryService(IHttpClientFactory httpFactory, ILogger<KiwiDirectoryService> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task<IReadOnlyList<KiwiDirectoryEntry>> GetAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cache is { } c && DateTimeOffset.UtcNow - c.At < CacheTtl)
                return c.List;
        }

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Get, DirectoryUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "OpenHPSDR-Zeus");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("kiwi.directory upstream HTTP {Status}", (int)resp.StatusCode);
                return CachedOrEmpty();
            }

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var list = Parse(body);
            if (list.Count == 0) return CachedOrEmpty();
            lock (_gate) { _cache = (DateTimeOffset.UtcNow, list); }
            _log.LogInformation("kiwi.directory fetched {Count} receivers", list.Count);
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "kiwi.directory fetch failed");
            return CachedOrEmpty();
        }
    }

    private IReadOnlyList<KiwiDirectoryEntry> CachedOrEmpty()
    {
        lock (_gate) { return _cache?.List ?? Array.Empty<KiwiDirectoryEntry>(); }
    }

    // The file is JS: a banner of `//` comments then `var kiwisdr_com = [ {...}, ... ]`.
    // Slice out the JSON array (first '[' .. last ']') and parse. All field values
    // are JSON strings (e.g. "users":"4"), so numbers are parsed leniently.
    internal static List<KiwiDirectoryEntry> Parse(string body)
    {
        var result = new List<KiwiDirectoryEntry>();
        if (string.IsNullOrEmpty(body)) return result;
        int lb = body.IndexOf('[');
        int rb = body.LastIndexOf(']');
        if (lb < 0 || rb <= lb) return result;

        JsonDocument doc;
        try
        {
            // The generator emits a TRAILING COMMA before the closing ']' (and
            // banner // comments precede the array) — both rejected by
            // System.Text.Json's strict defaults, so opt into them.
            doc = JsonDocument.Parse(
                body.AsMemory(lb, rb - lb + 1),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
        }
        catch (JsonException) { return result; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var url = Str(el, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (!TryParseGps(Str(el, "gps"), out var lat, out var lon)) continue;

                result.Add(new KiwiDirectoryEntry(
                    Name: Str(el, "name") ?? url!,
                    Url: url!,
                    Lat: lat,
                    Lon: lon,
                    Users: Int(el, "users"),
                    UsersMax: Int(el, "users_max"),
                    // The directory marks dead receivers with offline="yes" /
                    // status!="active"; treat anything else as reachable.
                    Online: !string.Equals(Str(el, "offline"), "yes", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(Str(el, "status"), "offline", StringComparison.OrdinalIgnoreCase),
                    Location: Str(el, "loc"),
                    Snr: Str(el, "snr")));
            }
        }
        return result;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int Int(JsonElement el, string name) =>
        int.TryParse(Str(el, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // GPS is "(lat, lon)" with invariant decimals, e.g. "(50.850000, -0.660000)".
    internal static bool TryParseGps(string? gps, out double lat, out double lon)
    {
        lat = 0; lon = 0;
        if (string.IsNullOrWhiteSpace(gps)) return false;
        var s = gps.Trim().TrimStart('(').TrimEnd(')');
        int comma = s.IndexOf(',');
        if (comma <= 0) return false;
        if (!double.TryParse(s.AsSpan(0, comma).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat))
            return false;
        if (!double.TryParse(s.AsSpan(comma + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
            return false;
        // Reject the (0,0) placeholder many un-GPS'd receivers report — it would
        // pile every such Kiwi onto Null Island.
        if (lat == 0 && lon == 0) return false;
        return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
    }
}
