// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// ActivationSpotsService — aggregates live POTA + SOTA + DX-cluster spots and
// caches them for the Spots panel (GET /api/spots/activations). Inspired by
// POTACAT (github.com/Waffleslop/POTACAT, GPL-2.0): same public feeds, but
// re-implemented in Zeus's stack so click-to-tune drives the Zeus VFO instead
// of Hamlib rigctld.
//
// This is a self-contained background poller. It touches NOTHING on the radio
// / DSP / TX path — it only fetches public JSON feeds on a timer and holds the
// merged result in memory. If a feed is unreachable the last good cache is
// kept; one source failing never blanks the others. It is unrelated to the TCI
// DX-cluster SpotManager (Zeus.Server.Tci).
//
// Each source URL is operator-configurable in Settings -> Spots; the defaults
// (and the unit each feed reports frequency in) are:
//   POTA: https://api.pota.app/spot/activator       (kHz string)
//   SOTA: https://api2.sota.org.uk/api/spots/N/all   (MHz string)
//   DX:   https://www.dxsummit.fi/api/v1/spots        (kHz string; off by default)

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
        // A disabled feed resolves to an empty list (not null) so it doesn't
        // count as a failure that would keep a stale snapshot.
        var empty = Task.FromResult<IReadOnlyList<ActivationSpotDto>?>(Array.Empty<ActivationSpotDto>());
        var potaTask = cfg.PotaEnabled ? FetchPotaAsync(cfg.PotaUrl, ct) : empty;
        var sotaTask = cfg.SotaEnabled ? FetchSotaAsync(cfg.SotaUrl, ct) : empty;
        var dxTask = cfg.DxEnabled ? FetchDxAsync(cfg.DxUrl, ct) : empty;
        await Task.WhenAll(potaTask, sotaTask, dxTask).ConfigureAwait(false);

        var pota = potaTask.Result;
        var sota = sotaTask.Result;
        var dx = dxTask.Result;
        var anyFailed = pota is null || sota is null || dx is null;

        var merged = new List<ActivationSpotDto>((pota?.Count ?? 0) + (sota?.Count ?? 0) + (dx?.Count ?? 0));
        if (pota is not null) merged.AddRange(pota);
        if (sota is not null) merged.AddRange(sota);
        if (dx is not null) merged.AddRange(dx);

        // Keep the previous snapshot if every fetch we attempted failed — a
        // transient outage shouldn't blank a good list. Otherwise publish
        // (including a genuine empty result).
        if (merged.Count == 0 && anyFailed && _spots.Count > 0)
        {
            _log.LogDebug("activation-spots poll: all enabled feeds failed, keeping last snapshot");
            return;
        }

        // Newest first. SpotTime is ISO-8601 (UTC, no offset) from every feed,
        // so ordinal string compare is chronological.
        merged.Sort((a, b) => string.CompareOrdinal(b.SpotTime, a.SpotTime));
        _spots = merged;

        _log.LogDebug("activation-spots polled pota={Pota} sota={Sota} dx={Dx}",
            pota?.Count ?? -1, sota?.Count ?? -1, dx?.Count ?? -1);
    }

    // Returns null on error so the caller can keep the last good snapshot.
    private async Task<IReadOnlyList<ActivationSpotDto>?> FetchPotaAsync(string url, CancellationToken ct)
    {
        try
        {
            var raw = await Http.GetFromJsonSafeAsync<List<PotaSpot>>(url, JsonOpts, ct).ConfigureAwait(false);
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
    private async Task<IReadOnlyList<ActivationSpotDto>?> FetchSotaAsync(string url, CancellationToken ct)
    {
        try
        {
            var raw = await Http.GetFromJsonSafeAsync<List<SotaSpot>>(url, JsonOpts, ct).ConfigureAwait(false);
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

    // Returns null on error so the caller can keep the last good snapshot.
    // Targets the DXSummit JSON shape (de_call / dx_call / frequency-kHz / time /
    // info), the de-facto public DX-cluster feed; an operator can point DxUrl at
    // any mirror or cluster gateway that serves the same array-of-objects shape.
    private async Task<IReadOnlyList<ActivationSpotDto>?> FetchDxAsync(string url, CancellationToken ct)
    {
        try
        {
            var raw = await Http.GetFromJsonSafeAsync<List<DxSpot>>(url, JsonOpts, ct).ConfigureAwait(false);
            if (raw is null) return Array.Empty<ActivationSpotDto>();

            var list = new List<ActivationSpotDto>(raw.Count);
            foreach (var s in raw)
            {
                var hz = FreqElementKHz(s.Frequency);
                if (hz <= 0 || string.IsNullOrWhiteSpace(s.DxCall)) continue;
                // The "activator" of a DX spot is the station being worked
                // (dx_call); the spotter (de_call) is who reported it.
                list.Add(new ActivationSpotDto(
                    Source: "DX",
                    Activator: s.DxCall!.Trim().ToUpperInvariant(),
                    FreqHz: hz,
                    Mode: (s.Mode ?? "").Trim().ToUpperInvariant(),
                    Reference: "",
                    Name: string.IsNullOrWhiteSpace(s.Info) ? null : s.Info!.Trim(),
                    Location: string.IsNullOrWhiteSpace(s.DxCountry) ? null : s.DxCountry!.Trim(),
                    Grid: null,
                    Comments: string.IsNullOrWhiteSpace(s.Info) ? null : s.Info!.Trim(),
                    Spotter: string.IsNullOrWhiteSpace(s.DeCall) ? null : s.DeCall!.Trim().ToUpperInvariant(),
                    SpotTime: s.Time ?? ""));
            }
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DX spot fetch failed");
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

    // DX frequency (kHz) tolerant of either a JSON string or a JSON number.
    private static long FreqElementKHz(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => ParseKHz(el.GetString()),
        JsonValueKind.Number when el.TryGetDouble(out var khz) => (long)Math.Round(khz * 1_000.0),
        _ => 0,
    };

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

    // DXSummit /api/v1/spots shape (snake_case JSON → explicit JsonPropertyName
    // since these don't match the PropertyNameCaseInsensitive camel/Pascal rule).
    private sealed class DxSpot
    {
        [JsonPropertyName("de_call")] public string? DeCall { get; set; }
        [JsonPropertyName("dx_call")] public string? DxCall { get; set; }
        // DXSummit may serialize frequency as a JSON string ("14074.0") or a
        // number (14074.0); JsonElement accepts either, parsed by FreqElementKHz.
        [JsonPropertyName("frequency")] public JsonElement Frequency { get; set; }
        [JsonPropertyName("info")] public string? Info { get; set; }
        [JsonPropertyName("time")] public string? Time { get; set; }
        [JsonPropertyName("mode")] public string? Mode { get; set; }
        [JsonPropertyName("dx_country")] public string? DxCountry { get; set; }
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
