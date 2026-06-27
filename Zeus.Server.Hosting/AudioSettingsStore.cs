// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Global (per-radio, NOT per-band) TX-audio SOURCE selection (external-ports
// plan, §2/§8). This REPLACES the prior four-bool AudioFrontEndSelection
// (LineIn/MicBoost/MicBias/BalancedInput/LineInGain) — that model only set codec
// mic-gain wire bits and never switched the TX pipeline, and it allowed illegal
// combinations. The model is now a single mutually-exclusive
// <see cref="Zeus.Contracts.TxAudioSource"/> selector PLUS the parameters that
// apply to whichever radio jack is chosen (MicBoost, MicBias, LineInGain).
//
// Mirrors AntennaSettingsStore's LiteDB pattern but holds ONE global row instead
// of a band-keyed collection — the audio source is a per-radio front-panel
// state, not per-band. Upsert uses DeleteMany+Insert rather than Update/Upsert to
// dodge the LiteDB `Id=0`-always-inserts bug (PR #387). The store is deliberately
// board-agnostic; the persisted Source is clamped against the connected board's
// capabilities at the RadioService push (a board that can't honour a jack falls
// back to Host), so a value stored on one radio is simply ignored on another.
//
// LEGACY MIGRATION: a pre-rework DB carries an AudioFrontEndEntry row WITHOUT the
// `Source` field. LiteDB is schema-less, so that row hydrates `Source` to its
// default value 0 == TxAudioSource.Host — exactly the clean fallback the plan
// requires (§8). The dropped `LineIn`/`BalancedInput` columns are simply ignored.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class AudioSettingsStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<AudioFrontEndEntry> _rows;
    private readonly ILogger<AudioSettingsStore> _log;
    private readonly object _sync = new();

    // Fired on any write so RadioService can re-push the audio state to the
    // live client — same pattern as AntennaSettingsStore.Changed.
    public event Action? Changed;

    public AudioSettingsStore(ILogger<AudioSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _rows = _db.GetCollection<AudioFrontEndEntry>("audio_frontend");

        _log.LogInformation("AudioSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// The persisted global TX-audio source state. A missing row (fresh install
    /// / pre-feature DB) — AND a legacy four-bool row that has no
    /// <c>Source</c> field — defaults to <see cref="TxAudioSource.Host"/>, the
    /// radio's power-on state with the analog jacks suppressed. That default is
    /// byte-identical to today's wire output on every board.
    /// </summary>
    public AudioSourceSelection Get()
    {
        lock (_sync)
        {
            var e = _rows.FindAll().FirstOrDefault();
            return e is null
                ? AudioSourceSelection.Default
                : new AudioSourceSelection(
                    Source: ClampSource(e.Source),
                    MicBoost: e.MicBoost,
                    MicBias: e.MicBias,
                    LineInGain: ClampGain(e.LineInGain));
        }
    }

    /// <summary>
    /// Replace the global TX-audio source state. Uses DeleteMany+Insert so the
    /// single global row is rewritten cleanly regardless of its Id (the LiteDB
    /// Id=0-always-inserts bug, PR #387). mic_bias is stored as-given; the
    /// REST / UI layer is responsible for the floating-connector guard.
    /// </summary>
    public void Set(AudioSourceSelection sel)
    {
        lock (_sync)
        {
            _rows.DeleteMany(_ => true);
            _rows.Insert(new AudioFrontEndEntry
            {
                Source = (byte)ClampSource((byte)sel.Source),
                MicBoost = sel.MicBoost,
                MicBias = sel.MicBias,
                LineInGain = ClampGain(sel.LineInGain),
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        Changed?.Invoke();
    }

    // line_in_gain is a 5-bit field (0..31) on both the HL2 0x14 frame and the
    // P2 TxSpecific byte 51. Clamp defensively on read/write so a stale value
    // can never overflow the wire field.
    private static byte ClampGain(int v) => (byte)Math.Clamp(v, 0, 31);

    // An out-of-range persisted source byte (corruption / a future enum value an
    // older build can't represent) falls back to Host rather than reaching the
    // wire as an undefined selection.
    private static TxAudioSource ClampSource(byte raw) =>
        Enum.IsDefined(typeof(TxAudioSource), raw) ? (TxAudioSource)raw : TxAudioSource.Host;

    public void Dispose() => _dbLease.Dispose();
}

/// <summary>
/// Resolved global TX-audio source selection (external-ports plan, §2).
/// <see cref="Source"/> is the single mutually-exclusive selector; the remaining
/// fields are PARAMETERS OF that source and apply only when the matching radio
/// jack is chosen: <see cref="MicBoost"/>/<see cref="MicBias"/> for
/// <see cref="TxAudioSource.RadioMic"/> (and MicBoost optionally for XLR),
/// <see cref="LineInGain"/> (0..31) for <see cref="TxAudioSource.RadioLineIn"/>.
/// Under <see cref="TxAudioSource.Host"/> none of the params reach the wire — the
/// encoder is a pure function of Source and returns literal zero for Host.
/// </summary>
public sealed record AudioSourceSelection(
    TxAudioSource Source,
    bool MicBoost,
    bool MicBias,
    byte LineInGain)
{
    /// <summary>Power-on default: Host audio (radio jacks suppressed), no
    /// boost/bias, gain 0 — byte-identical to today's wire output.</summary>
    public static readonly AudioSourceSelection Default =
        new(Source: TxAudioSource.Host, MicBoost: false, MicBias: false, LineInGain: 0);
}

/// <summary>
/// Runtime audio front-end push payload (external-ports plan). RadioService fires
/// this on <c>AudioFrontEndChanged</c>; DspPipelineService forwards it into the
/// live Protocol2Client (TxSpecific bytes 50/51). The wire bytes are already
/// resolved (board-clamped + Host-zeroed) by the time it is raised — the payload
/// carries the literal byte-50 mic_control + byte-51 line_in_gain so the
/// forwarder does no source interpretation of its own.
/// </summary>
public sealed record AudioFrontEndPush(
    TxAudioSource Source,
    byte MicControlByte,
    byte LineInGain);

public sealed class AudioFrontEndEntry
{
    public int Id { get; set; }
    // The persisted TX-audio source (TxAudioSource as byte). LiteDB is
    // schema-less, so a legacy four-bool row that predates this field hydrates
    // Source = 0 == TxAudioSource.Host — the correct migration default.
    public byte Source { get; set; }
    // Per-source parameters. MicBoost/MicBias apply to RadioMic; LineInGain to
    // RadioLineIn. All ignored under Host.
    public bool MicBoost { get; set; }
    public bool MicBias { get; set; }
    // 0..31 line-in gain. Rows written before this feature hydrate this as 0,
    // the correct legacy default.
    public byte LineInGain { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
