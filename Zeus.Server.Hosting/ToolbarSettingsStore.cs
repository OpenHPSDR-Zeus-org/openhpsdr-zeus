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

// Persists the toolbar Mode/Band/Step favorite-slot pins and the currently-
// selected tuning step (StepHz). Lives in the same zeus-prefs.db as PA /
// band-memory / layout / display settings — none of these values are
// sensitive.
//
// Why server-side: the previous implementation stored these in browser
// localStorage via zustand's persist middleware. localStorage is keyed by
// origin, and the Photino desktop backend binds the webview to a fresh
// OS-assigned random loopback port on every launch — so each launch was a
// new origin with empty localStorage, resetting the tuning step to its
// 500 Hz default every time. Moving it to LiteDB lets the setting survive a
// backend restart and follow the operator across every browser pointed at the
// Zeus instance. Same pattern as DisplaySettingsStore.
public sealed class ToolbarSettingsStore : IDisposable
{
    public const int DefaultStepHz = 500;
    public const int MaxStepHz = 5_000;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ToolbarSettingsEntry> _docs;
    private readonly ILogger<ToolbarSettingsStore> _log;
    private readonly object _sync = new();
    private bool _stepCacheLoaded;
    private int? _cachedStepHz;

    public ToolbarSettingsStore(ILogger<ToolbarSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<ToolbarSettingsEntry>("toolbar_settings");

        _log.LogInformation("ToolbarSettingsStore initialized at {Path}", dbPath);
    }

    public ToolbarSettingsDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null)
            {
                _cachedStepHz = null;
                _stepCacheLoaded = true;
                // Null fields tell the frontend the server has never stored a
                // value, so it keeps its built-in defaults and pushes them up.
                return new ToolbarSettingsDto(Mode: null, Band: null, Step: null, StepHz: null);
            }
            _cachedStepHz = e.StepHz;
            _stepCacheLoaded = true;
            return new ToolbarSettingsDto(
                Mode: NormalizeSlots(e.Mode),
                Band: NormalizeSlots(e.Band),
                Step: NormalizeSlots(e.Step),
                StepHz: e.StepHz);
        }
    }

    public int CurrentStepHz
    {
        get
        {
            lock (_sync)
            {
                EnsureStepCacheLocked();
                return NormalizeStepHz(_cachedStepHz);
            }
        }
    }

    // Null arguments are treated as "not provided" — the existing stored value
    // is kept unchanged. This lets a caller update only StepHz (the common
    // case) without disturbing the favorite-slot pins, and vice-versa.
    public void Save(
        IReadOnlyList<string>? mode = null,
        IReadOnlyList<string>? band = null,
        IReadOnlyList<string>? step = null,
        int? stepHz = null)
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new ToolbarSettingsEntry();
            if (mode is not null) e.Mode = NormalizeSlots(mode);
            if (band is not null) e.Band = NormalizeSlots(band);
            if (step is not null) e.Step = NormalizeSlots(step);
            if (stepHz.HasValue) e.StepHz = stepHz;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
            _cachedStepHz = e.StepHz;
            _stepCacheLoaded = true;
        }
    }

    public void Dispose() => _db.Dispose();

    internal static int NormalizeStepHz(int? stepHz) =>
        stepHz is >= 1 and <= MaxStepHz ? stepHz.Value : DefaultStepHz;

    private void EnsureStepCacheLocked()
    {
        if (_stepCacheLoaded) return;
        _cachedStepHz = _docs.FindAll().FirstOrDefault()?.StepHz;
        _stepCacheLoaded = true;
    }

    // Favorite slots are always exactly three keys. A malformed array (wrong
    // length, null entries) is rejected as null so the frontend falls back to
    // its defaults rather than rendering a broken picker.
    private static List<string>? NormalizeSlots(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count != 3) return null;
        var copy = new List<string>(3);
        foreach (var s in raw)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            copy.Add(s);
        }
        return copy;
    }
}

public sealed class ToolbarSettingsEntry
{
    public int Id { get; set; }
    // Three favorite-slot keys per picker. Null on legacy / fresh rows — Get()
    // returns null to the frontend, which then keeps its built-in defaults.
    public List<string>? Mode { get; set; }
    public List<string>? Band { get; set; }
    public List<string>? Step { get; set; }
    // Currently-selected tuning step in Hz. Null on rows written before this
    // field existed — Get() returns null and the frontend falls back to its
    // 500 Hz default.
    public int? StepHz { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
