// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using LiteDB;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Wav;

/// <summary>
/// Persists the single operator preference that the tape deck needs across
/// restarts: the recordings root directory. Null/absent = "use the platform
/// default" (<see cref="WavLibrary.DefaultRoot"/>).
///
/// Deliberately the smallest possible LiteDB store — one tiny single-row
/// document, no concurrent writers — mirroring <see cref="PreferredRadioStore"/>
/// exactly so it stays clear of the Linux LiteDB shared-mode crash (issue #682)
/// that bit a heavier store. Lives in <c>zeus-prefs.db</c> alongside the other
/// non-sensitive preferences.
/// </summary>
public sealed class WavRecorderSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<WavRecorderSettingsEntry> _entries;
    private readonly ILogger<WavRecorderSettingsStore> _log;
    private readonly object _sync = new();

    public WavRecorderSettingsStore(ILogger<WavRecorderSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<WavRecorderSettingsEntry>("wav_recorder_settings");

        _log.LogInformation("WavRecorderSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>The persisted recordings root, or null if the operator has not
    /// chosen one (use the platform default). Empty/whitespace stored values are
    /// treated as null.</summary>
    public string? GetRoot()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            return string.IsNullOrWhiteSpace(e?.RecordingsRoot) ? null : e!.RecordingsRoot;
        }
    }

    /// <summary>Persist the chosen recordings root. Null/empty clears it back to
    /// "use the platform default".</summary>
    public void SetRoot(string? absPath)
    {
        string? value = string.IsNullOrWhiteSpace(absPath) ? null : absPath;
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new WavRecorderSettingsEntry
                {
                    RecordingsRoot = value,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.RecordingsRoot = value;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class WavRecorderSettingsEntry
{
    public int Id { get; set; }
    /// <summary>Absolute path to the operator-chosen recordings root, or null
    /// for "use the platform default". LiteDB hydrates as null for older rows
    /// that pre-date this store.</summary>
    public string? RecordingsRoot { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
