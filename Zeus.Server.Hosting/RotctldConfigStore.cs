// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists the rotctld host / port / enabled flag server-side. Until this
// existed, the rotator config was held only in each frontend's localStorage
// and POSTed to the backend at page-load — which meant a fresh client
// (e.g. a phone visiting Zeus for the first time, or a session opened on a
// device that had never enabled the rotator) would see the backend in its
// default disabled state, even though the operator had configured the
// rotator on a different device. Now the backend itself owns the
// authoritative config; clients just read it via /api/rotator/status.
//
// Persistence layer matches the other prefs stores (PaSettings, DspSettings,
// PreferredRadio): LiteDB collection living in zeus-prefs.db under the
// platform default path.
public sealed class RotctldConfigStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Entry> _entries;
    private readonly ILogger<RotctldConfigStore> _log;
    private readonly object _sync = new();

    public RotctldConfigStore(ILogger<RotctldConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<Entry>("rotctld_config");
        _entries.EnsureIndex(e => e.Id, unique: true);
        _log.LogInformation("RotctldConfigStore initialized at {DbPath}", dbPath);
    }

    public RotctldConfig Get()
    {
        lock (_sync)
        {
            var e = _entries.FindById(SingletonId);
            if (e == null) return new RotctldConfig();
            return new RotctldConfig(
                Enabled: e.Enabled,
                Host: string.IsNullOrWhiteSpace(e.Host) ? "127.0.0.1" : e.Host,
                Port: e.Port is > 0 and < 65536 ? e.Port : 4533,
                PollingIntervalMs: Math.Clamp(e.PollingIntervalMs, 100, 10_000));
        }
    }

    public void Set(RotctldConfig cfg)
    {
        lock (_sync)
        {
            _entries.Upsert(new Entry
            {
                Id = SingletonId,
                Enabled = cfg.Enabled,
                Host = cfg.Host,
                Port = cfg.Port,
                PollingIntervalMs = cfg.PollingIntervalMs,
            });
        }
    }

    public void Dispose() => _db.Dispose();

    private const int SingletonId = 1;

    // Internal storage shape. Lives in the rotctld_config collection.
    private sealed class Entry
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 4533;
        public int PollingIntervalMs { get; set; } = 500;
    }
}
