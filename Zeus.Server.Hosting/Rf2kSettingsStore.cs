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
//
// RF2K-S amp connection settings persistence. Single-row collection in
// zeus-prefs.db (same database as the other preference stores). Mirrors
// TciConfigStore's shape — there is one amp per Zeus instance, so a profile
// key would just be ceremony.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class Rf2kSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Rf2kConfigEntry> _entries;
    private readonly ILogger<Rf2kSettingsStore> _log;
    private readonly object _sync = new();

    public Rf2kSettingsStore(ILogger<Rf2kSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<Rf2kConfigEntry>("rf2k_config");

        _log.LogInformation("Rf2kSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Persisted config or <c>null</c> if nothing has been saved yet.</summary>
    public Rf2kConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new Rf2kConfig(
                Enabled: e.Enabled,
                Host: e.Host,
                Port: e.Port,
                VncPort: e.VncPort,
                VncPassword: e.VncPassword ?? string.Empty,
                PollingIntervalMs: e.PollingIntervalMs,
                TuneClickX: e.TuneClickX,
                TuneClickY: e.TuneClickY,
                BypassClickX: e.BypassClickX,
                BypassClickY: e.BypassClickY);
        }
    }

    public void Set(Rf2kConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new Rf2kConfigEntry
                {
                    Enabled = config.Enabled,
                    Host = config.Host,
                    Port = config.Port,
                    VncPort = config.VncPort,
                    VncPassword = config.VncPassword ?? string.Empty,
                    PollingIntervalMs = config.PollingIntervalMs,
                    TuneClickX = config.TuneClickX,
                    TuneClickY = config.TuneClickY,
                    BypassClickX = config.BypassClickX,
                    BypassClickY = config.BypassClickY,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Enabled = config.Enabled;
                existing.Host = config.Host;
                existing.Port = config.Port;
                existing.VncPort = config.VncPort;
                existing.VncPassword = config.VncPassword ?? string.Empty;
                existing.PollingIntervalMs = config.PollingIntervalMs;
                existing.TuneClickX = config.TuneClickX;
                existing.TuneClickY = config.TuneClickY;
                existing.BypassClickX = config.BypassClickX;
                existing.BypassClickY = config.BypassClickY;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _db.Dispose();

}

public sealed class Rf2kConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string Host { get; set; } = "10.70.120.41";
    public int Port { get; set; } = 8080;
    public int VncPort { get; set; } = 5900;
    public string VncPassword { get; set; } = string.Empty;
    public int PollingIntervalMs { get; set; } = 500;
    public int TuneClickX { get; set; }
    public int TuneClickY { get; set; }
    public int BypassClickX { get; set; }
    public int BypassClickY { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
