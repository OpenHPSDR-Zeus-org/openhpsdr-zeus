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
// SpotsSettingsStore — persists the operator's POTA/SOTA Spots settings in a
// single-row LiteDB collection sharing zeus-prefs.db, mirroring
// AudioProcessingModeStore. First run (no row) returns the SpotsSettings
// defaults, so the feature works out of the box.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Reads/writes the <see cref="SpotsSettings"/> row. Thread-safe; values are
/// normalized on write so the poller and panel always see a sane config.
/// </summary>
public sealed class SpotsSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SpotsSettingsEntry> _state;
    private readonly ILogger<SpotsSettingsStore> _log;
    private readonly object _sync = new();

    public SpotsSettingsStore(ILogger<SpotsSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _state = _db.GetCollection<SpotsSettingsEntry>("spots_settings");

        _log.LogInformation("SpotsSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Current settings, or the defaults if nothing has been saved yet.</summary>
    public SpotsSettings Get()
    {
        lock (_sync)
        {
            var e = _state.FindAll().FirstOrDefault();
            if (e is null) return new SpotsSettings();
            return new SpotsSettings(
                Enabled: e.Enabled,
                PotaEnabled: e.PotaEnabled,
                SotaEnabled: e.SotaEnabled,
                PollIntervalSeconds: e.PollIntervalSeconds,
                SetModeOnTune: e.SetModeOnTune,
                TuneOnlyWhenConnected: e.TuneOnlyWhenConnected,
                CwSideband: e.CwSideband ?? "CWU",
                Bands: e.Bands,
                Modes: e.Modes,
                HideQrt: e.HideQrt,
                MaxAgeMinutes: e.MaxAgeMinutes,
                LatestPerActivator: e.LatestPerActivator,
                CwTuneOffsetHz: e.CwTuneOffsetHz,
                DigiTuneOffsetHz: e.DigiTuneOffsetHz,
                DxEnabled: e.DxEnabled,
                // Null on older rows; Normalized() resolves blanks to defaults.
                PotaUrl: e.PotaUrl ?? "",
                SotaUrl: e.SotaUrl ?? "",
                DxUrl: e.DxUrl ?? "",
                Watchlist: e.Watchlist,
                AlertsEnabled: e.AlertsEnabled,
                AlertSound: e.AlertSound,
                HideWorked: e.HideWorked,
                EnrichQrz: e.EnrichQrz,
                // Older rows default the column to 0; coerce to the seed so the
                // scanner doesn't dwell for zero seconds. Normalized() clamps.
                ScanDwellSeconds: e.ScanDwellSeconds <= 0 ? 8 : e.ScanDwellSeconds).Normalized();
        }
    }

    /// <summary>Persist settings (normalized) and return what was stored.</summary>
    public SpotsSettings Set(SpotsSettings settings)
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
                existing.Enabled = s.Enabled;
                existing.PotaEnabled = s.PotaEnabled;
                existing.SotaEnabled = s.SotaEnabled;
                existing.PollIntervalSeconds = s.PollIntervalSeconds;
                existing.SetModeOnTune = s.SetModeOnTune;
                existing.TuneOnlyWhenConnected = s.TuneOnlyWhenConnected;
                existing.CwSideband = s.CwSideband;
                existing.Bands = s.Bands?.ToList();
                existing.Modes = s.Modes?.ToList();
                existing.HideQrt = s.HideQrt;
                existing.MaxAgeMinutes = s.MaxAgeMinutes;
                existing.LatestPerActivator = s.LatestPerActivator;
                existing.CwTuneOffsetHz = s.CwTuneOffsetHz;
                existing.DigiTuneOffsetHz = s.DigiTuneOffsetHz;
                existing.DxEnabled = s.DxEnabled;
                existing.PotaUrl = s.PotaUrl;
                existing.SotaUrl = s.SotaUrl;
                existing.DxUrl = s.DxUrl;
                existing.Watchlist = s.Watchlist?.ToList();
                existing.AlertsEnabled = s.AlertsEnabled;
                existing.AlertSound = s.AlertSound;
                existing.HideWorked = s.HideWorked;
                existing.EnrichQrz = s.EnrichQrz;
                existing.ScanDwellSeconds = s.ScanDwellSeconds;
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }
        return s;
    }

    private static SpotsSettingsEntry FromSettings(SpotsSettings s, DateTime nowUtc) => new()
    {
        Enabled = s.Enabled,
        PotaEnabled = s.PotaEnabled,
        SotaEnabled = s.SotaEnabled,
        PollIntervalSeconds = s.PollIntervalSeconds,
        SetModeOnTune = s.SetModeOnTune,
        TuneOnlyWhenConnected = s.TuneOnlyWhenConnected,
        CwSideband = s.CwSideband,
        Bands = s.Bands?.ToList(),
        Modes = s.Modes?.ToList(),
        HideQrt = s.HideQrt,
        MaxAgeMinutes = s.MaxAgeMinutes,
        LatestPerActivator = s.LatestPerActivator,
        CwTuneOffsetHz = s.CwTuneOffsetHz,
        DigiTuneOffsetHz = s.DigiTuneOffsetHz,
        DxEnabled = s.DxEnabled,
        PotaUrl = s.PotaUrl,
        SotaUrl = s.SotaUrl,
        DxUrl = s.DxUrl,
        Watchlist = s.Watchlist?.ToList(),
        AlertsEnabled = s.AlertsEnabled,
        AlertSound = s.AlertSound,
        HideWorked = s.HideWorked,
        EnrichQrz = s.EnrichQrz,
        ScanDwellSeconds = s.ScanDwellSeconds,
        UpdatedUtc = nowUtc,
    };

    public void Dispose() => _db.Dispose();
}

public sealed class SpotsSettingsEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    public bool PotaEnabled { get; set; } = true;
    public bool SotaEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public bool SetModeOnTune { get; set; } = true;
    public bool TuneOnlyWhenConnected { get; set; } = true;
    public string? CwSideband { get; set; } = "CWU";
    // Stored as plain string lists; null/absent (older rows) means "no filter".
    public List<string>? Bands { get; set; }
    public List<string>? Modes { get; set; }
    public bool HideQrt { get; set; } = true;
    public int MaxAgeMinutes { get; set; }
    public bool LatestPerActivator { get; set; }
    public int CwTuneOffsetHz { get; set; }
    public int DigiTuneOffsetHz { get; set; }
    public bool DxEnabled { get; set; }
    // Null/absent on older rows; SpotsSettings.Normalized() resolves to defaults.
    public string? PotaUrl { get; set; }
    public string? SotaUrl { get; set; }
    public string? DxUrl { get; set; }
    // Watchlist callsigns + alert/enrichment prefs. Null/absent on older rows.
    public List<string>? Watchlist { get; set; }
    public bool AlertsEnabled { get; set; }
    public bool AlertSound { get; set; } = true;
    public bool HideWorked { get; set; }
    public bool EnrichQrz { get; set; }
    public int ScanDwellSeconds { get; set; } = 8;
    public DateTime UpdatedUtc { get; set; }
}
