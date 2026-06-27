// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Support;

/// <summary>
/// The operator's L1 "Remote Diagnostics availability" master switch — the
/// single gate that decides whether a maintainer can even REQUEST a read-only
/// support session. Persisted across restarts in zeus-prefs.db (a single-row
/// "support_availability" collection, mirroring <see cref="ChatEnabledStore"/>).
///
/// <para>OFF by default and on first run (no row yet): a brand-new operator is
/// never reachable for support until they explicitly opt in. While OFF,
/// <see cref="SupportRequestCoordinator.RegisterRequest"/> refuses inbound
/// requests outright — no prompt is ever shown.</para>
///
/// <para><see cref="AutoShareOnCrash"/> is an independent sub-toggle (also OFF
/// by default) that pre-authorises the out-of-process sidecar to attach a crash
/// report when the backend dies. It does NOT grant live sessions; the L1 switch
/// still gates those.</para>
///
/// Thread-safe; registered as a singleton.
/// </summary>
public sealed class SupportAvailabilityStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<SupportAvailabilityEntry> _state;
    private readonly ILogger<SupportAvailabilityStore> _log;
    private readonly object _sync = new();

    public SupportAvailabilityStore(ILogger<SupportAvailabilityStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _state = _db.GetCollection<SupportAvailabilityEntry>("support_availability");

        _log.LogInformation("SupportAvailabilityStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// True only when the operator has opted in to being reachable for remote
    /// diagnostics. Defaults to false on first run (no row yet). This is the
    /// master gate the coordinator checks before surfacing any request.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                var entry = _state.FindAll().FirstOrDefault();
                return entry?.Available ?? false;
            }
        }
    }

    /// <summary>
    /// Whether the operator has pre-authorised the sidecar to attach a crash
    /// report on an unexpected backend exit. Defaults to false. Independent of
    /// <see cref="IsAvailable"/>.
    /// </summary>
    public bool AutoShareOnCrash
    {
        get
        {
            lock (_sync)
            {
                var entry = _state.FindAll().FirstOrDefault();
                return entry?.AutoShareCrashes ?? false;
            }
        }
    }

    /// <summary>Persist both opt-in flags atomically and return the new state.</summary>
    public (bool Available, bool AutoShareCrashes) Set(bool available, bool autoShareCrashes)
    {
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            if (existing is null)
            {
                _state.Insert(new SupportAvailabilityEntry
                {
                    Available = available,
                    AutoShareCrashes = autoShareCrashes,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.Available = available;
                existing.AutoShareCrashes = autoShareCrashes;
                existing.UpdatedUtc = nowUtc;
                _state.Update(existing);
            }
        }

        _log.LogInformation(
            "support.availability set available={Available} autoShareCrashes={AutoShare}",
            available, autoShareCrashes);
        return (available, autoShareCrashes);
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class SupportAvailabilityEntry
{
    public int Id { get; set; }
    public bool Available { get; set; }
    public bool AutoShareCrashes { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
