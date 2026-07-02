// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8SettingsStore — persists the FT8/FT4/WSPR workspace behaviour + display
// preferences (Ft8Settings) PER MODE in a LiteDB collection sharing
// zeus-prefs.db, mirroring FreeDvReporterSettingsStore. Each digital mode (FT8, FT4,
// WSPR) keeps its OWN row keyed by Mode; first run (no row) returns the
// Ft8Settings defaults, which match the current pre-settings behaviour exactly.
// A legacy single, un-keyed row written before per-mode keying is migrated
// in place to the FT8 row so no operator config is lost. These are
// behaviour/UI/display knobs only — none transmit; TX still requires an
// explicit arm.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Reads/writes one <see cref="Ft8Settings"/> row PER digital mode (FT8/FT4/WSPR).
/// Thread-safe; values are normalized on write (offset/passes clamped, macros
/// trimmed/capped, waterfall/display bounded). The legacy single un-keyed row is
/// adopted as the FT8 row on first access so upgrades preserve the operator's
/// existing FT8 config.
/// </summary>
public sealed class Ft8SettingsStore : IDisposable
{
    /// <summary>Mode used when the caller omits one (back-compat with the old
    /// mode-less endpoint and the legacy single-row migration target).</summary>
    public const string DefaultMode = "FT8";

    private static readonly string[] ValidModes = { "FT8", "FT4", "WSPR" };

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Ft8SettingsEntry> _entries;
    private readonly ILogger<Ft8SettingsStore> _log;
    private readonly object _sync = new();

    public Ft8SettingsStore(ILogger<Ft8SettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<Ft8SettingsEntry>("ft8_settings");

        _log.LogInformation("Ft8SettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Whitelist a mode token (case-insensitive); unknown → FT8.</summary>
    public static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return DefaultMode;
        var m = mode.Trim().ToUpperInvariant();
        return Array.IndexOf(ValidModes, m) >= 0 ? m : DefaultMode;
    }

    /// <summary>The default-mode (FT8) settings — back-compat with the
    /// mode-less endpoint.</summary>
    public Ft8Settings Get() => Get(DefaultMode);

    /// <summary>The saved settings for <paramref name="mode"/> (normalized), or
    /// the defaults if that mode has no row yet.</summary>
    public Ft8Settings Get(string mode)
    {
        var m = NormalizeMode(mode);
        lock (_sync)
        {
            var e = FindEntry(m);
            if (e is null) return new Ft8Settings();
            return ToSettings(e).Normalized();
        }
    }

    /// <summary>Persist default-mode (FT8) settings — back-compat overload.</summary>
    public Ft8Settings Set(Ft8Settings settings) => Set(DefaultMode, settings);

    /// <summary>Persist settings (normalized) for <paramref name="mode"/> and
    /// return what was stored. Each mode is an independent row.</summary>
    public Ft8Settings Set(string mode, Ft8Settings settings)
    {
        var m = NormalizeMode(mode);
        var s = settings.Normalized();
        lock (_sync)
        {
            var existing = FindEntry(m);
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                var e = new Ft8SettingsEntry { Mode = m };
                FromSettings(e, s, nowUtc);
                _entries.Insert(e);
            }
            else
            {
                // Re-stamp the row's Mode (a legacy row already hydrates as "FT8";
                // this also future-proofs against any partially-written row).
                existing.Mode = m;
                FromSettings(existing, s, nowUtc);
                _entries.Update(existing);
            }
        }
        return s;
    }

    // The collection holds at most one row per mode (≤3), so an in-memory scan
    // is trivially cheap and avoids LiteDB query-translation edge cases around
    // null/missing Mode on legacy rows.
    //
    // Legacy single-row migration: a row written before per-mode keying has no
    // Mode field in the BSON document. LiteDB leaves fields missing from the
    // document at their C# initializer value, and Ft8SettingsEntry.Mode
    // initializes to DefaultMode ("FT8") — so a legacy row hydrates with
    // Mode == "FT8" and is matched here by the ordinary exact-match lookup, with
    // no separate fallback branch needed. (Keep the Mode initializer at "FT8"
    // for this to hold; do not change it to null without restoring a fallback.)
    private Ft8SettingsEntry? FindEntry(string mode) =>
        _entries.FindAll().FirstOrDefault(
            e => string.Equals(e.Mode, mode, StringComparison.OrdinalIgnoreCase));

