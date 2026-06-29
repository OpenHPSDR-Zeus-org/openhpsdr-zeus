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

namespace Zeus.Server;

/// <summary>Persisted operator settings for the KiwiSDR slice receiver — the
/// remote SDR URL, an optional password, and the enable flag. Lives in
/// zeus-prefs.db (single global row) alongside the other non-sensitive
/// preferences. The password is stored in clear text exactly as the QRZ / TCI
/// stores keep their secrets in this DB; the API never returns it to clients
/// (only a <c>HasPassword</c> bool).</summary>
public sealed class KiwiSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<KiwiSettingsEntry> _entries;
    private readonly ILogger<KiwiSettingsStore> _log;
    private readonly object _sync = new();

    /// <summary>Raised after a successful <see cref="Set"/>.</summary>
    public event Action? Changed;

    public KiwiSettingsStore(ILogger<KiwiSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<KiwiSettingsEntry>("kiwi_settings");
        _log.LogInformation("KiwiSettingsStore initialized at {Path}", dbPath);
    }

    public KiwiSettings Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            return e is null
                ? new KiwiSettings(false, null, null)
                : new KiwiSettings(e.Enabled, e.Url, e.Password);
        }
    }

    /// <summary>Patch the stored settings. Null fields are left unchanged; an
    /// empty <paramref name="password"/> string clears the stored password.</summary>
    public KiwiSettings Set(bool? enabled = null, string? url = null, string? password = null)
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault() ?? new KiwiSettingsEntry { Id = 1 };
            if (enabled.HasValue) e.Enabled = enabled.Value;
            if (url is not null) e.Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
            if (password is not null) e.Password = password.Length == 0 ? null : password;
            _entries.Upsert(e);
            _log.LogInformation("kiwi.settings.set enabled={Enabled} url={Url} hasPw={HasPw}",
                e.Enabled, e.Url, e.Password is not null);
            Changed?.Invoke();
            return new KiwiSettings(e.Enabled, e.Url, e.Password);
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

/// <summary>Immutable snapshot of the persisted Kiwi settings.</summary>
public sealed record KiwiSettings(bool Enabled, string? Url, string? Password);

/// <summary>LiteDB row for <see cref="KiwiSettingsStore"/>.</summary>
public sealed class KiwiSettingsEntry
{
    [BsonId] public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string? Password { get; set; }
}
