// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// Persisted config for the four serial CAT ports (Thetis CAT1–4 parity).
// Single-row LiteDB collection ("cat_serial_config") sharing zeus-prefs.db,
// mirroring G2PanelSettingsStore: a Changed event lets CatSerialService
// re-resolve and reconnect without a server restart, and Insert-then-Update
// (NOT Upsert with Id=0) avoids the LiteDB Id=0-always-inserts bug (PR #387).
//
// Serial CAT is host-specific (a COM port / device path is meaningful only on
// the machine it is wired to), so — like the G2 panel — it is store-only with
// NO appsettings section: you would never bake a COM port into config.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class CatSerialConfigStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<CatSerialConfigEntry> _rows;
    private readonly ILogger<CatSerialConfigStore> _log;
    private readonly object _sync = new();

    // Fired on any write so CatSerialService re-resolves + reconnects its ports.
    public event Action? Changed;

    public CatSerialConfigStore(ILogger<CatSerialConfigStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _rows = _db.GetCollection<CatSerialConfigEntry>("cat_serial_config");

        _log.LogInformation("CatSerialConfigStore initialized at {Path}", dbPath);
    }

    /// <summary>The four port configs, always exactly
    /// <see cref="CatSerialDefaults.PortCount"/> entries. A missing/short/long
    /// stored row is normalised to that count (pad with disabled defaults,
    /// truncate extras), so callers never index out of range.</summary>
    public IReadOnlyList<CatSerialPortConfig> Get()
    {
        lock (_sync)
        {
            var entry = _rows.FindAll().FirstOrDefault();
            var stored = entry?.Ports ?? new List<CatSerialPortEntry>();
            // Sanitize on READ as well as write: a hand-edited or legacy DB row
            // must never feed an empty/invalid Parity/baud downstream (e.g. the
            // CatSerialService status log indexes Parity[..1]).
            return Normalize(stored.Select(e => Sanitize(ToConfig(e))));
        }
    }

    /// <summary>Replace all stored port configs. The incoming list is normalised
    /// to exactly <see cref="CatSerialDefaults.PortCount"/> ports before saving,
    /// and each port's fields are sanitised. Fires <see cref="Changed"/>.</summary>
    public void Set(IEnumerable<CatSerialPortConfig> ports)
    {
        var normalized = Normalize(ports.Select(Sanitize));
        lock (_sync)
        {
            var existing = _rows.FindAll().FirstOrDefault();
            var nowUtc = DateTime.UtcNow;
            var rows = normalized.Select(ToEntry).ToList();
            if (existing is null)
            {
                _rows.Insert(new CatSerialConfigEntry { Ports = rows, UpdatedUtc = nowUtc });
            }
            else
            {
                existing.Ports = rows;
                existing.UpdatedUtc = nowUtc;
                _rows.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    // Pad/truncate to exactly PortCount. Each port keeps its own settings — in
    // particular its own baud (Thetis has a real bug where CAT4 reports CAT1's
    // baud; we deliberately give every slot an independent round-trip).
    private static IReadOnlyList<CatSerialPortConfig> Normalize(IEnumerable<CatSerialPortConfig> ports)
    {
        var list = ports.Take(CatSerialDefaults.PortCount).ToList();
        while (list.Count < CatSerialDefaults.PortCount)
            list.Add(new CatSerialPortConfig());
        return list;
    }

    // Clamp/whitelist user-supplied values so a bad PUT can't persist a config
    // that throws on SerialPort construction.
    private static CatSerialPortConfig Sanitize(CatSerialPortConfig c)
    {
        int baud = CatSerialDefaults.BaudRates.Contains(c.BaudRate)
            ? c.BaudRate : CatSerialDefaults.BaudRate;
        int data = c.DataBits is 8 or 7 or 6 ? c.DataBits : 8;
        string parity = NormalizeParity(c.Parity);
        string stop = NormalizeStopBits(c.StopBits);
        return c with
        {
            PortName = (c.PortName ?? string.Empty).Trim(),
            BaudRate = baud,
            DataBits = data,
            Parity = parity,
            StopBits = stop,
        };
    }

    private static string NormalizeParity(string? p) =>
        p is "None" or "Odd" or "Even" or "Mark" or "Space" ? p : "None";

    private static string NormalizeStopBits(string? s) =>
        s is "One" or "OnePointFive" or "Two" ? s : "One";

    private static CatSerialPortConfig ToConfig(CatSerialPortEntry e) => new(
        Enabled: e.Enabled,
        PortName: e.PortName ?? string.Empty,
        BaudRate: e.BaudRate,
        Parity: e.Parity ?? "None",
        DataBits: e.DataBits,
        StopBits: e.StopBits ?? "One");

    private static CatSerialPortEntry ToEntry(CatSerialPortConfig c) => new()
    {
        Enabled = c.Enabled,
        PortName = c.PortName,
        BaudRate = c.BaudRate,
        Parity = c.Parity,
        DataBits = c.DataBits,
        StopBits = c.StopBits,
    };

    public void Dispose() => _dbLease.Dispose();
}

public sealed class CatSerialConfigEntry
{
    public int Id { get; set; }
    public List<CatSerialPortEntry> Ports { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}

public sealed class CatSerialPortEntry
{
    public bool Enabled { get; set; }
    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = CatSerialDefaults.BaudRate;
    public string Parity { get; set; } = "None";
    public int DataBits { get; set; } = 8;
    public string StopBits { get; set; } = "One";
}
