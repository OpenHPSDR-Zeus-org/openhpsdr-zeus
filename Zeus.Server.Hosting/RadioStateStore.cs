// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists the subset of RadioService state that no dedicated store covers:
// active mode/VFO, RX/TX filter bounds, master volume, display zoom,
// auto-ATT/AGC toggles, user-baseline attenuator, and the eight per-mode-
// family filter memory slots (SSB/AM/FM/CW × RX/TX).
//
// Not persisted here — handled elsewhere or intentionally ephemeral:
//   AgcTopDb, Nr, Cfc       → DspSettingsStore
//   PA/OC settings          → PaSettingsStore
//   PS tunables             → PsSettingsStore
//   Filter presets          → FilterPresetStore
//   Per-band VFO/mode       → BandMemoryStore
//   PsEnabled, PsMonitor,
//   TxMonitorEnabled        → ephemeral (operator re-arms each session)
//   AttOffsetDb, AgcOffsetDb → control-loop accumulators, always reset
//   AdcOverloadWarning      → derived / transient
//   PsCorrecting, PsCalState → PS feedback state
//   Status, Endpoint        → connection state
//   SampleRate              → per-board sub-collection in this store
//
// Single-row global snapshot (collection "radio_state") plus a keyed
// sub-collection ("board_sample_rates") for per-board sample-rate memory.
// Both share zeus-prefs.db with the other preference stores.
public sealed class RadioStateStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RadioStateEntry> _state;
    private readonly ILiteCollection<BoardSampleRateEntry> _boardRates;
    private readonly ILogger<RadioStateStore> _log;
    private readonly object _sync = new();

    public RadioStateStore(ILogger<RadioStateStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _state = _db.GetCollection<RadioStateEntry>("radio_state");
        _boardRates = _db.GetCollection<BoardSampleRateEntry>("board_sample_rates");
        _boardRates.EnsureIndex(x => x.BoardKey, unique: true);

        _log.LogInformation("RadioStateStore initialized at {Path}", dbPath);
    }

    // Returns null on first run (no snapshot written yet).
    public RadioStateEntry? Get()
    {
        lock (_sync)
            return _state.FindAll().FirstOrDefault();
    }

    // Upsert the full snapshot. RadioService calls this from the debounce flush timer.
    public void Save(RadioStateEntry entry)
    {
        lock (_sync)
        {
            var existing = _state.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _state.Insert(entry);
            }
            else
            {
                entry.Id = existing.Id;
                _state.Update(entry);
            }
        }
    }

    // Per-board sample-rate memory. Key is "{boardByte}" for non-OrionMkII
    // boards; "{boardByte}-{variantByte}" for the 0x0A alias family so that
    // an operator who switches from a G2 to an ANAN-8000DLE doesn't restore
    // the G2's 192 kHz rate on a board that may prefer 96 kHz (or vice versa).
    public int? GetBoardSampleRate(HpsdrBoardKind board, OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        var key = BoardKey(board, variant);
        lock (_sync)
        {
            var e = _boardRates.FindOne(x => x.BoardKey == key);
            return e?.SampleRateHz;
        }
    }

    public void SetBoardSampleRate(HpsdrBoardKind board, int hz, OrionMkIIVariant variant = OrionMkIIVariant.G2)
    {
        var key = BoardKey(board, variant);
        lock (_sync)
        {
            var existing = _boardRates.FindOne(x => x.BoardKey == key);
            if (existing is null)
            {
                _boardRates.Insert(new BoardSampleRateEntry
                {
                    BoardKey = key,
                    SampleRateHz = hz,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.SampleRateHz = hz;
                existing.UpdatedUtc = DateTime.UtcNow;
                _boardRates.Update(existing);
            }
        }
    }

    public void Dispose() => _db.Dispose();

    private static string BoardKey(HpsdrBoardKind board, OrionMkIIVariant variant) =>
        board == HpsdrBoardKind.OrionMkII
            ? $"{(byte)board}-{(byte)variant}"
            : $"{(byte)board}";
}

// LiteDB persistence model for the global RadioService snapshot.
// Field defaults match the RadioService hardcoded defaults so that a row
// written by an older build (missing new fields) deserialises with
// sensible values.
public sealed class RadioStateEntry
{
    public int Id { get; set; }
    // Active radio state
    public long VfoHz { get; set; } = 14_200_000;
    public RxMode Mode { get; set; } = RxMode.USB;
    public int FilterLowHz { get; set; } = 100;
    public int FilterHighHz { get; set; } = 2850;
    public int TxFilterLowHz { get; set; } = 150;
    public int TxFilterHighHz { get; set; } = 2850;
    public string? FilterPresetName { get; set; } = "VAR1";
    public bool AutoAttEnabled { get; set; } = true;
    public int AttenDb { get; set; }
    public bool AutoAgcEnabled { get; set; }
    public double RxAfGainDb { get; set; }
    public int ZoomLevel { get; set; } = 1;
    // Per-mode-family RX filter memory (abs values, always positive)
    public int SsbFilterLoAbs { get; set; } = 150;
    public int SsbFilterHiAbs { get; set; } = 2850;
    public int AmFilterLoAbs { get; set; }
    public int AmFilterHiAbs { get; set; } = 4000;
    public int FmFilterLoAbs { get; set; }
    public int FmFilterHiAbs { get; set; } = 5500;
    public int CwFilterLoAbs { get; set; } = 475;
    public int CwFilterHiAbs { get; set; } = 725;
    // Per-mode-family TX filter memory (abs values)
    public int SsbTxFilterLoAbs { get; set; } = 150;
    public int SsbTxFilterHiAbs { get; set; } = 2850;
    public int AmTxFilterLoAbs { get; set; }
    public int AmTxFilterHiAbs { get; set; } = 4000;
    public int FmTxFilterLoAbs { get; set; }
    public int FmTxFilterHiAbs { get; set; } = 3000;
    public int CwTxFilterLoAbs { get; set; } = 475;
    public int CwTxFilterHiAbs { get; set; } = 725;
    public DateTime UpdatedUtc { get; set; }
}

// Per-board sample-rate memory. Keyed by board byte (plus variant byte for
// the 0x0A / OrionMkII alias family) so switching between a HermesLite2
// and an ANAN-G2 doesn't bleed one radio's preferred rate onto the other.
public sealed class BoardSampleRateEntry
{
    public int Id { get; set; }
    public string BoardKey { get; set; } = string.Empty;
    public int SampleRateHz { get; set; } = 192_000;
    public DateTime UpdatedUtc { get; set; }
}
