// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Single-row collection for the digital-mode spotting uploaders' config. Mirrors
// WsjtxConfigStore — same Connection=shared pattern and Directory guard (no new
// exposure to the Linux LiteDB shared-mode caveat, GH #682). Both enables persist
// false-on-fresh — egress is opt-in only.
public sealed class SpottingSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SpottingConfigEntry> _entries;
    private readonly ILogger<SpottingSettingsStore> _log;
    private readonly object _sync = new();

    public SpottingSettingsStore(ILogger<SpottingSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<SpottingConfigEntry>("spotting_config");

        _log.LogInformation("SpottingSettingsStore initialized at {Path}", dbPath);
    }

    // null = nothing persisted yet — caller falls back to defaults (both OFF).
    public SpottingRuntimeConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new SpottingRuntimeConfig(
                PskReporterEnabled: e.PskReporterEnabled,
                WsprnetEnabled: e.WsprnetEnabled,
                Callsign: e.Callsign ?? "",
                Grid: e.Grid ?? "");
        }
    }

    public void Set(SpottingRuntimeConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new SpottingConfigEntry
                {
                    PskReporterEnabled = config.PskReporterEnabled,
                    WsprnetEnabled = config.WsprnetEnabled,
                    Callsign = config.Callsign,
                    Grid = config.Grid,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.PskReporterEnabled = config.PskReporterEnabled;
                existing.WsprnetEnabled = config.WsprnetEnabled;
                existing.Callsign = config.Callsign;
                existing.Grid = config.Grid;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _db.Dispose();
}

public sealed class SpottingConfigEntry
{
    public int Id { get; set; }
    public bool PskReporterEnabled { get; set; }
    public bool WsprnetEnabled { get; set; }
    public string? Callsign { get; set; } = "";
    public string? Grid { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}
