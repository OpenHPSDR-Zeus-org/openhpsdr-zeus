// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists the panadapter background mode (basic / beam-map / image), the
// image-fit variant, and (optionally) a single user-supplied background
// image stored as raw bytes. Lives in the same zeus-prefs.db as PA / band-
// memory / layout — none of these values are sensitive.
//
// Why server-side: the previous implementation stored panBackground +
// backgroundImage in browser localStorage, which is per-origin and per-
// device. Operators connecting from a phone (different origin / device than
// the desktop where the picture was set) saw an empty panel. Moving it to
// LiteDB lets a single setting follow the operator across every browser
// pointed at the Zeus instance.
public sealed class DisplaySettingsStore : IDisposable
{
    // Defensive bounds for any persisted dB window. Mirrors DB_ABS_LIMIT /
    // MIN_SPAN_DB in zeus-web's display-settings-store.ts. A range outside
    // these bounds (or with span < MIN_SPAN_DB) would render the panadapter
    // or waterfall as a single flat colour, so we drop the write instead of
    // poisoning zeus-prefs.db. Either endpoint being null means "field not
    // provided in this PUT" and is handled by the caller.
    private const double DbAbsLimit = 200;
    private const double MinSpanDb = 20;

    private static bool IsValidRange(double? min, double? max)
    {
        if (!min.HasValue || !max.HasValue) return false;
        if (double.IsNaN(min.Value) || double.IsNaN(max.Value)) return false;
        if (double.IsInfinity(min.Value) || double.IsInfinity(max.Value)) return false;
        if (min.Value < -DbAbsLimit || max.Value > DbAbsLimit) return false;
        if (max.Value - min.Value < MinSpanDb) return false;
        return true;
    }

    // Bounds for the TX display analyzer parameters. These are display-only
    // (they shape the transmitted-signal visualization, never the air), but we
    // still guard the persisted values so a malformed PUT can't poison the DB
    // or push an out-of-range value into the WDSP analyzer reconfig.
    private const double TxCalOffsetAbsDb = 60.0;
    private const double TxAvgTauMaxMs = 2000.0;
    // Power-of-two FFT sizes the WDSP TX analyzer accepts. 16384 is the engine
    // default (matches the RX analyzer); smaller = faster/less resolution,
    // larger = finer bins / heavier compute.
    private static readonly int[] TxFftSizes = { 2048, 4096, 8192, 16384, 32768, 65536 };