    private static Ft8Settings ToSettings(Ft8SettingsEntry e) => new(
        AutoSequence: e.AutoSequence,
        CallFirst: e.CallFirst,
        HoldTxFreq: e.HoldTxFreq,
        DisableTxAfter73: e.DisableTxAfter73,
        DefaultTxSlot: e.DefaultTxSlot,
        DefaultTxOffsetHz: e.DefaultTxOffsetHz,
        Rr73InsteadOfRrr: e.Rr73InsteadOfRrr,
        SkipGrid: e.SkipGrid,
        CallerMaxRetries: e.CallerMaxRetries,
        CqMessage: e.CqMessage ?? "CQ",
        CqDxMessage: e.CqDxMessage ?? "CQ DX",
        FreeTextMacro: e.FreeTextMacro ?? "",
        DecodePasses: e.DecodePasses,
        ShowOnlyCq: e.ShowOnlyCq,
        HideWorkedBefore: e.HideWorkedBefore,
        AutoLog: e.AutoLog,
        PromptBeforeLog: e.PromptBeforeLog,
        ClearDxAfterLog: e.ClearDxAfterLog,
        ReportToComment: e.ReportToComment,
        WfDbMin: e.WfDbMin,
        WfDbMax: e.WfDbMax,
        Palette: e.Palette ?? Ft8Settings.DefaultPalette,
        Rbw: e.Rbw ?? Ft8Settings.DefaultRbw,
        Smoothing: e.Smoothing,
        Zoom: e.Zoom,
        SpanHz: e.SpanHz);

    private static Ft8SettingsEntry FromSettings(Ft8SettingsEntry e, Ft8Settings s, DateTime nowUtc)
    {
        e.AutoSequence = s.AutoSequence;
        e.CallFirst = s.CallFirst;
        e.HoldTxFreq = s.HoldTxFreq;
        e.DisableTxAfter73 = s.DisableTxAfter73;
        e.DefaultTxSlot = s.DefaultTxSlot;
        e.DefaultTxOffsetHz = s.DefaultTxOffsetHz;
        e.Rr73InsteadOfRrr = s.Rr73InsteadOfRrr;
        e.SkipGrid = s.SkipGrid;
        e.CallerMaxRetries = s.CallerMaxRetries;
        e.CqMessage = s.CqMessage;
        e.CqDxMessage = s.CqDxMessage;
        e.FreeTextMacro = s.FreeTextMacro;
        e.DecodePasses = s.DecodePasses;
        e.ShowOnlyCq = s.ShowOnlyCq;
        e.HideWorkedBefore = s.HideWorkedBefore;
        e.AutoLog = s.AutoLog;
        e.PromptBeforeLog = s.PromptBeforeLog;
        e.ClearDxAfterLog = s.ClearDxAfterLog;
        e.ReportToComment = s.ReportToComment;
        e.WfDbMin = s.WfDbMin;
        e.WfDbMax = s.WfDbMax;
        e.Palette = s.Palette;
        e.Rbw = s.Rbw;
        e.Smoothing = s.Smoothing;
        e.Zoom = s.Zoom;
        e.SpanHz = s.SpanHz;
        e.UpdatedUtc = nowUtc;
        return e;
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class Ft8SettingsEntry
{
    public int Id { get; set; }
    /// <summary>Digital mode this row belongs to ("FT8"|"FT4"|"WSPR"). The
    /// initializer is the migration mechanism: a legacy pre-per-mode row has NO
    /// Mode field in its BSON document, and LiteDB leaves missing fields at their
    /// C# initializer value, so such a row hydrates as "FT8" and is adopted as
    /// the FT8 row. Do not change this initializer to null without restoring the
    /// legacy fallback in <see cref="Ft8SettingsStore.FindEntry"/>.</summary>
    public string? Mode { get; set; } = Ft8SettingsStore.DefaultMode;
    public bool AutoSequence { get; set; } = true;
    public bool CallFirst { get; set; }
    public bool HoldTxFreq { get; set; }
    public bool DisableTxAfter73 { get; set; } = true;
    public int DefaultTxSlot { get; set; }
    public int DefaultTxOffsetHz { get; set; } = 1500;
    public bool Rr73InsteadOfRrr { get; set; }
    public bool SkipGrid { get; set; }
    public int CallerMaxRetries { get; set; }
    public string? CqMessage { get; set; } = "CQ";
    public string? CqDxMessage { get; set; } = "CQ DX";
    public string? FreeTextMacro { get; set; } = "";
    // Match the contract default (3 passes = Deep). The old 2 here only ever bit
    // a partially-written row; aligning it removes that latent mismatch.
    public int DecodePasses { get; set; } = 3;
    public bool ShowOnlyCq { get; set; }
    public bool HideWorkedBefore { get; set; }
    public bool AutoLog { get; set; } = true;
    public bool PromptBeforeLog { get; set; }
    public bool ClearDxAfterLog { get; set; } = true;
    public bool ReportToComment { get; set; }
    // Waterfall / display (per-mode). Defaults mirror the contract so a row
    // written before these fields existed deserialises to sane values.
    public double WfDbMin { get; set; } = Ft8Settings.DefaultWfDbMin;
    public double WfDbMax { get; set; } = Ft8Settings.DefaultWfDbMax;
    public string? Palette { get; set; } = Ft8Settings.DefaultPalette;
    public string? Rbw { get; set; } = Ft8Settings.DefaultRbw;
    public int Smoothing { get; set; } = Ft8Settings.DefaultSmoothing;
    public double Zoom { get; set; } = Ft8Settings.DefaultZoom;
    public int SpanHz { get; set; } = Ft8Settings.DefaultSpanHz;
    public DateTime UpdatedUtc { get; set; }
}
