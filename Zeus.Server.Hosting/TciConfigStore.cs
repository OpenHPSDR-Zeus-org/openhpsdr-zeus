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
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// TCI runtime config persistence. Holds the operator's pending Enabled/Bind/Port
// selection so it survives restarts — TCI shares Kestrel's listener, which
// can only be configured at host build, so changes are queued here and
// re-read at next startup. Lives in zeus-prefs.db alongside the other
// non-sensitive preference stores.
//
// Single-row collection: there is one TCI server per Zeus instance, so a
// profile key would just be ceremony. Everything else (PaSettings, PsSettings)
// uses Upsert keyed by ProfileId; here the row identity is implicit.
public sealed class TciConfigStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<TciConfigEntry> _entries;
    private readonly ILogger<TciConfigStore> _log;
    private readonly object _sync = new();

    public TciConfigStore(ILogger<TciConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<TciConfigEntry>("tci_config");

        _log.LogInformation("TciConfigStore initialized at {Path}", dbPath);
    }

    // null = nothing persisted yet — caller should fall back to appsettings.
    public TciRuntimeConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new TciRuntimeConfig(
                Enabled: e.Enabled,
                BindAddress: e.BindAddress,
                Port: e.Port);
        }
    }

    public void Set(TciRuntimeConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new TciConfigEntry
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

    public void Dispose() => _db.Dispose();

}

public sealed class TciConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 40001;
    public DateTime UpdatedUtc { get; set; }
}
