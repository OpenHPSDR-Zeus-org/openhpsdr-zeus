// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists per-mode VAR1/VAR2 overrides and the last-selected preset slot
// across server restarts. Lives in the shared zeus-prefs.db file.
//
// On first run, USB and LSB VAR1 are seeded with Zeus's wider 150/2850 default
// (PRD §9 open question: preserve Zeus's low-cut as VAR1 on first run).
public sealed class FilterPresetStore : IDisposable
{
    // BsonMapper.Global is a shared, lazily-built entity map. When multiple
    // WebApplicationFactory hosts boot in parallel (xUnit test collections),
    // concurrent first-touches of a type can lose the race with EnsureIndex's
    // LINQ resolver and throw "Member X not found on BsonMapper". Force the
    // entity mapping to be materialized once, under a static lock, before any
    // collection constructs a LINQ expression that walks its members.
    private static readonly object _mapperInitLock = new();
    private static bool _mapperInitialized;

    private static void EnsureMapperRegistered()
    {
        if (_mapperInitialized) return;
        lock (_mapperInitLock)
        {
            if (_mapperInitialized) return;
            BsonMapper.Global.Entity<FilterPresetStoreEntry>()
                .Id(x => x.Id);
            _mapperInitialized = true;
        }
    }

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<FilterPresetStoreEntry> _entries;
    private readonly ILogger<FilterPresetStore> _log;
    private readonly object _sync = new();

    public FilterPresetStore(ILogger<FilterPresetStore> log)
    {
        _log = log;
        EnsureMapperRegistered();

        var dbPath = PrefsDbPath.Get();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<FilterPresetStoreEntry>("filter_presets");
        _entries.EnsureIndex("ModeKey", "$.ModeKey", unique: true);

        SeedDefaults();
        _log.LogInformation("FilterPresetStore initialized at {Path}", dbPath);
    }

    private FilterPresetStoreEntry? FindByMode(string key) =>
        _entries.FindOne("$.ModeKey = @0", key);

    // Returns the stored override for a VAR slot, or null if not overridden.
    public (int LowHz, int HighHz)? GetVarOverride(RxMode mode, string slotName)
    {
        lock (_sync)
        {
            var e = FindByMode(mode.ToString());
            if (e is null) return null;
            return slotName == "VAR1"
                ? (e.HasVar1 ? (e.Var1Lo, e.Var1Hi) : null)
                : slotName == "VAR2"
                    ? (e.HasVar2 ? (e.Var2Lo, e.Var2Hi) : null)
                    : null;
        }
    }

