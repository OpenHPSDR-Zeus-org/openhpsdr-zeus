// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Zeus.Dsp.Ft8;

namespace Zeus.Server.Spotting;

/// <summary>
/// Subscribes to <see cref="WsprService.SpotsReady"/> and uploads each spot to
/// WSPRnet (the wsprd-compatible <c>http://wsprnet.org/post</c> endpoint) as a
/// fire-and-forget form POST. Same leaf-subscriber seam as the FT8 broadcasters:
/// the handler never calls back into the radio/DSP/TX and swallows all errors, so
/// a reporter or network fault can never disturb decode or TX. DISABLED by
/// default and additionally no-ops when operator identity (callsign + grid) is
/// unresolved.
///
/// WSPR decodes carry the heard station's call/grid/power inside the message
/// text ("CALL GRID PWR"), not as separate fields, so <see cref="WsprnetMessage"/>
/// splits it first. The receiver dial (rqrg) comes from the batch; the spot's
/// absolute tx frequency (tqrg) from the spot.
/// </summary>
public sealed class WsprnetReporter : BackgroundService
{
    /// <summary>WSPRnet wsprd-compatible upload endpoint.</summary>
    public const string PostUrl = "http://wsprnet.org/post";

    // Host portion of PostUrl, for the success heartbeat log only.
    private static readonly string PostHost = new Uri(PostUrl).Host;

    private static readonly string Version = BuildSoftwareString();
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ILogger<WsprnetReporter> _log;
    private readonly WsprService? _wspr;
    private readonly SpottingManagementService _spotting;
    private readonly HttpClient _http;

    public WsprnetReporter(
        ILogger<WsprnetReporter> log,
        WsprService? wspr,
        SpottingManagementService spotting,
        HttpMessageHandler? handlerForTests = null)
    {
        _log = log;
        _wspr = wspr;
        _spotting = spotting;
        _http = handlerForTests is null
            ? SharedHttp
            : new HttpClient(handlerForTests) { Timeout = TimeSpan.FromSeconds(10) };
        if (_wspr is not null) _wspr.SpotsReady += OnSpots;
    }

    // Test seam: no WsprService subscription. Feed batches via HandleSpotsAsync and
    // observe the supplied recording handler.
    internal WsprnetReporter(
        ILogger<WsprnetReporter> log,
        SpottingManagementService spotting,
        HttpMessageHandler handler)
        : this(log, wspr: null, spotting, handler) { }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    public override void Dispose()
    {
        if (_wspr is not null) _wspr.SpotsReady -= OnSpots;
        // Dispose only a per-instance (test) client; never the shared singleton.
        if (!ReferenceEquals(_http, SharedHttp)) _http.Dispose();
        base.Dispose();
    }

    // Worker-thread handler — fires the gated POSTs without blocking the decode
    // path (fire-and-forget). Never throws into the decode path.
    private void OnSpots(WsprSpotBatch batch)
    {
        try { _ = HandleSpotsAsync(batch); }
        catch (Exception ex) { _log.LogDebug(ex, "wsprnet enqueue failed"); }
    }

    // Applies the enable + identity gate, splits/filters each spot, and POSTs the
    // survivors. Returns the number of spots WSPRnet ACCEPTED with an HTTP success
    // status (failed POSTs count as 0). Internal + awaitable so the gate (disabled
    // => 0, no-identity => 0, enabled+identity => N accepted) can be asserted
    // against a recording handler. OnSpots calls this fire-and-forget, so the only
    // synchronous work on the decode thread is the cheap gate + form building.
    internal async Task<int> HandleSpotsAsync(WsprSpotBatch batch)
    {
        if (!_spotting.GetConfig().WsprnetEnabled) return 0;

        var (rcall, rgrid) = _spotting.ResolveOperator();
        if (string.IsNullOrWhiteSpace(rcall) || string.IsNullOrWhiteSpace(rgrid))
            return 0;

        var posts = new List<Task<bool>>();
        foreach (var spot in batch.Spots)
        {
            if (!WsprnetMessage.TrySplit(spot.Message, out var tcall, out var tgrid, out var dbm))
                continue;
            // Hashed/partial calls can't be uploaded meaningfully.
            if (tcall.Contains('<') || tcall.Contains('>')) continue;

            var form = BuildSpotForm(
                rcall, rgrid,
                batch.DialFreqMhz, batch.SlotStartUtc,
                spot.SnrDb, spot.DtSec, spot.DriftHz, spot.FreqMhz,
                tcall, tgrid, dbm, Version);

            posts.Add(PostSpotAsync(form));
        }

        if (posts.Count == 0) return 0;
        var results = await Task.WhenAll(posts).ConfigureAwait(false);
        int sent = results.Count(ok => ok);
        // Heartbeat: one Info line per batch that landed at least one spot, so the
        // operator can confirm in-app that WSPR spots actually went out.
        if (sent > 0)
            _log.LogInformation("wsprnet.upload sent={Sent} -> {Host}", sent, PostHost);
        return sent;
    }

