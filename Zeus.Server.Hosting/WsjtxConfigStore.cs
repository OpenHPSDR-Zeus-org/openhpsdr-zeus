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

// Single-row collection for the WSJT-X logged-QSO broadcaster config. Mirrors
// CatConfigStore — shares the single LiteDatabase via SharedLiteDatabase.Acquire
// and Directory guard (one engine per prefs file; avoids the Windows
// exclusive-lock IOException a second handle hits, and the Linux LiteDB
// shared-mode caveat, GH #682).
public sealed class WsjtxConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<WsjtxConfigEntry> _entries;
    private readonly ILogger<WsjtxConfigStore> _log;
    private readonly object _sync = new();

    public WsjtxConfigStore(ILogger<WsjtxConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<WsjtxConfigEntry>("wsjtx_config");

        _log.LogInformation("WsjtxConfigStore initialized at {Path}", dbPath);
    }

    // null = nothing persisted yet — caller falls back to defaults.
    public WsjtxRuntimeConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new WsjtxRuntimeConfig(
                Enabled: e.Enabled,
                Host: e.Host,
                Port: e.Port,
                InstanceId: string.IsNullOrWhiteSpace(e.InstanceId) ? "WSJT-X" : e.InstanceId,
                // LiteDB returns the type default for columns absent from older rows
                // (Transport="" / Group="" / Ttl=0) — coalesce to the documented
                // defaults so a pre-multicast row reads back as plain unicast.
                Transport: string.IsNullOrWhiteSpace(e.Transport) ? "unicast" : e.Transport,
                MulticastGroup: string.IsNullOrWhiteSpace(e.MulticastGroup) ? "224.0.0.73" : e.MulticastGroup,
                MulticastTtl: e.MulticastTtl <= 0 ? 1 : e.MulticastTtl,
                SendQsoLogged: e.SendQsoLogged,
                SendLiveDecodes: e.SendLiveDecodes);
        }
    }

    public void Set(WsjtxRuntimeConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _entries.Insert(new WsjtxConfigEntry
                {
                    Enabled = config.Enabled,
                    Host = config.Host,
                    Port = config.Port,
                    InstanceId = config.InstanceId,
                    Transport = config.Transport,
                    MulticastGroup = config.MulticastGroup,
                    MulticastTtl = config.MulticastTtl,
                    SendQsoLogged = config.SendQsoLogged,
                    SendLiveDecodes = config.SendLiveDecodes,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Enabled = config.Enabled;
                existing.Host = config.Host;
                existing.Port = config.Port;
                existing.InstanceId = config.InstanceId;
                existing.Transport = config.Transport;
                existing.MulticastGroup = config.MulticastGroup;
                existing.MulticastTtl = config.MulticastTtl;
                existing.SendQsoLogged = config.SendQsoLogged;
                existing.SendLiveDecodes = config.SendLiveDecodes;
                existing.UpdatedUtc = DateTime.UtcNow;
                _entries.Update(existing);
            }
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class WsjtxConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2237;
    public string InstanceId { get; set; } = "WSJT-X";
    public string Transport { get; set; } = "unicast";
    public string MulticastGroup { get; set; } = "224.0.0.73";
    public int MulticastTtl { get; set; } = 1;
    public bool SendQsoLogged { get; set; }
    public bool SendLiveDecodes { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
