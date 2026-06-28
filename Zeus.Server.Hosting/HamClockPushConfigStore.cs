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

namespace Zeus.Server;

/// <summary>
/// Live config + persistence for the outbound "push worked station to HamClock"
/// feature. SEND-ONLY (HamClockService only issues an HTTP GET out); egress is
/// OFF by default. Config applies live (no listener, no restart).
///
/// Server-side DTOs only — these never cross the SignalR/StateDto wire, only the
/// /api/hamclock/push-config + /api/hamclock/dx REST endpoints and the frontend
/// store consume them as JSON (keeps a Zeus.Contracts wire change off the table).
/// </summary>
public sealed class HamClockPushManagementService
{
    private readonly ILogger<HamClockPushManagementService> _log;
    private readonly HamClockPushConfigStore _store;
    private readonly object _sync = new();
    private HamClockPushConfig _config;

    public HamClockPushManagementService(ILogger<HamClockPushManagementService> log, HamClockPushConfigStore store)
    {
        _log = log;
        _store = store;
        _config = _store.Get() ?? new HamClockPushConfig();
    }

    public HamClockPushConfig GetConfig()
    {
        lock (_sync) return _config;
    }

    public HamClockPushConfig SetConfig(HamClockPushConfig config)
    {
        var trigger = config.Trigger?.Trim().ToLowerInvariant() switch
        {
            "on-active-qso" => "on-active-QSO",
            _ => "on-click",
        };
        var target = string.Equals(config.Target?.Trim(), "bundled", StringComparison.OrdinalIgnoreCase)
            ? "bundled"
            : "external";

        var normalized = new HamClockPushConfig(
            Enabled: config.Enabled,
            Trigger: trigger,
            Target: target,
            ExternalHost: (config.ExternalHost ?? "").Trim(),
            ExternalPort: config.ExternalPort is > 0 and < 65536 ? config.ExternalPort : 8080);

        lock (_sync) _config = normalized;
        try { _store.Set(normalized); }
        catch (Exception ex) { _log.LogWarning(ex, "hamclock.push.config.persist failed"); }

        _log.LogInformation(
            "hamclock.push.config.updated enabled={En} trigger={Tr} target={Tg} host={Host} port={Port}",
            normalized.Enabled, normalized.Trigger, normalized.Target, normalized.ExternalHost, normalized.ExternalPort);
        return normalized;
    }
}

// POST body for /api/hamclock/dx. Grid is the worked station's Maidenhead
// locator (required for a push — classic HamClock sets DX by grid, not call).
// Call is accepted for logging context only.
public sealed record HamClockDxRequest(string? Grid = null, string? Call = null);

public sealed record HamClockPushConfig(
    bool Enabled = false,
    string Trigger = "on-click",        // "on-click" | "on-active-QSO"
    string Target = "external",         // "external" | "bundled" (bundled unsupported today)
    string ExternalHost = "",
    int ExternalPort = 8080);           // classic HamClock REST default port

// Single-row LiteDB collection. Shares the single LiteDatabase via
// SharedLiteDatabase.Acquire + Directory guard (mirrors CatConfigStore; one engine
// per prefs file avoids the Windows exclusive-lock IOException a second handle
// hits, and the Linux LiteDB shared-mode caveat, GH #682).
public sealed class HamClockPushConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<HamClockPushConfigEntry> _entries;
    private readonly object _sync = new();

    public HamClockPushConfigStore(ILogger<HamClockPushConfigStore> log, string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _entries = _db.GetCollection<HamClockPushConfigEntry>("hamclock_push_config");
        log.LogInformation("HamClockPushConfigStore initialized at {Path}", dbPath);
    }

    public HamClockPushConfig? Get()
    {
        lock (_sync)
        {
            var e = _entries.FindAll().FirstOrDefault();
            if (e is null) return null;
            return new HamClockPushConfig(
                Enabled: e.Enabled,
                Trigger: string.IsNullOrWhiteSpace(e.Trigger) ? "on-click" : e.Trigger,
                Target: string.IsNullOrWhiteSpace(e.Target) ? "external" : e.Target,
                ExternalHost: e.ExternalHost ?? "",
                ExternalPort: e.ExternalPort is > 0 and < 65536 ? e.ExternalPort : 8080);
        }
    }

    public void Set(HamClockPushConfig config)
    {
        lock (_sync)
        {
            var existing = _entries.FindAll().FirstOrDefault() ?? new HamClockPushConfigEntry();
            existing.Enabled = config.Enabled;
            existing.Trigger = config.Trigger;
            existing.Target = config.Target;
            existing.ExternalHost = config.ExternalHost;
            existing.ExternalPort = config.ExternalPort;
            existing.UpdatedUtc = DateTime.UtcNow;
            if (existing.Id == 0) _entries.Insert(existing);
            else _entries.Update(existing);
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class HamClockPushConfigEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string Trigger { get; set; } = "on-click";
    public string Target { get; set; } = "external";
    public string? ExternalHost { get; set; }
    public int ExternalPort { get; set; } = 8080;
    public DateTime UpdatedUtc { get; set; }
}
