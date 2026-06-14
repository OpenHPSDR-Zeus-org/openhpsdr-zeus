// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Global (per-radio, NOT per-band) audio front-end selection (external-ports
// plan, Phase 4): mic-vs-line-in, mic boost, mic bias, line-in gain, and the
// HL2 mic_trs / Saturn XLR balanced-input selects.
//
// Mirrors AntennaSettingsStore's LiteDB pattern but holds ONE global row
// instead of a band-keyed collection — the audio front-end is a per-radio
// front-panel state, not per-band. Upsert uses DeleteMany+Insert rather than
// Update/Upsert to dodge the LiteDB `Id=0` bug (an Upsert with Id=0 always
// inserts, never updates — PR #387). The store is deliberately board-agnostic;
// which fields actually reach the wire is gated per-board at the encoder /
// REST layer (HasOnboardCodec for Hermes-class, HermesLite2MicFrontEnd for
// HL2), so a value stored on one radio is simply ignored on another.

using LiteDB;

namespace Zeus.Server;

public sealed class AudioSettingsStore : IDisposable
{
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

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _rows = _db.GetCollection<AudioFrontEndEntry>("audio_frontend");

        _log.LogInformation("AudioSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>
    /// The persisted global audio front-end state. A missing row (fresh
    /// install / legacy DB) defaults to the radio's power-on state: mic input
    /// (line-in off), no boost, mic bias OFF (floating-connector PTT-hang
    /// guard), line-in gain 0, balanced input off. That default is
    /// byte-identical to today's wire output on every board.
    /// </summary>
    public AudioFrontEndSelection Get()
    {
        lock (_sync)
        {
            var e = _rows.FindAll().FirstOrDefault();
            return e is null
                ? AudioFrontEndSelection.Default
                : new AudioFrontEndSelection(
                    LineIn: e.LineIn,
                    MicBoost: e.MicBoost,
                    MicBias: e.MicBias,
                    BalancedInput: e.BalancedInput,
                    LineInGain: ClampGain(e.LineInGain));
        }
    }

    /// <summary>
    /// Replace the global audio front-end state. Uses DeleteMany+Insert so the
    /// single global row is rewritten cleanly regardless of its Id (the LiteDB
    /// Id=0-always-inserts bug, PR #387). mic_bias is stored as-given; the
    /// REST / UI layer is responsible for the floating-connector guard.
    /// </summary>
    public void Set(AudioFrontEndSelection sel)
    {
        lock (_sync)
        {
            _rows.DeleteMany(_ => true);
            _rows.Insert(new AudioFrontEndEntry
            {
                LineIn = sel.LineIn,
                MicBoost = sel.MicBoost,
                MicBias = sel.MicBias,
                BalancedInput = sel.BalancedInput,
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

    public void Dispose() => _db.Dispose();
}

/// <summary>
/// Resolved global audio front-end selection. <see cref="LineIn"/> selects
/// line-in vs mic. <see cref="MicBias"/> is the Orion/HL2 mic-bias enable —
/// defaults OFF (enabling on a floating connector can hang PTT).
/// <see cref="BalancedInput"/> is the Saturn XLR balanced-input select (P2
/// only). <see cref="LineInGain"/> is the 0..31 line-in gain.
/// </summary>
public sealed record AudioFrontEndSelection(
    bool LineIn,
    bool MicBoost,
    bool MicBias,
    bool BalancedInput,
    byte LineInGain)
{
    /// <summary>Power-on default: mic input, no boost/bias, no balanced
    /// input, gain 0 — byte-identical to today's wire output.</summary>
    public static readonly AudioFrontEndSelection Default =
        new(LineIn: false, MicBoost: false, MicBias: false, BalancedInput: false, LineInGain: 0);
}

/// <summary>
/// Runtime audio front-end push payload (external-ports plan, Phase 4).
/// RadioService fires this on <c>AudioFrontEndChanged</c>; DspPipelineService
/// forwards it into the live Protocol2Client (TxSpecific bytes 50/51). Already
/// per-board-gated by the time it is raised — a non-audio board pushes the
/// all-default (no-op) payload.
/// </summary>
public sealed record AudioFrontEndPush(
    bool LineIn,
    bool MicBoost,
    bool MicBias,
    bool BalancedInput,
    byte LineInGain);

public sealed class AudioFrontEndEntry
{
    public int Id { get; set; }
    public bool LineIn { get; set; }
    public bool MicBoost { get; set; }
    public bool MicBias { get; set; }
    public bool BalancedInput { get; set; }
    // 0..31 line-in gain. LiteDB is schema-less so rows written before this
    // feature hydrate this as 0, the correct legacy default.
    public byte LineInGain { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
