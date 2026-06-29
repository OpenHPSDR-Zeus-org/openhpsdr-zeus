// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Text.Json;
using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server.Midi;

/// <summary>
/// Single-row LiteDB store for the MIDI subsystem: the enable flag plus the
/// controller binding document. Mirrors <see cref="CatConfigStore"/> exactly —
/// same <c>Connection=shared</c> lease via <see cref="Zeus.Data.SharedLiteDatabase"/>
/// and the same Directory guard, so it adds no new exposure to the Linux
/// shared-mode caveat (GH #682). The binding document is stored as a single
/// System.Text.Json blob rather than mapped field-by-field — the DTOs are
/// immutable positional records, so a JSON column round-trips losslessly and
/// keeps LiteDB's constructor mapping out of the picture.
/// </summary>
public sealed class MidiConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<MidiConfigEntry> _entries;
    private readonly ILogger<MidiConfigStore> _log;
    private readonly object _sync = new();

    public MidiConfigStore(ILogger<MidiConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<MidiConfigEntry>("midi_config");

        _log.LogInformation("MidiConfigStore initialized at {Path}", dbPath);
    }

    /// <summary>The persisted enable flag (false until the operator turns MIDI
    /// on). MIDI is purely an input path, so there is no transmit-safety reason
    /// to force-disable on startup — it persists like the CAT/TCI enable.</summary>
    public bool GetEnabled()
    {
        lock (_sync)
            return _entries.FindAll().FirstOrDefault()?.Enabled ?? false;
    }

    /// <summary>The persisted binding document, or <see cref="MidiBindingsDoc.Empty"/>
    /// if nothing has been saved yet (or a stored blob fails to parse).</summary>
    public MidiBindingsDoc GetBindings()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null || string.IsNullOrWhiteSpace(e.BindingsJson))
                return MidiBindingsDoc.Empty;
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<MidiBindingsDoc>(e.BindingsJson) ?? MidiBindingsDoc.Empty;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "midi.bindings.parse failed; falling back to empty");
                return MidiBindingsDoc.Empty;
            }
        }
    }

    public void Set(bool enabled, MidiBindingsDoc bindings)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(bindings);
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new MidiConfigEntry
                {
                    Enabled = enabled,
                    BindingsJson = json,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Enabled = enabled;
                existing.BindingsJson = json;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class MidiConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string BindingsJson { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}
