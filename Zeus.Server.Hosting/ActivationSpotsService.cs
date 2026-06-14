// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// ActivationSpotsService — aggregates live POTA + SOTA activation spots and
// caches them for the Spots panel (GET /api/spots/activations). Inspired by
// POTACAT (github.com/Waffleslop/POTACAT, GPL-2.0): same public feeds, but
// re-implemented in Zeus's stack so click-to-tune drives the Zeus VFO instead
// of Hamlib rigctld.
//
// This is a self-contained background poller. It touches NOTHING on the radio
// / DSP / TX path — it only fetches two public JSON feeds on a timer and holds
// the merged result in memory. If a feed is unreachable the last good cache is
// kept; one source failing never blanks the other. It is unrelated to the TCI
// DX-cluster SpotManager (Zeus.Server.Tci).
//
// Feeds:
//   POTA: https://api.pota.app/spot/activator   (frequency in kHz, string)
//   SOTA: https://api2.sota.org.uk/api/spots/N/all (frequency in MHz, string)

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Polls the POTA and SOTA activation feeds on a timer and exposes the merged,
/// normalized snapshot via <see cref="GetCurrentSpots"/>. Registered as a
/// singleton + hosted service in <c>ZeusHost</c>.
/// </summary>
public sealed class ActivationSpotsService : BackgroundService
{
    private const string PotaUrl = "https://api.pota.app/spot/activator";
    private const string SotaUrl = "https://api2.sota.org.uk/api/spots/50/all";

    private static readonly HttpClient Http = CreateClient();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<ActivationSpotsService> _log;
    private readonly SpotsSettingsStore _settings;

    // Signalled by Wake() so a settings change refreshes immediately instead of
    // waiting out the current poll interval. Initial count 0 (nothing pending),
    // max 1 (coalesce repeated wakes into one).
    private readonly SemaphoreSlim _wake = new(0, 1);

    // Last good merged snapshot. Replaced wholesale on each successful poll;
    // reads are lock-free against an immutable list reference.
    private volatile IReadOnlyList<ActivationSpotDto> _spots = Array.Empty<ActivationSpotDto>();

    public ActivationSpotsService(ILogger<ActivationSpotsService> log, SpotsSettingsStore settings)
    {
        _log = log;
        _settings = settings;
    }

