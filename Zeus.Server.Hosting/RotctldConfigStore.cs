// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists the multi-rotator config (issue #917) server-side. Until the
// multi-slot feature landed, this store held a single host/port/enabled
// triplet — that legacy schema is migrated to a one-slot RotctldMultiConfig
// on first read.
//
// Persistence layer matches the other prefs stores (PaSettings, DspSettings,
// PreferredRadio): a LiteDB collection in zeus-prefs.db.
public sealed class RotctldConfigStore : IDisposable
{
    public const int MaxSlots = 4;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<MultiEntry> _multi;
    private readonly ILiteCollection<LegacyEntry> _legacy;
    private readonly ILogger<RotctldConfigStore> _log;
    private readonly object _sync = new();

    public RotctldConfigStore(ILogger<RotctldConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _multi = _db.GetCollection<MultiEntry>("rotctld_multi_config");
        _multi.EnsureIndex(e => e.Id, unique: true);
        _legacy = _db.GetCollection<LegacyEntry>("rotctld_config");
        _legacy.EnsureIndex(e => e.Id, unique: true);
        _log.LogInformation("RotctldConfigStore initialized at {DbPath}", dbPath);
    }

    public RotctldMultiConfig Get()
    {
        lock (_sync)
        {
            var multi = _multi.FindById(SingletonId);
            // Read-repair: a hand-corrupted or rolled-back DB (duplicate slot
            // ids, >MaxSlots slots, active id pointing at a removed slot) must
            // never break the app. FromEntry only coerces per-field ranges;
            // Sanitize additionally dedups ids and caps the count. Idempotent.
            if (multi != null) return RotctldService.Sanitize(FromEntry(multi));

            // First load on a server upgraded from the single-slot schema:
            // promote the legacy entry to slot 1 with all HF bands assigned.
            var legacy = _legacy.FindById(SingletonId);
            var promoted = legacy != null
                ? new RotctldMultiConfig(
                    ActiveSlotId: 1,
                    AutoRoute: false,
                    Slots: new[]
                    {
                        new RotctldSlot(
                            Id: 1,
                            Label: "Rotator 1",
                            Enabled: legacy.Enabled,
                            Host: string.IsNullOrWhiteSpace(legacy.Host) ? "127.0.0.1" : legacy.Host,
                            Port: legacy.Port is > 0 and < 65536 ? legacy.Port : 4533,
                            Bands: BandUtils.HfBands.ToArray(),
                            PollingIntervalMs: Math.Clamp(legacy.PollingIntervalMs, 100, 10_000)),
                    })
                : new RotctldMultiConfig(
                    ActiveSlotId: 1,
                    AutoRoute: false,
                    Slots: new[]
                    {
                        new RotctldSlot(
                            Id: 1,
                            Label: "Rotator 1",
                            Enabled: false,
                            Host: "127.0.0.1",
                            Port: 4533,
                            Bands: BandUtils.HfBands.ToArray(),
                            PollingIntervalMs: 500),
                    });
            _multi.Upsert(ToEntry(promoted));
            return promoted;
        }
    }

    public void Set(RotctldMultiConfig cfg)
    {
        lock (_sync)
        {
            _multi.Upsert(ToEntry(cfg));
        }
    }

    public void Dispose() => _dbLease.Dispose();

    private const int SingletonId = 1;

    private static MultiEntry ToEntry(RotctldMultiConfig cfg) => new()
    {
        Id = SingletonId,
        ActiveSlotId = cfg.ActiveSlotId,
        AutoRoute = cfg.AutoRoute,
        Slots = cfg.Slots.Select(s => new SlotEntry
        {
            SlotId = s.Id,
            Label = s.Label,
            Enabled = s.Enabled,
            Host = s.Host,
            Port = s.Port,
            Bands = s.Bands.ToList(),
            PollingIntervalMs = s.PollingIntervalMs,
        }).ToList(),
    };

    private static RotctldMultiConfig FromEntry(MultiEntry e) => new(
        ActiveSlotId: e.ActiveSlotId <= 0 ? 1 : e.ActiveSlotId,
        AutoRoute: e.AutoRoute,
        Slots: (e.Slots ?? new List<SlotEntry>())
            .Select(s => new RotctldSlot(
                Id: s.SlotId,
                Label: string.IsNullOrWhiteSpace(s.Label) ? $"Rotator {s.SlotId}" : s.Label,
                Enabled: s.Enabled,
                Host: string.IsNullOrWhiteSpace(s.Host) ? "127.0.0.1" : s.Host,
                Port: s.Port is > 0 and < 65536 ? s.Port : 4533,
                Bands: (s.Bands ?? new List<string>()).ToArray(),
                PollingIntervalMs: Math.Clamp(s.PollingIntervalMs <= 0 ? 500 : s.PollingIntervalMs, 100, 10_000)))
            .ToArray());

    // Storage shape. Lives in rotctld_multi_config; legacy single-slot rows in
    // rotctld_config are read once for migration and then left in place
    // (harmless) for forensic / rollback purposes.
    private sealed class MultiEntry
    {
        public int Id { get; set; }
        public int ActiveSlotId { get; set; } = 1;
        public bool AutoRoute { get; set; }
        public List<SlotEntry>? Slots { get; set; }
    }

    private sealed class SlotEntry
    {
        public int SlotId { get; set; }
        public string Label { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 4533;
        public List<string>? Bands { get; set; }
        public int PollingIntervalMs { get; set; } = 500;
    }

    private sealed class LegacyEntry
    {
        public int Id { get; set; }
        public bool Enabled { get; set; }
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 4533;
        public int PollingIntervalMs { get; set; } = 500;
    }
}
