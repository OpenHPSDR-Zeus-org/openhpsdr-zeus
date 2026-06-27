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
// FreeDvReporterSettingsStore — persists the operator's FreeDV Reporter
// "report mode" opt-in (callsign + grid + optional status message) in a
// single-row LiteDB collection sharing zeus-prefs.db, mirroring
// SpotsSettingsStore. First run (no row) returns the FreeDvReporterSettings
// defaults: report mode OFF. Reporting only ever broadcasts the operator's
// location after an explicit opt-in, so the default-off behaviour is a safety
// property — see FreeDvReporterService.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Reads/writes the <see cref="FreeDvReporterSettings"/> row. Thread-safe; values
/// are normalized on write so the reporter link and panel always see a sane
/// config (callsign upper-cased, grid Maidenhead-shaped, message capped).
/// </summary>
public sealed class FreeDvReporterSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<FreeDvReporterSettingsEntry> _state;
    private readonly ILogger<FreeDvReporterSettingsStore> _log;
    private readonly object _sync = new();

    public FreeDvReporterSettingsStore(ILogger<FreeDvReporterSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _state = _db.GetCollection<FreeDvReporterSettingsEntry>("freedv_reporter_settings");

        _log.LogInformation("FreeDvReporterSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Current settings, or the defaults (report OFF) if nothing has been saved yet.</summary>
    public FreeDvReporterSettings Get()
    {
        lock (_sync)
        {
            var e = _state.FindAll().FirstOrDefault();
            if (e is null) return new FreeDvReporterSettings();
            return new FreeDvReporterSettings(
                ReportEnabled: e.ReportEnabled,
                Callsign: e.Callsign ?? "",
                GridSquare: e.GridSquare ?? "",
                Message: e.Message ?? "").Normalized();
        }
    }

    /// <summary>Persist settings (normalized) and return what was stored.</summary>
    public FreeDvReporterSettings Set(FreeDvReporterSettings settings)
    {
        var s = settings.Normalized();
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _state.Insert(FromSettings(s, nowUtc));
            }
            else
            {
                existing.ReportEnabled = s.ReportEnabled;
                existing.Callsign = s.Callsign;
                existing.GridSquare = s.GridSquare;
                existing.Message = s.Message;
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }
        return s;
    }

    private static FreeDvReporterSettingsEntry FromSettings(FreeDvReporterSettings s, DateTime nowUtc) => new()
    {
        ReportEnabled = s.ReportEnabled,
        Callsign = s.Callsign,
        GridSquare = s.GridSquare,
        Message = s.Message,
        UpdatedUtc = nowUtc,
    };

    public void Dispose() => _dbLease.Dispose();
}

public sealed class FreeDvReporterSettingsEntry
{
    public int Id { get; set; }
    public bool ReportEnabled { get; set; }
    public string? Callsign { get; set; }
    public string? GridSquare { get; set; }
    public string? Message { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
