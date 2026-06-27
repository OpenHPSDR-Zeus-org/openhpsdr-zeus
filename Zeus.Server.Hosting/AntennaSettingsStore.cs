// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Per-band TX/RX antenna relay + RX-aux selection (external-ports plan —
// antenna slice, issue #804).
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
//
// Connection=shared + PrefsDbPath.Get() mirror PaSettingsStore for cross-
// platform parity; endpoint/store tests derive from IsolatedPrefsFactory to
// avoid the Linux LiteDB shared-mode crash (GH #682).

using LiteDB;
using Zeus.Protocol1;

namespace Zeus.Server;

public sealed class AntennaSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
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

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _bands = _db.GetCollection<AntennaBandEntry>("antenna_bands");
        _bands.EnsureIndex(x => x.Band, unique: true);

        _log.LogInformation("AntennaSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// Per-band antenna selection. Missing rows (fresh install, legacy DB)
    /// default to ANT1/ANT1/None — the byte-identical "no relay change" state.
    /// </summary>
    public AntennaBandSelection GetBand(string band)
    {
        lock (_sync)
        {
            var e = _bands.FindOne(x => x.Band == band);
            return e is null
                ? new AntennaBandSelection(band, HpsdrAntenna.Ant1, HpsdrAntenna.Ant1, RxAuxInputSel.None)
                : new AntennaBandSelection(band, ClampAnt(e.TxAnt), ClampAnt(e.RxAnt), ClampAux(e.RxAux));
        }
    }

    /// <summary>All HF bands, missing rows defaulting to ANT1/ANT1/None.</summary>
    public IReadOnlyList<AntennaBandSelection> GetAll()
    {
        lock (_sync)
        {
            var existing = _bands.FindAll().ToDictionary(e => e.Band, e => e);
            return BandUtils.HfBands
                .Select(b => existing.TryGetValue(b, out var e)
                    ? new AntennaBandSelection(b, ClampAnt(e.TxAnt), ClampAnt(e.RxAnt), ClampAux(e.RxAux))
                    : new AntennaBandSelection(b, HpsdrAntenna.Ant1, HpsdrAntenna.Ant1, RxAuxInputSel.None))
                .ToArray();
        }
    }

    /// <summary>Upsert one band's antenna selection. Invalid band names are
    /// rejected by the caller; here we narrow to the HF set defensively.</summary>
    public void SetBand(string band, HpsdrAntenna txAnt, HpsdrAntenna rxAnt)
        => SetBand(band, txAnt, rxAnt, RxAuxInputSel.None);

    /// <summary>Upsert one band's antenna + RX-aux selection.
    /// <paramref name="rxAux"/> selects an auxiliary RX feed
    /// (EXT1/EXT2/XVTR/BYPASS); <see cref="RxAuxInputSel.None"/> uses the base
    /// RX-antenna relay. LiteDB is schema-less so rows written before this slice
    /// hydrate RxAux as 0 = None.</summary>
    public void SetBand(string band, HpsdrAntenna txAnt, HpsdrAntenna rxAnt, RxAuxInputSel rxAux)
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
                    RxAux = (byte)rxAux,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.TxAnt = (byte)txAnt;
                existing.RxAnt = (byte)rxAnt;
                existing.RxAux = (byte)rxAux;
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

    private static RxAuxInputSel ClampAux(byte v) =>
        v <= (byte)RxAuxInputSel.Bypass ? (RxAuxInputSel)v : RxAuxInputSel.None;

    public void Dispose() => _dbLease.Dispose();
}

/// <summary>
/// Per-band RX auxiliary input selection (external-ports plan — antenna slice,
/// #804). A SINGLE-choice enum (the radio routes ONE RX source at a time) —
/// distinct from the <c>[Flags]</c> <see cref="Zeus.Contracts.RxAuxInputs"/>
/// capability set. Persisted as a byte; <see cref="None"/> means "use the base
/// RX-antenna relay" (today's behaviour). Maps 1:1 to the Protocol2Client aux
/// selector (1=EXT1, 2=EXT2, 3=XVTR, 4=BYPASS), with 0 = no aux.
/// </summary>
public enum RxAuxInputSel : byte
{
    None  = 0,
    Ext1  = 1,
    Ext2  = 2,
    Xvtr  = 3,
    Bypass = 4,
}

/// <summary>Resolved per-band antenna + RX-aux selection.</summary>
public sealed record AntennaBandSelection(
    string Band, HpsdrAntenna TxAnt, HpsdrAntenna RxAnt, RxAuxInputSel RxAux);

public sealed class AntennaBandEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    // 0-based HpsdrAntenna (Ant1=0). LiteDB is schema-less so rows persisted
    // before this feature hydrate these as 0 = ANT1, the correct legacy default.
    public byte TxAnt { get; set; }
    public byte RxAnt { get; set; }
    // RX auxiliary input (RxAuxInputSel byte). Pre-feature rows hydrate as
    // 0 = None, the byte-identical legacy default.
    public byte RxAux { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
