// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// OperatorIdentityStore — persists the single shared station callsign + grid
// (OperatorIdentity) in a single-row LiteDB collection sharing zeus-prefs.db,
// mirroring SpottingSettingsStore / FreeDvReporterSettingsStore. First run (no
// row) returns the empty default, which means "no override — fall back to QRZ".
// This is the one place operator identity is stored; the spotting / FreeDV /
// FT8-TX resolvers all read it first (see OperatorIdentityResolver).

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Reads/writes the shared <see cref="OperatorIdentity"/> override row. Thread-safe;
/// values are normalized on write (callsign upper-cased, grid Maidenhead-shaped).
/// </summary>
public sealed class OperatorIdentityStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<OperatorIdentityEntry> _entries;
    private readonly ILogger<OperatorIdentityStore> _log;
    private readonly object _sync = new();

    public OperatorIdentityStore(ILogger<OperatorIdentityStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<OperatorIdentityEntry>("operator_identity");

        _log.LogInformation("OperatorIdentityStore initialized at {Path}", dbPath);
    }

    /// <summary>The saved override (normalized), or the empty default if none.</summary>
    public OperatorIdentity Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return new OperatorIdentity();
            return new OperatorIdentity(
                Callsign: e.Callsign ?? "",
                Grid: e.Grid ?? "").Normalized();
        }
    }

    /// <summary>Persist the override (normalized) and return what was stored.</summary>
    public OperatorIdentity Set(OperatorIdentity identity)
    {
        var id = identity.Normalized();
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _entries.Insert(new OperatorIdentityEntry
                {
                    Callsign = id.Callsign,
                    Grid = id.Grid,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.Callsign = id.Callsign;
                existing.Grid = id.Grid;
                existing.UpdatedUtc = nowUtc;
                _entries.Update(existing);
            }
        }
        return id;
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class OperatorIdentityEntry
{
    public int Id { get; set; }
    public string? Callsign { get; set; } = "";
    public string? Grid { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}
