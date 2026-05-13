// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PaBandEntry> _bands;
    private readonly ILiteCollection<PaGlobalEntry> _globals;
    private readonly ILogger<PaSettingsStore> _log;
    private readonly object _sync = new();

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

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
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
                        return new PaBandSettingsDto(e.Band, e.PaGainDb, e.DisablePa, e.OcTx, e.OcRx, auto);
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
            return e is null
                ? new PaBandSettingsDto(band, PaGainDb: PaDefaults.GetPaGainDb(board, band, variant), AutoOcMask: auto)
                : new PaBandSettingsDto(e.Band, e.PaGainDb, e.DisablePa, e.OcTx, e.OcRx, auto);
        }
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
                if (existing is null)
                {
                    _bands.Insert(new PaBandEntry
                    {
                        Band = band.Band,
                        PaGainDb = band.PaGainDb,
                        DisablePa = band.DisablePa,
                        OcTx = band.OcTx,
                        OcRx = band.OcRx,
                        UpdatedUtc = DateTime.UtcNow,
                    });
                }
                else
                {
                    existing.PaGainDb = band.PaGainDb;
                    existing.DisablePa = band.DisablePa;
                    existing.OcTx = band.OcTx;
                    existing.OcRx = band.OcRx;
                    existing.UpdatedUtc = DateTime.UtcNow;
                    _bands.Update(existing);
                }
            }
        }
        Changed?.Invoke();
    }

    public void Dispose() => _db.Dispose();

}

// Resolved snapshot that RadioService pushes to the P1 client directly and to
// the P2 client via DspPipelineService. Keeps the protocol clients free of
// any knowledge of per-band gain or Stores.
public sealed record PaRuntimeSnapshot(
    byte DriveByte,
    byte OcTxMask,
    byte OcRxMask,
    bool PaEnabled);

public sealed class PaBandEntry
{
    public int Id { get; set; }
    public string Band { get; set; } = string.Empty;
    public double PaGainDb { get; set; }
    public bool DisablePa { get; set; }
    public byte OcTx { get; set; }
    public byte OcRx { get; set; }
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
