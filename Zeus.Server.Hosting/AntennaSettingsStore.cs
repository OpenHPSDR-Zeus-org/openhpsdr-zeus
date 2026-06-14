// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Per-band TX/RX antenna relay selection (external-ports plan, Phase 2).
//
// Reuses the band-keyed LiteDB pattern of PaSettingsStore (per-band entry in
// the shared, unencrypted zeus-prefs.db) but keeps antenna out of the PA wire
// DTO so the red-light Zeus.Contracts PA contract is untouched. The store is
// deliberately band-only keyed — NO board/variant column. That is safe ONLY
// because the wire layer gates emission: a per-band ANT3 persisted on a G2
// rides into the same row an HL2 later reads, and HL2 is protected because its
// encoder never emits TX-antenna bits and clamps RX-antenna to ANT1
// (ControlFrame.EncodeRxAntennaC3Bits). All 0x0A variants share
// HpsdrBoardKind.OrionMkII and identical antenna wire semantics, so a band row
// round-trips across variants. Keep this wire-gate dependency in mind before
// any refactor that drops the clamp.

using LiteDB;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

public sealed class AntennaSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<AntennaBandEntry> _bands;
    private readonly ILogger<AntennaSettingsStore> _log;
    private readonly object _sync = new();

    // Fired on any write so RadioService can re-push the active band's antenna
    // to the live client on the next recompute, same pattern as PaSettingsStore.
    public event Action? Changed;

    public AntennaSettingsStore(ILogger<AntennaSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _bands = _db.GetCollection<AntennaBandEntry>("antenna_bands");
        _bands.EnsureIndex(x => x.Band, unique: true);

        _log.LogInformation("AntennaSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// Per-band antenna selection. Missing rows (fresh install, legacy DB)
    /// default to ANT1/ANT1 — the byte-identical "no relay change" state.
    /// </summary>
    public AntennaBandSelection GetBand(string band)
    {
        lock (_sync)
        {
            var e = _bands.FindOne(x => x.Band == band);
            return e is null
                ? new AntennaBandSelection(band, HpsdrAntenna.Ant1, HpsdrAntenna.Ant1)
                : new AntennaBandSelection(band, ClampAnt(e.TxAnt), ClampAnt(e.RxAnt));
        }
    }

    /// <summary>All HF bands, missing rows defaulting to ANT1/ANT1.</summary>
    public IReadOnlyList<AntennaBandSelection> GetAll()
    {
        lock (_sync)
        {
            var existing = _bands.FindAll().ToDictionary(e => e.Band, e => e);
            return BandUtils.HfBands
                .Select(b => existing.TryGetValue(b, out var e)
                    ? new AntennaBandSelection(b, ClampAnt(e.TxAnt), ClampAnt(e.RxAnt))
                    : new AntennaBandSelection(b, HpsdrAntenna.Ant1, HpsdrAntenna.Ant1))
                .ToArray();
        }
    }

    /// <summary>Upsert one band's antenna selection. Invalid band names are
    /// rejected by the caller; here we narrow to the HF set defensively.</summary>
    public void SetBand(string band, HpsdrAntenna txAnt, HpsdrAntenna rxAnt)
    {
        if (!BandUtils.HfBands.Contains(band)) return;
        lock (_sync)
        {
            var existing = _bands.FindOne(x => x.Band == band);
            if (existing is null)
            {
                _bands.Insert(new AntennaBandEntry
                {
                    Band = band,
                    TxAnt = (byte)txAnt,
                    RxAnt = (byte)rxAnt,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.TxAnt = (byte)txAnt;
                existing.RxAnt = (byte)rxAnt;
                existing.UpdatedUtc = DateTime.UtcNow;
                _bands.Update(existing);
            }
        }
        Changed?.Invoke();
    }

    // Defensive clamp on read — a corrupt / out-of-range byte resolves to ANT1
    // rather than throwing or producing a bogus enum value on the wire.
    private static HpsdrAntenna ClampAnt(byte v) =>
        v <= (byte)HpsdrAntenna.Ant3 ? (HpsdrAntenna)v : HpsdrAntenna.Ant1;

    public void Dispose() => _db.Dispose();
}

/// <summary>Resolved per-band antenna selection.</summary>
public sealed record AntennaBandSelection(string Band, HpsdrAntenna TxAnt, HpsdrAntenna RxAnt);

public sealed class AntennaBandEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    // 0-based HpsdrAntenna (Ant1=0). LiteDB is schema-less so rows persisted
    // before this feature hydrate these as 0 = ANT1, the correct legacy default.
    public byte TxAnt { get; set; }
    public byte RxAnt { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
