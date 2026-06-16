// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Global (per-install, NOT per-band/per-radio) hardware-PTT-IN enable gate
// (external-ports plan §4). When OFF, ExternalPttService still drives the
// per-protocol PTT-IN status lamp (so the operator sees the footswitch press)
// but does NOT promote the hardware edge to MOX — keying is UI-only. Defaults
// ON so a fresh install keeps the footswitch live, matching prior behaviour.
//
// Mirrors Hl2GpioSettingsStore's single-row LiteDB pattern. Upsert uses
// DeleteMany+Insert rather than Update/Upsert to dodge the LiteDB
// `Id=0`-always-inserts bug (PR #387). A missing/legacy row hydrates Enabled to
// its default — handled in Get() (LiteDB defaults a missing bool to false, so
// we store the row eagerly on first Set and treat "no row" as ON).

using LiteDB;

namespace Zeus.Server;

public sealed class PttSettingsStore : IDisposable
{
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

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _rows = _db.GetCollection<PttSettingsEntry>("ptt_settings");

        _log.LogInformation("PttSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Whether hardware PTT-IN is promoted to MOX. Missing row (fresh
    /// install / pre-feature DB) defaults to OFF — hardware PTT-IN keying is
    /// opt-in; the operator enables the footswitch gate in Radio Settings.</summary>
    public bool Get()
    {
        lock (_sync)
        {
            var e = _rows.FindAll().FirstOrDefault();
            return e is null ? false : e.Enabled;
        }
    }

    /// <summary>Replace the global enable gate. DeleteMany+Insert avoids the
    /// LiteDB Id=0 upsert bug.</summary>
    public void Set(bool enabled)
    {
        lock (_sync)
        {
            _rows.DeleteMany(_ => true);
            _rows.Insert(new PttSettingsEntry { Enabled = enabled, UpdatedUtc = DateTime.UtcNow });
        }
        Changed?.Invoke();
    }

    public void Dispose() => _db.Dispose();
}

public sealed class PttSettingsEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
