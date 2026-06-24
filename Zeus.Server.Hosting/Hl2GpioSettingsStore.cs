// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// HL2 user GPIO mask (external-ports plan, Phase 5; re-ported in the external-
// port parity audit). Holds ONE global per-radio row: the 4-bit user_dig_out
// mask that lands on the Protocol-1 0x0a / wire-0x14 frame C3[3:0] → MCP23008 on
// the Hermes-Lite 2 IO connector. It is NOT per-band — the GPIO lines are a
// front-panel station state, so the model mirrors AudioSettingsStore's single
// global row rather than AntennaSettingsStore's band-keyed collection.
//
// Board-agnostic by design: the store always round-trips the low nibble, and the
// capability gate (HasHl2UserGpio — HL2 only) is enforced at the RadioService
// push and the REST layer, so a mask stored while an HL2 was connected is simply
// ignored when a non-HL2 board is in front of you (PushHl2Gpio hands the live
// client 0). Upsert uses DeleteMany+Insert to dodge the LiteDB `Id=0`-always-
// inserts bug (PR #387). Default mask 0 → C3 stays clear → byte-identical to a
// board that never drives the lines.

using LiteDB;

namespace Zeus.Server;

public sealed class Hl2GpioSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Hl2GpioEntry> _rows;
    private readonly ILogger<Hl2GpioSettingsStore> _log;
    private readonly object _sync = new();

    // Fired on any write so RadioService can re-push the GPIO mask to the live
    // client — same pattern as AudioSettingsStore.Changed.
    public event Action? Changed;

    public Hl2GpioSettingsStore(ILogger<Hl2GpioSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _rows = _db.GetCollection<Hl2GpioEntry>("hl2_gpio");

        _log.LogInformation("Hl2GpioSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// The persisted 4-bit user_dig_out mask (0..15). A missing row (fresh
    /// install / pre-feature DB) defaults to 0 — every line low, byte-identical
    /// to today's wire output on every board.
    /// </summary>
    public byte Get()
    {
        lock (_sync)
        {
            var e = _rows.FindAll().FirstOrDefault();
            return e is null ? (byte)0 : (byte)(e.Bits & 0x0F);
        }
    }

    /// <summary>
    /// Replace the global GPIO mask. Only the low nibble is stored. Uses
    /// DeleteMany+Insert so the single global row is rewritten cleanly regardless
    /// of its Id (the LiteDB Id=0-always-inserts bug, PR #387).
    /// </summary>
    public void Set(byte mask)
    {
        lock (_sync)
        {
            _rows.DeleteMany(_ => true);
            _rows.Insert(new Hl2GpioEntry
            {
                Bits = (byte)(mask & 0x0F),
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        Changed?.Invoke();
    }

    public void Dispose() => _db.Dispose();
}

public sealed class Hl2GpioEntry
{
    public int Id { get; set; }
    // The persisted 4-bit user_dig_out mask. Rows written before this feature
    // hydrate Bits = 0, the correct legacy default.
    public byte Bits { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
