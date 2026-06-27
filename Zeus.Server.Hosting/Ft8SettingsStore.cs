// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8SettingsStore — persists the FT8/FT4 workspace behaviour preferences
// (Ft8Settings) in a single-row LiteDB collection sharing zeus-prefs.db,
// mirroring SpottingSettingsStore. First run (no row) returns the Ft8Settings
// defaults, which match the current pre-settings behaviour exactly. These are
// behaviour/UI knobs only — none transmit; TX still requires an explicit arm.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Reads/writes the single <see cref="Ft8Settings"/> row. Thread-safe; values are
/// normalized on write (offset/passes clamped, macros trimmed/capped).
/// </summary>
public sealed class Ft8SettingsStore : IDisposable
{
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

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<Ft8SettingsEntry>("ft8_settings");

        _log.LogInformation("Ft8SettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>The saved settings (normalized), or the defaults if none.</summary>
    public Ft8Settings Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return new Ft8Settings();
            return new Ft8Settings(
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
                ReportToComment: e.ReportToComment).Normalized();
        }
    }

    /// <summary>Persist settings (normalized) and return what was stored.</summary>
    public Ft8Settings Set(Ft8Settings settings)
    {
        var s = settings.Normalized();
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _entries.Insert(FromSettings(new Ft8SettingsEntry(), s, nowUtc));
            }
            else
            {
                FromSettings(existing, s, nowUtc);
                _entries.Update(existing);
            }
        }
        return s;
    }

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
        e.UpdatedUtc = nowUtc;
        return e;
    }

    public void Dispose() => _db.Dispose();
}

public sealed class Ft8SettingsEntry
{
    public int Id { get; set; }
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
    public int DecodePasses { get; set; } = 2;
    public bool ShowOnlyCq { get; set; }
    public bool HideWorkedBefore { get; set; }
    public bool AutoLog { get; set; } = true;
    public bool PromptBeforeLog { get; set; }
    public bool ClearDxAfterLog { get; set; } = true;
    public bool ReportToComment { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
