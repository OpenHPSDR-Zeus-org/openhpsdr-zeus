// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists the classic-layout bottom-row per-slot pin state (Logbook,
// TX Stage Meters). Lives in the same zeus-prefs.db as PA / band-memory
// / layout / display-settings. Server-side rather than localStorage so
// the layout choice follows the operator across desktops, phones, and
// every browser pointed at the Zeus instance.
public sealed class BottomPinStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BottomPinEntry> _docs;
    private readonly ILogger<BottomPinStore> _log;
    private readonly object _sync = new();

    public BottomPinStore(ILogger<BottomPinStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<BottomPinEntry>("bottom_pin");

        _log.LogInformation("BottomPinStore initialized at {Path}", dbPath);
    }

    public BottomPinDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            // Default = both pinned. Matches the historical layout, so a new
            // operator opens the app with the familiar full-body bottom row.
            if (e is null) return new BottomPinDto(Logbook: true, TxMeters: true);
            return new BottomPinDto(Logbook: e.Logbook, TxMeters: e.TxMeters);
        }
    }

    public void Save(bool logbook, bool txMeters)
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new BottomPinEntry();
            e.Logbook = logbook;
            e.TxMeters = txMeters;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    public void Dispose() => _db.Dispose();

}

public sealed class BottomPinEntry
{
    public int Id { get; set; }
    public bool Logbook { get; set; } = true;
    public bool TxMeters { get; set; } = true;
    public DateTime UpdatedUtc { get; set; }
}
