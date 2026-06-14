// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// HL2 user GPIO (external-ports plan, Phase 5): the 4-bit user_dig_out mask
// emitted on the Protocol-1 register 0x0a (wire 0x14) frame C3[3:0] → MCP23008
// (verified Thetis-mi0bot networkproto1.c:774). Global per-radio (NOT per-band)
// — it is a static front-panel control-line state, like the audio front-end.
//
// Mirrors AudioSettingsStore's single-global-row LiteDB pattern, including the
// DeleteMany+Insert upsert that dodges the LiteDB Id=0-always-inserts bug
// (PR #387). HL2-only; the value is stored board-agnostically and gated at the
// REST / wire layer (HasHl2UserGpio), so it never reaches the wire on a non-HL2
// board. Default 0 (no bits set) is byte-identical to today.

using LiteDB;

namespace Zeus.Server;

public sealed class Hl2GpioSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Hl2GpioEntry> _rows;
    private readonly ILogger<Hl2GpioSettingsStore> _log;
    private readonly object _sync = new();

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

    /// <summary>The persisted 4-bit user GPIO mask. Missing row defaults to 0
    /// (all GPIO lines low) — byte-identical to today.</summary>
    public byte Get()
    {
        lock (_sync)
        {
            var e = _rows.FindAll().FirstOrDefault();
            return e is null ? (byte)0 : (byte)(e.Mask & 0x0F);
        }
    }

    /// <summary>Replace the global GPIO mask (low nibble only). DeleteMany+Insert
    /// avoids the LiteDB Id=0 upsert bug.</summary>
    public void Set(byte mask)
    {
        lock (_sync)
        {
            _rows.DeleteMany(_ => true);
            _rows.Insert(new Hl2GpioEntry { Mask = (byte)(mask & 0x0F), UpdatedUtc = DateTime.UtcNow });
        }
        Changed?.Invoke();
    }

    public void Dispose() => _db.Dispose();
}

public sealed class Hl2GpioEntry
{
    public int Id { get; set; }
    // 4-bit user_dig_out mask. LiteDB is schema-less; a missing/legacy row
    // hydrates as 0 = all lines low, the correct byte-identical default.
    public byte Mask { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