    /// <summary>Nudge the poller to re-fetch now (called after a settings change),
    /// without disturbing the steady-state interval.</summary>
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a wake is already pending */ }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // POTA/SOTA APIs reject requests without a User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Zeus-HPSDR/1.0 (+https://github.com/Kb2uka/openhpsdr-zeus)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>The latest merged POTA+SOTA snapshot, newest spots first.</summary>
    public IReadOnlyList<ActivationSpotDto> GetCurrentSpots() => _spots;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Immediate first poll, then on the configured interval. A settings
        // change calls Wake() to cut the wait short; an outage keeps the
        // existing cache and retries on the next tick.
        while (!ct.IsCancellationRequested)
        {
            var cfg = _settings.Get();
            try
            {
                if (cfg.Enabled)
                {
                    await PollAsync(cfg, ct).ConfigureAwait(false);
                }
                else
                {
                    // Master switch off — the panel should show nothing.
                    _spots = Array.Empty<ActivationSpotDto>();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "activation-spots poll failed");
            }

            var delay = TimeSpan.FromSeconds(cfg.PollIntervalSeconds);
            try
            {
                // Returns early (true) when Wake() signals a settings change.
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollAsync(SpotsSettings cfg, CancellationToken ct)
    {
        // Fetch the enabled feeds concurrently. A null result means that fetch
        // errored (vs an empty list, which means the feed is genuinely quiet).
        var potaTask = cfg.PotaEnabled ? FetchPotaAsync(ct) : Task.FromResult<IReadOnlyList<ActivationSpotDto>?>(Array.Empty<ActivationSpotDto>());
        var sotaTask = cfg.SotaEnabled ? FetchSotaAsync(ct) : Task.FromResult<IReadOnlyList<ActivationSpotDto>?>(Array.Empty<ActivationSpotDto>());
        await Task.WhenAll(potaTask, sotaTask).ConfigureAwait(false);

        var pota = potaTask.Result;
        var sota = sotaTask.Result;
        var anyFailed = pota is null || sota is null;

        var merged = new List<ActivationSpotDto>((pota?.Count ?? 0) + (sota?.Count ?? 0));
        if (pota is not null) merged.AddRange(pota);
        if (sota is not null) merged.AddRange(sota);

        // Keep the previous snapshot if every fetch we attempted failed — a
        // transient outage shouldn't blank a good list. Otherwise publish
        // (including a genuine empty result).
        if (merged.Count == 0 && anyFailed && _spots.Count > 0)
        {
            _log.LogDebug("activation-spots poll: all enabled feeds failed, keeping last snapshot");
            return;
        }

        // Newest first. SpotTime is ISO-8601 (UTC, no offset) from both feeds,
        // so ordinal string compare is chronological.
        merged.Sort((a, b) => string.CompareOrdinal(b.SpotTime, a.SpotTime));
        _spots = merged;

        _log.LogDebug("activation-spots polled pota={Pota} sota={Sota}", pota?.Count ?? -1, sota?.Count ?? -1);
    }

    // Returns null on error so the caller can keep the last good snapshot.
    private async Task<IReadOnlyList<ActivationSpotDto>?> FetchPotaAsync(CancellationToken ct)
    {
        try
        {
            var raw = await Http.GetFromJsonSafeAsync<List<PotaSpot>>(PotaUrl, JsonOpts, ct).ConfigureAwait(false);
            if (raw is null) return Array.Empty<ActivationSpotDto>();

            var list = new List<ActivationSpotDto>(raw.Count);
            foreach (var s in raw)
            {
                if (s.Invalid is not null) continue;
                var hz = ParseKHz(s.Frequency);
                if (hz <= 0 || string.IsNullOrWhiteSpace(s.Activator)) continue;
                list.Add(new ActivationSpotDto(
                    Source: "POTA",
                    Activator: s.Activator!.Trim().ToUpperInvariant(),
                    FreqHz: hz,
                    Mode: (s.Mode ?? "").Trim().ToUpperInvariant(),
                    Reference: s.Reference ?? "",
                    Name: s.Name,
                    Location: s.LocationDesc,
                    Grid: s.Grid6 ?? s.Grid4,
                    Comments: string.IsNullOrWhiteSpace(s.Comments) ? null : s.Comments,
                    Spotter: s.Spotter,
                    SpotTime: s.SpotTime ?? ""));
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "POTA spot fetch failed");
            return null;
        }
    }

    // Returns null on error so the caller can keep the last good snapshot.
    private async Task<IReadOnlyList<ActivationSpotDto>?> FetchSotaAsync(CancellationToken ct)
    {
        try
        {
            var raw = await Http.GetFromJsonSafeAsync<List<SotaSpot>>(SotaUrl, JsonOpts, ct).ConfigureAwait(false);
            if (raw is null) return Array.Empty<ActivationSpotDto>();

            var list = new List<ActivationSpotDto>(raw.Count);
            foreach (var s in raw)
            {
                var hz = ParseMHz(s.Frequency);
                var call = s.ActivatorCallsign ?? s.Callsign;
                if (hz <= 0 || string.IsNullOrWhiteSpace(call)) continue;
                var reference = !string.IsNullOrWhiteSpace(s.AssociationCode) && !string.IsNullOrWhiteSpace(s.SummitCode)
                    ? $"{s.AssociationCode}/{s.SummitCode}"
                    : (s.SummitCode ?? "");
                list.Add(new ActivationSpotDto(
                    Source: "SOTA",
                    Activator: call!.Trim().ToUpperInvariant(),
                    FreqHz: hz,
                    Mode: (s.Mode ?? "").Trim().ToUpperInvariant(),
                    Reference: reference,
                    Name: s.SummitDetails,
                    Location: s.AssociationCode,
                    Grid: null,
                    Comments: string.IsNullOrWhiteSpace(s.Comments) ? null : s.Comments,
                    Spotter: s.Callsign,
                    SpotTime: s.TimeStamp ?? ""));
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SOTA spot fetch failed");
            return null;
        }
    }

    // POTA reports frequency as a kHz string, e.g. "14076" or "14076.0".
    private static long ParseKHz(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var khz)
            ? (long)Math.Round(khz * 1_000.0)
            : 0;

    // SOTA reports frequency as a MHz string, e.g. "14.062".
    private static long ParseMHz(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
            ? (long)Math.Round(mhz * 1_000_000.0)
            : 0;

    // ----- upstream feed shapes (only the fields we consume) -----

    private sealed class PotaSpot
    {
        public string? Activator { get; set; }
        public string? Frequency { get; set; }
        public string? Mode { get; set; }
        public string? Reference { get; set; }
        public string? Name { get; set; }
        public string? Comments { get; set; }
        public string? Spotter { get; set; }
        public string? SpotTime { get; set; }
        public string? LocationDesc { get; set; }
        public string? Grid4 { get; set; }
        public string? Grid6 { get; set; }
        [JsonPropertyName("invalid")] public object? Invalid { get; set; }
    }

    private sealed class SotaSpot
    {
        public string? Callsign { get; set; }
        public string? ActivatorCallsign { get; set; }
        public string? AssociationCode { get; set; }
        public string? SummitCode { get; set; }
        public string? SummitDetails { get; set; }
        public string? Frequency { get; set; }
        public string? Mode { get; set; }
        public string? Comments { get; set; }
        public string? TimeStamp { get; set; }
    }
}

/// <summary>Small helper so a malformed/empty body doesn't throw — returns null
/// instead, letting each feed degrade independently.</summary>
internal static class HttpJsonExtensions
{
    public static async Task<T?> GetFromJsonSafeAsync<T>(
        this HttpClient http, string url, JsonSerializerOptions opts, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, opts, ct).ConfigureAwait(false);
    }
}
