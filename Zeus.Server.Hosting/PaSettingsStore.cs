// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using LiteDB;
using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

// PA settings (per-band gain, OC pin masks, globals). Shares the unencrypted
// zeus-prefs.db with BandMemoryStore — neither PA gain values nor OC pin
// assignments are sensitive. Fires Changed on any write so RadioService can
// recompute the drive byte and protocol clients can pick up new OC masks on
// the next C&C/HPC tick.
public sealed class PaSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PaBandEntry> _bands;
    private readonly ILiteCollection<PaGlobalEntry> _globals;
    private readonly ILogger<PaSettingsStore> _log;
    private readonly object _sync = new();
    // Dedup key for the cross-board PA-gain substitution warning (issue #1180).
    // PaBandEntry rows are not board-scoped, so a value stored under one board
    // family's semantics survives a session into another board family — fired
    // once per (band, board) pair on read so the operator sees the substitution
    // in the log without flooding it on every recompute.
    private readonly HashSet<(string Band, HpsdrBoardKind Board)> _crossBoardWarned = new();

    public event Action? Changed;

    public PaSettingsStore(ILogger<PaSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _bands = _db.GetCollection<PaBandEntry>("pa_bands");
        _bands.EnsureIndex(x => x.Band, unique: true);
        _globals = _db.GetCollection<PaGlobalEntry>("pa_globals");

        _log.LogInformation("PaSettingsStore initialized at {Path}", dbPath);
    }

    // Fills missing bands with per-board defaults from PaDefaults. When board
    // is Unknown (no radio connected yet) the fallback is 0 dB, which keeps the
    // drive math pinned to legacy behavior until connect resolves the board.
    // The variant parameter resolves the 0x0A wire-byte alias collision per
    // issue #218; G2 default preserves pre-#218 behaviour for every other board.
    public PaSettingsDto GetAll(
        HpsdrBoardKind board = HpsdrBoardKind.Unknown,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        lock (_sync)
        {
            var g = _globals.FindAll().FirstOrDefault();
            // When nothing is persisted yet, seed the global with board-specific
            // defaults so new operators don't land in the "PaMaxPowerWatts=0 →
            // PaGainDb ignored" legacy mode on first connect.
            var global = g is null
                ? new PaGlobalSettingsDto(
                    PaEnabled: true,
                    PaMaxPowerWatts: PaDefaults.GetMaxPowerWatts(board, variant))
                : new PaGlobalSettingsDto(g.PaEnabled, g.PaMaxPowerWatts);

            var existing = _bands.FindAll().ToDictionary(e => e.Band, e => e);
            var bands = BandUtils.HfBands
                .Select(b =>
                {
                    var auto = AutoOcMaskFor(board, b);
                    if (existing.TryGetValue(b, out var e))
                    {
                        var gain = ResolvePaGainDbForBoard(e.PaGainDb, e.Band, board, variant);
                        return new PaBandSettingsDto(e.Band, gain, e.DisablePa, e.OcTx, e.OcRx, auto, e.OcDxTx, e.OcDxRx);
                    }
                    return new PaBandSettingsDto(b, PaGainDb: PaDefaults.GetPaGainDb(board, b, variant), AutoOcMask: auto);
                })
                .ToArray();

            return new PaSettingsDto(global, bands);
        }
    }

    // Pure board defaults — used by the "Reset to defaults" action in the
    // settings panel to stomp any prior per-operator calibration back to the
    // piHPSDR/Thetis-published seed values for the selected radio. Does NOT
    // consult the pa_bands / pa_globals collections; OC masks and DisablePa
    // stay out of this because they're wiring decisions, not per-board data.
    public PaSettingsDto GetDefaults(
        HpsdrBoardKind board,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        var global = new PaGlobalSettingsDto(
            PaEnabled: true,
            PaMaxPowerWatts: PaDefaults.GetMaxPowerWatts(board, variant));
        var bands = BandUtils.HfBands
            .Select(b => new PaBandSettingsDto(
                b,
                PaGainDb: PaDefaults.GetPaGainDb(board, b, variant),
                AutoOcMask: AutoOcMaskFor(board, b)))
            .ToArray();
        return new PaSettingsDto(global, bands);
    }

    public PaBandSettingsDto GetBand(
        string band,
        HpsdrBoardKind board = HpsdrBoardKind.Unknown,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        lock (_sync)
        {
            var auto = AutoOcMaskFor(board, band);
            var e = _bands.FindOne(x => x.Band == band);
            if (e is null)
                return new PaBandSettingsDto(band, PaGainDb: PaDefaults.GetPaGainDb(board, band, variant), AutoOcMask: auto);
            var gain = ResolvePaGainDbForBoard(e.PaGainDb, e.Band, board, variant);
            return new PaBandSettingsDto(e.Band, gain, e.DisablePa, e.OcTx, e.OcRx, auto, e.OcDxTx, e.OcDxRx);
        }
    }

    // Sanity-check a stored PA-gain value against the connected board's drive-
    // profile range and substitute the per-board default when the stored value
    // is outside it. The pa_bands collection is not board-scoped, so a value
    // calibrated under one board's semantics (e.g. HL2 stores PaGainDb as a
    // 0..100 percentage) survives into the next session against a different
    // board family (Hermes / ANAN / Orion read PaGainDb as a 0..70 dB forward
    // gain). When HL2's 100 % surfaces on Angelia as "100 dB", FullByteDriveProfile
    // quantises the drive byte to 0 and TX goes silent (issue #1180:
    // pa.recompute gainDb=100.00 -> byte=0 -> drv=0 -> p1 IQ-zero short-circuit
    // at ControlFrame.cs:858 -> no RF). The substitute keeps RecomputePaAndPush
    // sane on the next session; the stored row stays untouched so an explicit
    // Reset → Apply is still required for the operator to persist the
    // board-appropriate value.
    private double ResolvePaGainDbForBoard(double stored, string band, HpsdrBoardKind board, OrionMkIIVariant variant)
    {
        // No connected board yet (preview / pre-discovery) — we can't reason
        // about the right range, so leave the stored value as-is.
        if (board == HpsdrBoardKind.Unknown) return stored;

        // HL2: 0..100 percentage (HermesLite2DriveProfile). Everything else:
        // dB forward gain. The PA Settings panel clamps non-HL2 input to
        // 0..70 dB (see docs/lessons/hl2-drive-model.md), so any persisted
        // value > 70 on a dB board is necessarily cross-board contamination.
        double upper = board == HpsdrBoardKind.HermesLite2 ? 100.0 : 70.0;
        if (stored >= 0.0 && stored <= upper) return stored;

        var fallback = PaDefaults.GetPaGainDb(board, band, variant);
        if (_crossBoardWarned.Add((band, board)))
        {
            _log.LogWarning(
                "pa.gain.cross_board_substituted band={Band} board={Board} variant={Variant} stored={Stored:F2} validUpper={Upper:F1} -> using per-board default {Fallback:F2}. The value in pa_bands was persisted under a different board's semantics (likely HL2 ↔ Hermes/ANAN/Orion). Open PA Settings and press \"Reset to defaults\" then APPLY to overwrite the row.",
                band, board, variant, stored, upper, fallback);
        }
        return fallback;
    }

    // Read-only mirror of the on-wire auto-filter mask for the connected
    // board. Today only HL2 ships a board with an auto-mask path (N2ADR,
    // forced-on in RadioService.ConnectAsync). The PA Settings panel uses
    // this to show operators which OC pins are already being driven by the
    // firmware before they layer their own OcTx/OcRx wiring on top — closes
    // the perception gap from issue #217 where empty checkboxes implied no
    // pins were active.
    private static byte AutoOcMaskFor(HpsdrBoardKind board, string band) =>
        board == HpsdrBoardKind.HermesLite2
            ? N2adrBands.RxOcMaskForBand(band)
            : (byte)0;

    public PaGlobalSettingsDto GetGlobal(
        HpsdrBoardKind board = HpsdrBoardKind.Unknown,
        OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        lock (_sync)
        {
            var g = _globals.FindAll().FirstOrDefault();
            return g is null
                ? new PaGlobalSettingsDto(
                    PaEnabled: true,
                    PaMaxPowerWatts: PaDefaults.GetMaxPowerWatts(board, variant))
                : new PaGlobalSettingsDto(g.PaEnabled, g.PaMaxPowerWatts);
        }
    }

    public void Save(PaSettingsDto dto)
    {
        lock (_sync)
        {
            var existingGlobal = _globals.FindAll().FirstOrDefault();
            var g = existingGlobal ?? new PaGlobalEntry();
            g.PaEnabled = dto.Global.PaEnabled;
            g.PaMaxPowerWatts = Math.Max(0, dto.Global.PaMaxPowerWatts);
            g.UpdatedUtc = DateTime.UtcNow;
            if (existingGlobal is null) _globals.Insert(g);
            else _globals.Update(g);

            foreach (var band in dto.Bands)
            {
                if (!BandUtils.HfBands.Contains(band.Band)) continue;
                var existing = _bands.FindOne(x => x.Band == band.Band);
                // DX masks are 4-bit per the EU2AV spec (bits 0..3 ->
                // DX OUT 7..10); narrow to 0x0F before persisting so the
                // bench API can't smuggle bits the wire path will drop.
                byte dxTx = (byte)(band.OcDxTx & 0x0F);
                byte dxRx = (byte)(band.OcDxRx & 0x0F);
                if (existing is null)
                {
                    _bands.Insert(new PaBandEntry
                    {
                        Band = band.Band,
                        PaGainDb = band.PaGainDb,
                        DisablePa = band.DisablePa,
                        OcTx = band.OcTx,
                        OcRx = band.OcRx,
                        OcDxTx = dxTx,
                        OcDxRx = dxRx,
                        UpdatedUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.PaGainDb = band.PaGainDb;
                    existing.DisablePa = band.DisablePa;
                    existing.OcTx = band.OcTx;
                    existing.OcRx = band.OcRx;
                    existing.OcDxTx = dxTx;
                    existing.OcDxRx = dxRx;
                    existing.UpdatedUtc = DateTime.UtcNow;
                    _bands.Update(existing);
                }
            }
        }
        Changed?.Invoke();
    }

    public void Dispose() => _dbLease.Dispose();

}

// Resolved snapshot that RadioService pushes to the P1 client directly and to
// the P2 client via DspPipelineService. Keeps the protocol clients free of
// any knowledge of per-band gain or Stores.
//
// OcDxTxMask / OcDxRxMask carry the Anvelina-PRO3 DX OUT 7..10 wiring (4-bit
// masks, bit 0..3 = DX OUT 7..10). Pushed unconditionally; Protocol2Client
// gates whether they reach the wire by board + variant (#407 / EU2AV).
//
// TxAntenna / RxAntenna / HasTxAntennaRelays / RxAuxInput / MkiiBpfRxSelect
// carry the per-band external-antenna selection (external-ports plan — antenna
// slice, #804). Pushed unconditionally to the P2 client via
// DspPipelineService.SetAntennas; Protocol2Client gates the TX-antenna emission
// on HasTxAntennaRelays and routes the operator RX-aux strictly BEFORE the PS
// coupler OR (the PS-K36 firewall). All defaulted so existing constructions stay
// valid (default ANT1/ANT1/None = byte-identical to today).
public sealed record PaRuntimeSnapshot(
    byte DriveByte,
    byte OcTxMask,
    byte OcRxMask,
    bool PaEnabled,
    byte OcDxTxMask = 0,
    byte OcDxRxMask = 0,
    HpsdrAntenna TxAntenna = HpsdrAntenna.Ant1,
    HpsdrAntenna RxAntenna = HpsdrAntenna.Ant1,
    bool HasTxAntennaRelays = false,
    int RxAuxInput = 0,
    bool MkiiBpfRxSelect = false);

public sealed class PaBandEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    public double PaGainDb { get; set; }
    public bool DisablePa { get; set; }
    public byte OcTx { get; set; }
    public byte OcRx { get; set; }
    // Anvelina DX OUT 7..10 per-band masks (issue #407). LiteDB is schema-
    // less so rows persisted before #407 hydrate these as 0, which is the
    // correct legacy default. Wire-encoded into P2 byte 1397 bits [4:1]
    // only when the active radio is OrionMkII + AnvelinaPro3 on P2.
    public byte OcDxTx { get; set; }
    public byte OcDxRx { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class PaGlobalEntry
{
    public int Id { get; set; }
    public bool PaEnabled { get; set; } = true;
    public int PaMaxPowerWatts { get; set; }
    // NOTE: legacy rows persisted before #124 may carry an `OcTune` column.
    // LiteDB's BsonMapper silently ignores unknown fields when deserializing,
    // so existing PaSettings rows survive a load → save roundtrip with the
    // column dropped on the next write. The global "OC bits while Tune"
    // override was removed for hardware-safety (issue #124): it could hand
    // an external amp a confused band-select state during a steady tune
    // carrier and damage the finals. OC during TUN now follows OcTx.
    public DateTime UpdatedUtc { get; set; }
}
