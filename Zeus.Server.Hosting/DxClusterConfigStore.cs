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

// DX-cluster client config persistence. Single-row collection in zeus-prefs.db
// — one cluster connection per Zeus instance, so a profile key would just be
// ceremony. Mirrors TciConfigStore / CatConfigStore exactly: the SAME shared
// LiteDB lease (Zeus.Data.SharedLiteDatabase.Acquire) so we never open a second
// LiteDatabase handle (the Windows-only exclusive-lock IOException, GH #682).
//
// Password is stored at-rest as plaintext, consistent with the existing
// CredentialStore (QRZ) precedent in the same DB — see the PR notes.
public sealed class DxClusterConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DxClusterConfigEntry> _entries;
    private readonly ILogger<DxClusterConfigStore> _log;
    private readonly object _sync = new();

    public DxClusterConfigStore(ILogger<DxClusterConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<DxClusterConfigEntry>("dxcluster_config");

        _log.LogInformation("DxClusterConfigStore initialized at {Path}", dbPath);
    }

    // null = nothing persisted yet — caller falls back to the default (all-off) config.
    public DxClusterConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new DxClusterConfig(
                Enabled: e.Enabled,
                Host: e.Host ?? "",
                Port: e.Port,
                Callsign: e.Callsign ?? "",
                Password: e.Password ?? "",
                LoginCommands: e.LoginCommands ?? "",
                AutoConnect: e.AutoConnect);
        }
    }

    public void Set(DxClusterConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new DxClusterConfigEntry
                {
                    Enabled = config.Enabled,
                    Host = config.Host,
                    Port = config.Port,
                    Callsign = config.Callsign,
                    Password = config.Password,
                    LoginCommands = config.LoginCommands,
                    AutoConnect = config.AutoConnect,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Enabled = config.Enabled;
                existing.Host = config.Host;
                existing.Port = config.Port;
                existing.Callsign = config.Callsign;
                existing.Password = config.Password;
                existing.LoginCommands = config.LoginCommands;
                existing.AutoConnect = config.AutoConnect;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class DxClusterConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? Host { get; set; } = "";
    public int Port { get; set; } = 7373;
    public string? Callsign { get; set; } = "";
    public string? Password { get; set; } = "";
    public string? LoginCommands { get; set; } = "";
    public bool AutoConnect { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
