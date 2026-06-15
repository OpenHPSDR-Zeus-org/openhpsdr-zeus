// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Comprehensive solar / space-weather snapshot for the dashboard panel.
///
/// Like <see cref="PropagationService"/>, this does not gather any data itself
/// — it proxies the embedded HamClock sidecar's <c>/api/n0nbh</c> endpoint
/// (N0NBH / hamqsl.com solar feed: SFI, A/K index, sunspots, X-ray, particle
/// flux, solar wind, MUF, plus per-band HF + VHF conditions) server-side to
/// avoid browser CORS, then caches and reshapes it into
/// <see cref="SpaceWeatherSnapshot"/>. Returns an <c>Available = false</c>
/// snapshot (never throws) when the sidecar is unreachable.
/// </summary>
public sealed class SpaceWeatherService
{
    private readonly HttpClient _http;
    private readonly HamClockService _hamClock;
    private readonly ILogger<SpaceWeatherService> _log;

    // N0NBH refreshes roughly every 3 hours and the sidecar caches it for an
    // hour; a short Zeus-side cache collapses panel polls/refreshes.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private (DateTimeOffset At, SpaceWeatherSnapshot Snap)? _cache;
    private readonly object _gate = new();

    public SpaceWeatherService(IHttpClientFactory httpClientFactory, HamClockService hamClock, ILogger<SpaceWeatherService> log)
    {
        _http = httpClientFactory.CreateClient("Propagation");
        _hamClock = hamClock;
        _log = log;
    }

    public async Task<SpaceWeatherSnapshot> GetAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cache is { } c && DateTimeOffset.UtcNow - c.At < CacheTtl)
                return c.Snap;
        }

        // Prefer the port Zeus spawned the sidecar on; otherwise probe the
        // configured port so this works when HamClock was started externally.
        var managed = _hamClock.RunningPort;
        var port = managed ?? _hamClock.ConfiguredPort;
        var url = $"http://127.0.0.1:{port}/api/n0nbh";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("Space-weather upstream returned {Status}", (int)resp.StatusCode);
                return Unavailable($"Space-weather feed returned HTTP {(int)resp.StatusCode}.");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var snap = Map(doc.RootElement);
            lock (_gate) { _cache = (DateTimeOffset.UtcNow, snap); }
            return snap;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Space-weather fetch failed");
            return Unavailable(managed is null
                ? "HamClock is not running — start it from Settings → HamClock to enable space-weather data."
                : "Could not reach the space-weather feed.");
        }
    }

    private static SpaceWeatherSnapshot Unavailable(string reason) => new(
        Available: false,
        Unavailable: reason,
        Source: null, Updated: null, FetchedAt: null,
        SolarFlux: null, Sunspots: null, AIndex: null, KIndex: null, KIndexNt: null,
        Xray: null, HeliumLine: null, ProtonFlux: null, ElectronFlux: null, Aurora: null,
        Normalization: null, LatDegree: null, SolarWind: null, MagneticField: null,
        Fof2: null, MufFactor: null, Muf: null,
        GeomagField: null, SignalNoise: null,
        BandConditions: Array.Empty<SpaceWeatherBand>(),
        VhfConditions: Array.Empty<SpaceWeatherVhf>());

    private static SpaceWeatherSnapshot Map(JsonElement root)
    {
        var solar = root.TryGetProperty("solarData", out var sd) ? sd : default;

        var bands = new List<SpaceWeatherBand>();
        if (root.TryGetProperty("bandConditions", out var bc) && bc.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in bc.EnumerateArray())
            {
                bands.Add(new SpaceWeatherBand(
                    Name: Str(b, "name") ?? "",
                    Time: Str(b, "time") ?? "",
                    Condition: Str(b, "condition") ?? ""));
            }
        }

        var vhf = new List<SpaceWeatherVhf>();
        if (root.TryGetProperty("vhfConditions", out var vc) && vc.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vc.EnumerateArray())
            {
                vhf.Add(new SpaceWeatherVhf(
                    Name: Str(v, "name") ?? "",
                    Location: Str(v, "location") ?? "",
                    Condition: Str(v, "condition") ?? ""));
            }
        }

        return new SpaceWeatherSnapshot(
            Available: true,
            Unavailable: null,
            Source: Str(root, "source"),
            Updated: Str(root, "updated"),
            FetchedAt: Long(root, "fetchedAt"),
            SolarFlux: Str(solar, "solarFlux"),
            Sunspots: Str(solar, "sunspots"),
            AIndex: Str(solar, "aIndex"),
            KIndex: Str(solar, "kIndex"),
            KIndexNt: Str(solar, "kIndexNt"),
            Xray: Str(solar, "xray"),
            HeliumLine: Str(solar, "heliumLine"),
            ProtonFlux: Str(solar, "protonFlux"),
            ElectronFlux: Str(solar, "electronFlux"),
            Aurora: Str(solar, "aurora"),
            Normalization: Str(solar, "normalization"),
            LatDegree: Str(solar, "latDegree"),
            SolarWind: Str(solar, "solarWind"),
            MagneticField: Str(solar, "magneticField"),
            Fof2: Str(solar, "fof2"),
            MufFactor: Str(solar, "mufFactor"),
            Muf: Str(solar, "muf"),
            GeomagField: Str(root, "geomagField"),
            SignalNoise: Str(root, "signalNoise"),
            BandConditions: bands,
            VhfConditions: vhf);
    }

    private static string? Str(JsonElement parent, string name)
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

    private static long? Long(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object) return null;
        if (!parent.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : null;
    }
}
