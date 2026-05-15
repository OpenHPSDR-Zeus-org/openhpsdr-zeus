// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists the per-mode expand/collapse state of the inline NR settings
// accordion that hangs below the DSP panel's NR toggle row (NR1/NR2/NR4
// chevron-headers). The expanded flag was previously kept in a non-persisted
// Zustand store on the client, so the panel collapsed on every reload —
// operators who routinely tweak NR2 EMNR tunables had to re-open the
// section every time. Moving it to LiteDB lets the choice follow them
// across browsers + devices, same pattern as DisplaySettingsStore.
//
// Lives in zeus-prefs.db alongside the other non-sensitive preferences.
public sealed class NrUiPrefsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<NrUiPrefsEntry> _docs;
    private readonly ILogger<NrUiPrefsStore> _log;
    private readonly object _sync = new();

    public NrUiPrefsStore(ILogger<NrUiPrefsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<NrUiPrefsEntry>("nr_ui_prefs");

        _log.LogInformation("NrUiPrefsStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// Returns the persisted disclosure state. All three booleans default to
    /// false on a fresh install — the dense NR2 gauge panel is too much for
    /// the casual operator, so the historical "collapsed by default" UX is
    /// preserved.
    /// </summary>
    public NrUiPrefsDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null)
            {
                return new NrUiPrefsDto(Nr1Expanded: false, Nr2Expanded: false, Nr4Expanded: false);
            }
            return new NrUiPrefsDto(
                Nr1Expanded: e.Nr1Expanded,
                Nr2Expanded: e.Nr2Expanded,
                Nr4Expanded: e.Nr4Expanded);
        }
    }

    public void Set(bool nr1Expanded, bool nr2Expanded, bool nr4Expanded)
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new NrUiPrefsEntry();
            e.Nr1Expanded = nr1Expanded;
            e.Nr2Expanded = nr2Expanded;
            e.Nr4Expanded = nr4Expanded;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    public void Dispose() => _db.Dispose();
}

public sealed class NrUiPrefsEntry
{
    public int Id { get; set; }
    public bool Nr1Expanded { get; set; }
    public bool Nr2Expanded { get; set; }
    public bool Nr4Expanded { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