    public void UpsertVarOverride(RxMode mode, string slotName, int loHz, int hiHz)
    {
        var key = mode.ToString();
        lock (_sync)
        {
            var existing = FindByMode(key);
            if (existing is null)
            {
                existing = new FilterPresetStoreEntry { ModeKey = key };
                if (slotName == "VAR1") { existing.HasVar1 = true; existing.Var1Lo = loHz; existing.Var1Hi = hiHz; }
                else                    { existing.HasVar2 = true; existing.Var2Lo = loHz; existing.Var2Hi = hiHz; }
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Insert(existing);
            }
            else
            {
                if (slotName == "VAR1") { existing.HasVar1 = true; existing.Var1Lo = loHz; existing.Var1Hi = hiHz; }
                else                    { existing.HasVar2 = true; existing.Var2Lo = loHz; existing.Var2Hi = hiHz; }
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public string? GetLastSelectedPreset(RxMode mode)
    {
        lock (_sync)
        {
            return FindByMode(mode.ToString())?.LastPreset;
        }
    }

    public void UpsertLastSelectedPreset(RxMode mode, string presetName)
    {
        var key = mode.ToString();
        lock (_sync)
        {
            var existing = FindByMode(key);
            if (existing is null)
            {
                _entries.Insert(new FilterPresetStoreEntry
                {
                    ModeKey = key,
                    LastPreset = presetName,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.LastPreset = presetName;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    // Sentinel mode key used for pane-visibility and other ribbon-scope flags
    // that aren't tied to any particular RX mode. Keeps the schema flat while
    // avoiding a second LiteDB collection just for a bool.
    private const string SettingsKey = "__SETTINGS__";

    // Advanced-ribbon visibility, persisted across server restarts so the
    // operator's close-the-ribbon choice sticks.
    public bool GetAdvancedPaneOpen()
    {
        lock (_sync)
        {
            return FindByMode(SettingsKey)?.AdvancedPaneOpen ?? false;
        }
    }

    public void SetAdvancedPaneOpen(bool open)
    {
        lock (_sync)
        {
            var existing = FindByMode(SettingsKey);
            if (existing is null)
            {
                _entries.Insert(new FilterPresetStoreEntry
                {
                    ModeKey = SettingsKey,
                    AdvancedPaneOpen = open,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.AdvancedPaneOpen = open;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    // Get favorite filter slots for a mode. Returns up to 3 slot names (e.g., ["F6", "F5", "F4"]).
    // Returns default favorites if not set: F6 (2.7k), F5 (2.9k), F4 (3.3k) for USB/LSB.
    public string[] GetFavoriteSlots(RxMode mode)
    {
        lock (_sync)
        {
            var existing = FindByMode(mode.ToString());
            if (existing?.FavoriteSlots is not null)
            {
                return existing.FavoriteSlots.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
            // Default favorites: 2.7k (F6), 2.9k (F5), 3.3k (F4) for USB/LSB
            return mode switch
            {
                RxMode.USB or RxMode.LSB => new[] { "F6", "F5", "F4" },
                RxMode.CWU or RxMode.CWL => new[] { "F4", "F5", "F6" }, // 500, 400, 250 Hz
                RxMode.AM or RxMode.SAM => new[] { "F7", "F8", "F9" }, // 8.0k, 7.0k, 6.0k
                RxMode.DSB => new[] { "F6", "F7", "F8" }, // 5.2k, 4.0k, 3.1k
                RxMode.DIGL or RxMode.DIGU => new[] { "F6", "F5", "F4" }, // 800, 1.0k, 1.5k
                _ => new[] { "F6", "F5", "F4" }
            };
        }
    }

    // Set favorite filter slots for a mode. Up to 3 slot names allowed.
    public void SetFavoriteSlots(RxMode mode, string[] slotNames)
    {
        if (slotNames.Length > 3)
            throw new ArgumentException("Maximum 3 favorite slots allowed", nameof(slotNames));

        var key = mode.ToString();
        var csv = string.Join(',', slotNames.Take(3));

        lock (_sync)
        {
            var existing = FindByMode(key);
            if (existing is null)
            {
                _entries.Insert(new FilterPresetStoreEntry
                {
                    ModeKey = key,
                    FavoriteSlots = csv,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.FavoriteSlots = csv;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    // Seed USB and LSB VAR1 with Zeus's 150/2850 default on first run so the
    // operator sees a familiar starting point (PRD §9 decision).
    private void SeedDefaults()
    {
        SeedVarIfAbsent(RxMode.USB, "VAR1",  150,  2850);
        SeedVarIfAbsent(RxMode.LSB, "VAR1", -2850, -150);
    }

    private void SeedVarIfAbsent(RxMode mode, string slotName, int loHz, int hiHz)
    {
        var key = mode.ToString();
        var existing = FindByMode(key);
        if (existing is null)
        {
            var entry = new FilterPresetStoreEntry
            {
                ModeKey = key,
                UpdatedUtc = DateTime.UtcNow,
            };
            if (slotName == "VAR1") { entry.HasVar1 = true; entry.Var1Lo = loHz; entry.Var1Hi = hiHz; }
            else                    { entry.HasVar2 = true; entry.Var2Lo = loHz; entry.Var2Hi = hiHz; }
            // Two stores can race the find-then-insert against the same shared
            // zeus-prefs.db (xUnit boots WebApplicationFactory in parallel and
            // every host builds its own FilterPresetStore singleton). The
            // unique ModeKey index is what keeps the row count correct; if a
            // racer beat us in, that's exactly the seeded state we wanted.
            try { _entries.Insert(entry); }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INDEX_DUPLICATE_KEY) { }
        }
        else if (slotName == "VAR1" && !existing.HasVar1)
        {
            existing.HasVar1 = true;
            existing.Var1Lo = loHz;
            existing.Var1Hi = hiHz;
            _entries.Update(existing);
        }
        else if (slotName == "VAR2" && !existing.HasVar2)
        {
            existing.HasVar2 = true;
            existing.Var2Lo = loHz;
            existing.Var2Hi = hiHz;
            _entries.Update(existing);
        }
    }

}

public sealed class FilterPresetStoreEntry
{
    public int Id { get; set; }
    public string ModeKey { get; set; } = string.Empty;
    public int Var1Lo { get; set; }
    public int Var1Hi { get; set; }
    public bool HasVar1 { get; set; }
    public int Var2Lo { get; set; }
    public int Var2Hi { get; set; }
    public bool HasVar2 { get; set; }
    public string? LastPreset { get; set; }
    // Ribbon-scope flag, only meaningful on the sentinel "__SETTINGS__" row.
    public bool AdvancedPaneOpen { get; set; }
    // Favorite filter slots (up to 3), stored as comma-separated preset names.
    // e.g., "F6,F5,F4" for 2.7k, 2.9k, 3.3k in USB/LSB.
    public string? FavoriteSlots { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
