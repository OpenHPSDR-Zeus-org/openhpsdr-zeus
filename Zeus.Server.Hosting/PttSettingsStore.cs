// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Global (per-install, NOT per-band/per-radio) hardware-PTT-IN → MOX enable
// gate. When OFF, ExternalPttService still drives the per-protocol PTT-IN
// status lamp (so the operator sees the footswitch press) but does NOT promote
// the hardware edge to MOX — keying stays UI-only.
//
// Defaults ON (Thetis-faithful): a fresh install / pre-feature DB keys MOX from
// the footswitch out of the box, exactly like Thetis. Arming the gate never
// auto-keys MOX on its own — actual TX still requires a physical footswitch
// edge (edge-triggered in ExternalPttService), so default-ON is safe: the radio
// only transmits when the operator presses the pedal. An operator who wants
// UI-only keying turns the gate OFF in Radio Settings; that choice persists.
//
// Single-row LiteDB collection ("ptt_settings") sharing zeus-prefs.db, mirroring
// ChatEnabledStore. Insert/Update (NOT Upsert with Id=0) avoids the LiteDB
// Id=0-always-inserts bug (PR #387).

using LiteDB;

namespace Zeus.Server;

public sealed class PttSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PttSettingsEntry> _rows;
    private readonly ILogger<PttSettingsStore> _log;
    private readonly object _sync = new();

    // Fired on any write so the UI / status endpoint re-reads the gate.
    public event Action? Changed;

    public PttSettingsStore(ILogger<PttSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _rows = _db.GetCollection<PttSettingsEntry>("ptt_settings");

        _log.LogInformation("PttSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Whether hardware PTT-IN is promoted to MOX. A missing row (fresh
    /// install / pre-feature DB) defaults to ON — the footswitch keys MOX out of
    /// the box, like Thetis. The operator can turn the gate OFF in Radio
    /// Settings for UI-only keying; that choice is persisted.</summary>
    public bool Get()
    {
        lock (_sync)
        {
            var entry = _rows.FindAll().FirstOrDefault();
            return entry?.Enabled ?? true;
        }
    }

    /// <summary>Replace the global enable gate. Insert-then-Update (matching
    /// ChatEnabledStore) avoids the LiteDB Id=0 upsert bug (PR #387).</summary>
    public void Set(bool enabled)
    {
        lock (_sync)
        {
            var existing = _rows.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _rows.Insert(new PttSettingsEntry { Enabled = enabled, UpdatedUtc = nowUtc });
            }
            else
            {
                existing.Enabled = enabled;
                existing.UpdatedUtc = nowUtc;
                _rows.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class PttSettingsEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
