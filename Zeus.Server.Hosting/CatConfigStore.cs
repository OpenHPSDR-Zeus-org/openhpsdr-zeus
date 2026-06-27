// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Single-row collection: there is one CAT server per Zeus instance, so a
// profile key would just be ceremony. Mirrors TciConfigStore — same
// Connection=shared pattern and Directory guard (no new exposure to the
// Linux LiteDB shared-mode caveat, GH #682).
public sealed class CatConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<CatConfigEntry> _entries;
    private readonly ILogger<CatConfigStore> _log;
    private readonly object _sync = new();

    public CatConfigStore(ILogger<CatConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<CatConfigEntry>("cat_config");

        _log.LogInformation("CatConfigStore initialized at {Path}", dbPath);
    }

    // null = nothing persisted yet — caller should fall back to appsettings.
    public CatRuntimeConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new CatRuntimeConfig(
                Enabled: e.Enabled,
                BindAddress: e.BindAddress,
                Port: e.Port);
        }
    }

    public void Set(CatRuntimeConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new CatConfigEntry
                {
                    Enabled = config.Enabled,
                    BindAddress = config.BindAddress,
                    Port = config.Port,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Enabled = config.Enabled;
                existing.BindAddress = config.BindAddress;
                existing.Port = config.Port;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class CatConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 19090;
    public DateTime UpdatedUtc { get; set; }
}