    // Returns true only when the POST reached WSPRnet with a success status.
    private async Task<bool> PostSpotAsync(IReadOnlyList<KeyValuePair<string, string>> form)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await _http.PostAsync(PostUrl, content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("wsprnet upload returned {Status}", (int)resp.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Tolerate all network failure silently — never crash the decode path.
            _log.LogWarning(ex, "wsprnet upload failed");
            return false;
        }
    }

    /// <summary>
    /// Builds the wsprd-compatible WSPRnet POST form. Pure and deterministic
    /// (date/time derived from <paramref name="slotStartUtc"/> in UTC, MHz at six
    /// decimals, invariant culture) so the field set can be unit-tested directly.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BuildSpotForm(
        string rcall, string rgrid,
        double dialFreqMhz, DateTime slotStartUtc,
        float snrDb, float dtSec, int driftHz, double freqMhz,
        string tcall, string? tgrid, int dbm, string version)
    {
        var utc = slotStartUtc.Kind == DateTimeKind.Utc
            ? slotStartUtc
            : slotStartUtc.ToUniversalTime();
        var inv = CultureInfo.InvariantCulture;

        return new List<KeyValuePair<string, string>>
        {
            new("function", "wspr"),
            new("rcall", rcall),
            new("rgrid", rgrid),
            new("rqrg", dialFreqMhz.ToString("F6", inv)),
            new("date", utc.ToString("yyMMdd", inv)),
            new("time", utc.ToString("HHmm", inv)),
            new("sig", ((int)Math.Round(snrDb)).ToString(inv)),
            new("dt", dtSec.ToString("F1", inv)),
            new("drift", driftHz.ToString(inv)),
            new("tqrg", freqMhz.ToString("F6", inv)),
            new("tcall", tcall),
            new("tgrid", tgrid ?? ""),
            new("dbm", dbm.ToString(inv)),
            new("version", version),
            // WSPR-2 — the only sub-mode the native WSPR path decodes today. Derive
            // from the batch if WSPR-15 / FST4W are ever added.
            new("mode", "2"),
        };
    }

    private static string BuildSoftwareString()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        var ver = v is null ? "" : $" {v.Major}.{v.Minor}.{v.Build}";
        return $"Zeus{ver}";
    }
}

/// <summary>
/// Splits a decoded WSPR message into the heard station's callsign, grid, and
/// power (dBm). Three on-air message types:
///   type 1: <c>CALL GRID4 DBM</c>           (e.g. "K1ABC FN42 37")
///   type 2: <c>CALL DBM</c>                  (no grid; e.g. "K1ABC 37")
///   type 3: <c>&lt;CALL&gt; GRID6 DBM</c>    (hashed call + 6-char grid)
/// Anything else returns false. Pure and unit-tested.
/// </summary>
public static class WsprnetMessage
{
    private static readonly Regex Grid4Re = new("^[A-R]{2}[0-9]{2}$", RegexOptions.Compiled);
    private static readonly Regex Grid6Re = new("^[A-R]{2}[0-9]{2}[A-X]{2}$", RegexOptions.Compiled);
    private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n' };

    public static bool TrySplit(string? message, out string call, out string? grid, out int dbm)
    {
        call = "";
        grid = null;
        dbm = 0;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var t = message.Trim().ToUpperInvariant().Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);

        if (t.Length == 3)
        {
            if (!int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return false;
            if (!Grid4Re.IsMatch(t[1]) && !Grid6Re.IsMatch(t[1])) return false;
            call = t[0];
            grid = t[1];
            dbm = p;
            return true;
        }

        if (t.Length == 2)
        {
            if (!int.TryParse(t[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return false;
            call = t[0];
            grid = null;
            dbm = p;
            return true;
        }

        return false;
    }
}
