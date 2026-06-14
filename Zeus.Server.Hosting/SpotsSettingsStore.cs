// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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
                CwSideband: e.CwSideband ?? "CWU").Normalized();
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
    public DateTime UpdatedUtc { get; set; }
}