    private static bool IsValidCalOffset(double? v) =>
        v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)
        && Math.Abs(v.Value) <= TxCalOffsetAbsDb;

    private static bool IsValidFftSize(int? v) => v.HasValue && Array.IndexOf(TxFftSizes, v.Value) >= 0;

    // WDSP win_type values 0..11 (analyzer.c). Keep the accept range generous;
    // the frontend only surfaces a friendly subset.
    private static bool IsValidWindow(int? v) => v.HasValue && v.Value >= 0 && v.Value <= 11;

    private static bool IsValidAvgTauMs(double? v) =>
        v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value)
        && v.Value >= 0.0 && v.Value <= TxAvgTauMaxMs;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DisplaySettingsEntry> _docs;
    private readonly ILogger<DisplaySettingsStore> _log;
    private readonly object _sync = new();

    public DisplaySettingsStore(ILogger<DisplaySettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<DisplaySettingsEntry>("display_settings");

        _log.LogInformation("DisplaySettingsStore initialized at {Path}", dbPath);
    }

    public DisplaySettingsDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null)
            {
                return new DisplaySettingsDto(
                    Mode: "basic",
                    Fit: "fill",
                    HasImage: false,
                    ImageMime: null,
                    RxTraceColor: DefaultRxTraceColor,
                    DbMin: null,
                    DbMax: null,
                    TxDbMin: null,
                    TxDbMax: null,
                    WfDbMin: null,
                    WfDbMax: null,
                    WfTxDbMin: null,
                    WfTxDbMax: null,
                    TxDisplayCalOffsetDb: null,
                    TxDisplayFftSize: null,
                    TxDisplayWindow: null,
                    TxDisplayAvgTauMs: null);
            }
            return new DisplaySettingsDto(
                Mode: NormalizeMode(e.Mode),
                Fit: NormalizeFit(e.Fit),
                HasImage: e.ImageBytes is { Length: > 0 },
                ImageMime: string.IsNullOrEmpty(e.ImageMime) ? null : e.ImageMime,
                RxTraceColor: NormalizeHexColor(e.RxTraceColor),
                DbMin: e.DbMin,
                DbMax: e.DbMax,
                TxDbMin: e.TxDbMin,
                TxDbMax: e.TxDbMax,
                WfDbMin: e.WfDbMin,
                WfDbMax: e.WfDbMax,
                WfTxDbMin: e.WfTxDbMin,
                WfTxDbMax: e.WfTxDbMax,
                TxDisplayCalOffsetDb: e.TxDisplayCalOffsetDb,
                TxDisplayFftSize: e.TxDisplayFftSize,
                TxDisplayWindow: e.TxDisplayWindow,
                TxDisplayAvgTauMs: e.TxDisplayAvgTauMs);
        }
    }

    // Null dB range values are treated as "not provided" — the existing
    // stored value is kept unchanged. This lets callers updating only
    // mode/fit/color leave the operator's dB scale untouched.
    public void SaveMode(string mode, string fit, string rxTraceColor,
        double? dbMin = null, double? dbMax = null,
        double? txDbMin = null, double? txDbMax = null,
        double? wfDbMin = null, double? wfDbMax = null,
        double? wfTxDbMin = null, double? wfTxDbMax = null,
        double? txDisplayCalOffsetDb = null, int? txDisplayFftSize = null,
        int? txDisplayWindow = null, double? txDisplayAvgTauMs = null)
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new DisplaySettingsEntry();
            e.Mode = NormalizeMode(mode);
            e.Fit = NormalizeFit(fit);
            e.RxTraceColor = NormalizeHexColor(rxTraceColor);
            // Accept a (min, max) pair only when it's a non-degenerate window.
            // The old code wrote whatever the client sent, so a dB drag that
            // hit the abs-limit on both endpoints persisted min == max == -200
            // and the next page load rendered a solid colour. See zeus-web's
            // sanitizeRange + clampShift for the matching client-side guard.
            if (IsValidRange(dbMin, dbMax)) { e.DbMin = dbMin; e.DbMax = dbMax; }
            if (IsValidRange(txDbMin, txDbMax)) { e.TxDbMin = txDbMin; e.TxDbMax = txDbMax; }
            if (IsValidRange(wfDbMin, wfDbMax)) { e.WfDbMin = wfDbMin; e.WfDbMax = wfDbMax; }
            if (IsValidRange(wfTxDbMin, wfTxDbMax)) { e.WfTxDbMin = wfTxDbMin; e.WfTxDbMax = wfTxDbMax; }
            // TX display analyzer params — each validated independently so a
            // caller can update one knob without clobbering the others.
            if (IsValidCalOffset(txDisplayCalOffsetDb)) e.TxDisplayCalOffsetDb = txDisplayCalOffsetDb;
            if (IsValidFftSize(txDisplayFftSize)) e.TxDisplayFftSize = txDisplayFftSize;
            if (IsValidWindow(txDisplayWindow)) e.TxDisplayWindow = txDisplayWindow;
            if (IsValidAvgTauMs(txDisplayAvgTauMs)) e.TxDisplayAvgTauMs = txDisplayAvgTauMs;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    // Returns (bytes, mime) or null when no image is stored.
    public (byte[] Bytes, string Mime)? GetImage()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null || e.ImageBytes is null || e.ImageBytes.Length == 0) return null;
            var mime = string.IsNullOrEmpty(e.ImageMime) ? "application/octet-stream" : e.ImageMime;
            return (e.ImageBytes, mime);
        }
    }

    public void SaveImage(byte[] bytes, string mime)
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new DisplaySettingsEntry();
            e.ImageBytes = bytes;
            e.ImageMime = string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    public void DeleteImage()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null) return;
            e.ImageBytes = null;
            e.ImageMime = null;
            e.UpdatedUtc = DateTime.UtcNow;
            _docs.Update(e);
        }
    }

    public void Dispose() => _db.Dispose();

    private static string NormalizeMode(string? raw) =>
        raw switch
        {
            "basic" or "beam-map" or "image" => raw,
            _ => "basic",
        };

    private static string NormalizeFit(string? raw) =>
        raw switch
        {
            "fit" or "fill" or "stretch" => raw,
            _ => "fill",
        };

    // Matches the frontend's DEFAULT_RX_TRACE_COLOR in display-settings-store.ts
    // and the original hard-coded #FFA028 in gl/panadapter.ts.
    public const string DefaultRxTraceColor = "#FFA028";

    private static string NormalizeHexColor(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return DefaultRxTraceColor;
        if (raw.Length != 7 || raw[0] != '#') return DefaultRxTraceColor;
        for (var i = 1; i < 7; i++)
        {
            var c = raw[i];
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return DefaultRxTraceColor;
        }
        return raw.ToUpperInvariant();
    }

}

public sealed class DisplaySettingsEntry
{
    public int Id { get; set; }
    public string Mode { get; set; } = "basic";
    public string Fit { get; set; } = "fill";
    // Inline byte[] keeps the doc self-contained; LiteDB handles BSON blobs
    // up to 16 MB per document, which is well over any realistic background
    // image. If we ever need bigger, swap to LiteFileStorage and store an
    // id here instead.
    public byte[]? ImageBytes { get; set; }
    public string? ImageMime { get; set; }
    // Panadapter signal trace colour as #RRGGBB. Null on legacy rows written
    // before this field existed — Get() normalises null → DefaultRxTraceColor.
    public string? RxTraceColor { get; set; }
    // Panadapter and waterfall dB window bounds. Null on rows written before
    // this field existed — Get() returns null to the frontend, which then
    // falls back to its built-in FIXED_DB_MIN / TX_FIXED_DB_MIN constants and
    // pushes the localStorage-saved (or default) value up on first interaction.
    public double? DbMin { get; set; }
    public double? DbMax { get; set; }
    public double? TxDbMin { get; set; }
    public double? TxDbMax { get; set; }
    public double? WfDbMin { get; set; }
    public double? WfDbMax { get; set; }
    public double? WfTxDbMin { get; set; }
    public double? WfTxDbMax { get; set; }
    // TX display analyzer params (display-only). Null on rows written before
    // these fields existed — Get() returns null and the engine applies its
    // built-in defaults (16384-bin FFT, Hann window (win_type 2), 175 ms
    // smoothing, 0 dB cal offset).
    public double? TxDisplayCalOffsetDb { get; set; }
    public int? TxDisplayFftSize { get; set; }
    public int? TxDisplayWindow { get; set; }
    public double? TxDisplayAvgTauMs { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
