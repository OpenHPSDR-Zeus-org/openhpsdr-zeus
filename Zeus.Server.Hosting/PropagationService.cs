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

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Point-to-point HF propagation predictions (DE → DX) for the QRZ card.
///
/// Zeus does NOT implement propagation physics itself. The embedded HamClock
/// sidecar (OpenHamClock) already ships a full ITU-R P.533-14 engine plus live
/// solar data (NOAA / N0NBH) and exposes it over REST at
/// <c>GET /api/propagation</c> on its own port. This service proxies that
/// endpoint server-side — avoiding browser CORS, keeping the call same-origin
/// for the web client, and resolving DE/DX/band from Zeus state — then caches
/// and reshapes the result into <see cref="PropagationResult"/>.
///
/// When the sidecar is not installed/running, <see cref="PredictAsync"/>
/// returns an <c>Available = false</c> result with a reason rather than
/// throwing, so the UI can degrade gracefully.
/// </summary>
public sealed class PropagationService
{
    private readonly HttpClient _http;
    private readonly HamClockService _hamClock;
    private readonly ILogger<PropagationService> _log;

    // Predictions change slowly (SSN is daily, the sidecar caches internally for
    // ~10-30 min). A short Zeus-side cache collapses repeated card refreshes for
    // the same path/band into one upstream call.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PropagationResult Result)> _cache = new();

    public PropagationService(IHttpClientFactory httpClientFactory, HamClockService hamClock, ILogger<PropagationService> log)
    {
        _http = httpClientFactory.CreateClient("Propagation");
        _hamClock = hamClock;
        _log = log;
    }

    public async Task<PropagationResult> PredictAsync(
        double deLat, double deLon,
        double dxLat, double dxLon,
        string? mode,
        double? powerW,
        string? antenna,
        double? currentFreqMhz,
        CancellationToken ct = default)
    {
        // Prefer the port Zeus actually spawned the sidecar on; otherwise probe
        // the configured port so propagation still works when HamClock was
        // started outside this process. A failed localhost call below just
        // surfaces as "unavailable".
        var managed = _hamClock.RunningPort;
        var port = managed ?? _hamClock.ConfiguredPort;

        var inv = CultureInfo.InvariantCulture;
        var pwr = powerW is > 0 ? (int)Math.Round(powerW.Value) : 100;
        var md = string.IsNullOrWhiteSpace(mode) ? "SSB" : mode!.ToUpperInvariant();
        var ant = string.IsNullOrWhiteSpace(antenna) ? "isotropic" : antenna!;

        var cacheKey = string.Create(inv,
            $"{deLat:F1},{deLon:F1}->{dxLat:F1},{dxLon:F1}|{md}|{pwr}|{ant}|{currentFreqMhz:F3}");
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheTtl)
            return hit.Result;

        var url = string.Create(inv,
            $"http://127.0.0.1:{port}/api/propagation?deLat={deLat}&deLon={deLon}&dxLat={dxLat}&dxLon={dxLon}&mode={md}&power={pwr}&antenna={Uri.EscapeDataString(ant)}");

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Propagation upstream returned {Status}", (int)resp.StatusCode);
                return Unavailable($"Propagation engine returned HTTP {(int)resp.StatusCode}.");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var result = Map(doc.RootElement, currentFreqMhz);
            _cache[cacheKey] = (DateTimeOffset.UtcNow, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Propagation fetch failed");
            return Unavailable(managed is null
                ? "HamClock is not running — start it from Settings → HamClock to enable propagation."
                : "Could not reach the propagation engine.");
        }
    }

    private static PropagationResult Unavailable(string reason) => new(
        Available: false,
        Unavailable: reason,
        Model: "",
        Sfi: 0, Ssn: 0, KIndex: 0,
        Muf: 0, Luf: 0,
        DistanceKm: 0,
        CurrentHourUtc: 0,
        Bands: Array.Empty<PropagationBand>(),
        CurrentBand: null);

    private static PropagationResult Map(JsonElement root, double? currentFreqMhz)
    {
        var solar = root.TryGetProperty("solarData", out var sd) ? sd : default;
        double sfi = GetNum(solar, "sfi");
        double ssn = GetNum(solar, "ssn");
        double k = GetNum(solar, "kIndex");

        var bands = new List<PropagationBand>();
        if (root.TryGetProperty("currentBands", out var cb) && cb.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in cb.EnumerateArray())
            {
                var band = GetStr(b, "band");
                if (string.IsNullOrWhiteSpace(band)) continue;
                bands.Add(new PropagationBand(
                    Band: band!,
                    FreqMhz: GetNum(b, "freq"),
                    Reliability: (int)Math.Round(GetNum(b, "reliability")),
                    Snr: GetStr(b, "snr") ?? "",
                    Status: GetStr(b, "status") ?? "CLOSED"));
            }
        }

        // Pick the prediction matching the radio's active band: closest model
        // centre frequency within a 2 MHz tolerance.
        PropagationBand? current = null;
        if (currentFreqMhz is > 0 && bands.Count > 0)
        {
            PropagationBand? best = null;
            double bestDist = double.PositiveInfinity;
            foreach (var b in bands)
            {
                var d = Math.Abs(b.FreqMhz - currentFreqMhz.Value);
                if (d < bestDist) { bestDist = d; best = b; }
            }
            if (bestDist <= 2.0) current = best;
        }

        return new PropagationResult(
            Available: true,
            Unavailable: null,
            Model: GetStr(root, "model") ?? "Built-in estimation",
            Sfi: sfi,
            Ssn: ssn,
            KIndex: k,
            Muf: GetNum(root, "muf"),
            Luf: GetNum(root, "luf"),
            DistanceKm: (int)Math.Round(GetNum(root, "distance")),
            CurrentHourUtc: (int)Math.Round(GetNum(root, "currentHour")),
            Bands: bands,
            CurrentBand: current);
    }

    private static double GetNum(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return 0;
        if (!parent.TryGetProperty(name, out var el)) return 0;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0,
            _ => 0,
        };
    }

    private static string? GetStr(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        };
    }
}
