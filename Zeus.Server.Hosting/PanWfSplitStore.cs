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

// Persists the vertical split between the panadapter and the waterfall
// inside the Hero panel. Value is the panadapter share as a percentage
// (10..90); the waterfall takes the remainder. Single global value for
// now — if multi-RX panadapters land we can key per-receiver later.
//
// Lives in zeus-prefs.db alongside the other UI prefs (BottomPin,
// NrUiPrefs, ThemeSettings, …) so the choice follows the operator across
// browsers / devices. Same pattern as BottomPinStore — server-side, not
// localStorage, per project_litedb_tx_filter_persistence /
// project_rotator_resume lessons.
public sealed class PanWfSplitStore : IDisposable
{
    public const double DefaultPanPercent = 50.0;
    public const double MinPanPercent = 10.0;
    public const double MaxPanPercent = 90.0;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PanWfSplitEntry> _docs;
    private readonly ILogger<PanWfSplitStore> _log;
    private readonly object _sync = new();

    public PanWfSplitStore(ILogger<PanWfSplitStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<PanWfSplitEntry>("pan_wf_split");

        _log.LogInformation("PanWfSplitStore initialized at {Path}", dbPath);
    }

    public PanWfSplitDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null) return new PanWfSplitDto(PanPercent: DefaultPanPercent);
            return new PanWfSplitDto(PanPercent: Clamp(e.PanPercent));
        }
    }

    public PanWfSplitDto Save(double panPercent)
    {
        var clamped = Clamp(panPercent);
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new PanWfSplitEntry();
            e.PanPercent = clamped;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
        return new PanWfSplitDto(PanPercent: clamped);
    }

    private static double Clamp(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return DefaultPanPercent;
        if (v < MinPanPercent) return MinPanPercent;
        if (v > MaxPanPercent) return MaxPanPercent;
        return v;
    }

    public void Dispose() => _db.Dispose();
}

public sealed class PanWfSplitEntry
{
    public int Id { get; set; }
    public double PanPercent { get; set; } = PanWfSplitStore.DefaultPanPercent;
    public DateTime UpdatedUtc { get; set; }
}
