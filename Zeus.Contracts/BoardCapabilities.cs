// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Contracts;

/// <summary>
/// RX auxiliary inputs a board's Alex / filter board exposes (external-ports
/// plan, Phase 5). These are the receive-only feed paths in addition to the
/// base ANT1/2/3 relays: an external transverter IF (<see cref="Xvtr"/>), two
/// external receive-loop inputs (<see cref="Ext1"/> / <see cref="Ext2"/>), and
/// the RX-bypass / K36 path (<see cref="Bypass"/>) that is BIT-IDENTICAL to the
/// PureSignal external-feedback select on the wire (alex0 bit 11). A board can
/// expose any subset; HL2 exposes <see cref="None"/> (the inputs do not
/// physically exist). The flags gate the UI/REST/wire so an operator can only
/// pick an input the connected board actually has.
/// </summary>
[Flags]
public enum RxAuxInputs
{
    None  = 0,
    Ext1  = 1 << 0,
    Ext2  = 1 << 1,
    Xvtr  = 1 << 2,
    Bypass = 1 << 3,
    /// <summary>Every aux input — the full Alex / Saturn-BPF set.</summary>
    All = Ext1 | Ext2 | Xvtr | Bypass,
}

/// <summary>
/// The selected RX antenna / aux input for one band (external-ports plan,
/// Phase 5). Base relays (<see cref="Ant1"/>..<see cref="Ant3"/>) coexist with
/// the aux feeds; this is a single enum because the radio routes ONE RX source
/// at a time. Persisted as a byte in <c>antenna_bands</c>; default
/// <see cref="Ant1"/> reproduces today's wire bit-for-bit.
/// </summary>
public enum RxSource : byte
{
    Ant1  = 0,
    Ant2  = 1,
    Ant3  = 2,
    Ext1  = 10,
    Ext2  = 11,
    Xvtr  = 12,
    Bypass = 13,
}

/// <summary>
/// Alex / filter-board revision family, for the K36-BYPASS-direction branch
/// (external-ports plan, §3.4(4)). On older Rev 15/16 Alex boards K36 switches
/// an OUTPUT and PureSignal external feedback must route to EXT1/XVTR (selecting
/// BYPASS breaks PS); on Rev 24+ boards K36 switches an INPUT and PS routes to
/// BYPASS. THIS IS NOT WIRE-DISCOVERABLE — verified against pihpsdr (operator
/// boolean <c>new_pa_board</c>, radio.c:221) and Thetis (model-derived
/// <c>mkiibpf</c>); neither reads it from a discovery/status packet. Zeus
/// therefore DEFAULTS CONSERVATIVELY to <see cref="Modern"/> (Rev 24+, BYPASS
/// for PS) — the behaviour Zeus already ships (PS external feedback sets bit 11)
/// — and treats the revision as an operator preference, never a guessed wire
/// value that could mis-route PS. See the open-question note in the Phase 5 PR.
/// </summary>
public enum AlexRevision
{
    /// <summary>Rev 24+ — K36 is an input; PureSignal external feedback routes
    /// to BYPASS (alex0 bit 11). The safe default.</summary>
    Modern = 0,
    /// <summary>Rev 15/16 — K36 is an output; PureSignal external feedback must
    /// route to EXT1/XVTR, NOT BYPASS. Operator opt-in only.</summary>
    Legacy15Or16 = 1,
}

/// <summary>
/// Per-board capability fingerprint. Mirrors the facts Thetis MW0LGE
/// special-cases in <c>clsHardwareSpecific.cs</c> — RX ADC count, MKII
/// BPF support, ADC supply mV, LR audio swap, telemetry presence,
/// audio amplifier, RX2 attenuation mode, Path Illustrator gating.
///
/// These are board-static facts (do not depend on connection state or
/// operator preferences). Dispatch lives in
/// <c>Zeus.Server.Hosting.BoardCapabilitiesTable.For(HpsdrBoardKind)</c>;
/// this record is a wire-stable contract for the web client to read once
/// at connect via <c>/api/radio/capabilities</c>.
///
/// Cross-references: <c>docs/references/protocol-1/thetis-board-matrix.md</c>
/// (the spec) and Thetis <c>clsHardwareSpecific.cs:85-803</c> (the source).
/// </summary>
public sealed record BoardCapabilities(
    /// <summary>RX ADC count: 1 for Hermes-class single-receiver boards
    /// (Hermes / ANAN-10 / ANAN-10E / ANAN-100 / ANAN-100B / ANAN-G2E),
    /// 2 for DDC dual-receiver family (ANAN-100D / ANAN-200D / OrionMkII /
    /// 7000DLE / 8000DLE / G2 / G2-1K / ANVELINA-PRO3 / Red Pitaya).</summary>
    int RxAdcCount,
    /// <summary>True for second-generation Apache Labs boards using the
    /// MKII band-pass filter board (Orion MkII / 7000DLE / 8000DLE / G2
    /// family / G2E / ANVELINA-PRO3). Drives the Alex BPF wire bits.</summary>
    bool MkiiBpf,
    /// <summary>ADC supply voltage in millivolts: 33 for Hermes-class,
    /// 50 for the high-power family from ANAN-200D onwards.</summary>
    int AdcSupplyMv,
    /// <summary>True for Hermes-family boards that need L/R audio swapped
    /// (HERMES / ANAN-10 / ANAN-10E / ANAN-100 / ANAN-100B). Off for every
    /// DDC family board.</summary>
    bool LrAudioSwap,
    /// <summary>Board has on-board PA voltage telemetry (Thetis HasVolts).
    /// 7000D / 8000D / G2 / G2-1K / G2E / ANVELINA-PRO3 / Red Pitaya.</summary>
    bool HasVolts,
    /// <summary>Board has on-board PA current telemetry (Thetis HasAmps).
    /// Same set as <see cref="HasVolts"/>.</summary>
    bool HasAmps,
    /// <summary>Board has on-board headphone / audio amplifier. Thetis
    /// gates this on Protocol-2 only (<c>HasAudioAmplifier</c> at
    /// <c>clsHardwareSpecific.cs:459-468</c>); ANAN-7000DLE / 8000DLE /
    /// G2 / G2-1K / G2E / ANVELINA-PRO3 / Red Pitaya.</summary>
    bool HasAudioAmplifier,
    /// <summary>RX2 has a hardware stepped attenuator (true) or relies on
    /// firmware gain-reduction (false). RX1 is always stepped on supported
    /// boards. False for HERMES / ANAN-10 / ANAN-10E / ANAN-100 / ANAN-100B /
    /// ANAN-G2E and any single-RX board (where RX2 doesn't exist).
    /// True for the dual-RX DDC family.</summary>
    bool HasSteppedAttenuationRx2,
    /// <summary>UI Path Illustrator panel is supported. Thetis
    /// <c>clsHardwareSpecific.cs:773-780</c> excludes the high-power
    /// MkII family (OrionMkII / 7000DLE / 8000DLE / G2 / G2-1K /
    /// ANVELINA-PRO3 / Red Pitaya / G2E).</summary>
    bool SupportsPathIllustrator,
    /// <summary>Rated maximum forward output power in watts, used as the
    /// default top-of-axis for the TX power meter so a fresh connect to any
    /// supported radio gives a meter that's neither cramped nor blank.
    /// HermesLite2 / ANAN-10 = 10 W, ANAN-10E = 30 W, ANAN-100/200/G2 family
    /// = 120 W, ANAN-8000DLE = 250 W, ANAN-G2-1K = 1000 W. The operator can
    /// still override per-rig in the PA settings panel; this is the
    /// out-of-the-box default the meter axis snaps to on connect.</summary>
    int MaxPowerWatts,
    /// <summary>True when the board exposes the HL2-only optional toggles
    /// surfaced by <c>/api/radio/hl2-options</c> (Band Volts PWM enable,
    /// future mi0bot HL2 toggles). The frontend gates the HL2 settings panel
    /// on this flag so the controls don't appear for boards that ignore
    /// them. True for <see cref="HpsdrBoardKind.HermesLite2"/> only — Square
    /// SDR ships HL2-class firmware so it inherits via the same enum value.
    /// Issue #279.</summary>
    bool HasHl2OptionalToggles = false,
    /// <summary>True when the board exposes the Anvelina-PRO3 DX Open-
    /// Collector extension (USEROUT7..10) defined by EU2AV's
    /// <c>Open_Collector_Anvelina_DX for Thetis</c> spec (issue #407).
    /// True for <see cref="HpsdrBoardKind.OrionMkII"/> +
    /// <see cref="OrionMkIIVariant.AnvelinaPro3"/> only — the OC DX
    /// controls in the PA Settings panel render unconditionally but are
    /// disabled when this flag is false, so operators can see the
    /// feature exists without being able to drive a non-supporting
    /// board.</summary>
    bool SupportsAnvelinaDxOc = false,
    // ---- External-port control capabilities (external-ports plan) ---------
    // Static per-board facts that gate the "Radio Settings" external-port
    // controls (antenna select, audio front-end). Additive optional fields —
    // the capabilities response is JSON, so older frontends ignore them and
    // newer ones default the absent fields. Conservative defaults (false) so
    // an unknown board never advertises a port it may not physically have.
    /// <summary>Board has switchable TX antenna relays (ANT1/2/3). True only
    /// for the 0x0A OrionMkII / Saturn family (G2 / 7000DLE / 8000DLE / G2-1K /
    /// ANVELINA-PRO3 / Red Pitaya / Apache OrionMkII original). Every P1 board
    /// (Hermes-class, ANAN-100D/200D, ANAN-G2E) and Hermes-Lite 2 are
    /// ANT1-hardwired on transmit. Gates the TX-antenna selector and, at the
    /// wire layer, whether the encoder emits TX-antenna relay bits.</summary>
    bool HasTxAntennaRelays = false,
    /// <summary>Board has switchable RX antenna relays (ANT1/2/3) via its
    /// Alex/filter board. True for every ANAN board (Hermes-class through the
    /// Saturn family and ANAN-G2E). False for Hermes-Lite 2 — HL2 has a single
    /// antenna jack and its C3[5] bit drives the N2ADR antenna pad, not an
    /// ANT1/2/3 relay, so RX-antenna selection MUST be clamped to ANT1 on HL2
    /// at the wire layer.</summary>
    bool HasRxAntennaRelays = false,
    /// <summary>Board decodes the host→radio audio STREAM (TLV320 codec) — the
    /// path that carries the Hermes-class mic / line-in front-end. True for
    /// every ANAN board. False for Hermes-Lite 2, which has no stream codec.
    /// STREAM codec only; does NOT gate the HL2 mic front-end (see
    /// <see cref="HermesLite2MicFrontEnd"/>).</summary>
    bool HasOnboardCodec = false,
    /// <summary>Board has the Hermes-Lite 2 mic / PTT / line-in front-end on
    /// register 0x0a (wire byte 0x14): mic_trs, mic_bias, mic_ptt and a 5-bit
    /// line-in gain. True for <see cref="HpsdrBoardKind.HermesLite2"/> only.
    /// Distinct from <see cref="HasOnboardCodec"/> — HL2 has the mic front-end
    /// but not the stream codec. mic_bias defaults OFF (enabling it on a
    /// floating connector can hang PTT) and is guarded.</summary>
    bool HermesLite2MicFrontEnd = false,
    // ---- TX-audio source jacks (external-ports plan, §6) ------------------
    // Static per-board facts gating which <see cref="Contracts.TxAudioSource"/>
    // options the Radio Settings panel offers. Additive optional fields — older
    // frontends ignore them; conservative defaults (false) so an unknown board
    // never advertises a jack it may not physically have. Host is ALWAYS
    // available regardless of these flags.
    /// <summary>Board has an analog line-in jack the operator can select as the
    /// TX-audio source (<see cref="Contracts.TxAudioSource.RadioLineIn"/>). True
    /// for ANAN-200D (Orion) and the whole 0x0A Saturn family; false for pure
    /// Hermes-class P1 boards (no P1 radio-mic receive path in v1 — §6),
    /// ANAN-G2E, Metis, and Hermes-Lite 2.</summary>
    bool HasRadioLineIn = false,
    /// <summary>Board has a switchable balanced XLR microphone input
    /// (<see cref="Contracts.TxAudioSource.RadioBalancedXlr"/>). True ONLY for
    /// the Saturn-FPGA ANAN-G2 and ANAN-G2-1K; offered nowhere else (use this
    /// flag, never <see cref="HasAudioAmplifier"/>, to gate XLR).</summary>
    bool HasBalancedXlr = false,
    /// <summary>Board exposes the Orion mic-bias enable on its mic jack. In
    /// Thetis the bias control lives in <c>pnlGeneralHardwareORION</c> and is
    /// Enabled for ANAN-200D, ANAN-7000DLE, ANAN-8000DLE, ANAN-G2, ANAN-G2-1K,
    /// and ANVELINA-PRO3. FALSE for Red Pitaya (Thetis disables the panel
    /// there), Hermes, ANAN-G2E, ANAN-10/10E/100/100B/100D, Metis, and HL2.
    /// Do NOT derive this from <see cref="HasAudioAmplifier"/>. mic_bias
    /// defaults OFF and is gated behind explicit operator confirmation on every
    /// bias-capable board (floating-connector RF / PTT-hang risk).</summary>
    bool HasMicBias = false,
    // ---- External-port control, Phase 5 -----------------------------------
    /// <summary>The RX auxiliary inputs (EXT1/EXT2/XVTR/BYPASS) this board's
    /// Alex / filter board exposes. <see cref="Contracts.RxAuxInputs.All"/> for
    /// the Alex-class P1 boards and the 0x0A Saturn family;
    /// <see cref="Contracts.RxAuxInputs.None"/> for Hermes-Lite 2 (the inputs do
    /// not physically exist). Gates the RX-aux selector in the UI/REST and, at
    /// the wire layer, whether the encoder emits any aux-relay bits. The
    /// <see cref="Contracts.RxAuxInputs.Bypass"/> (K36) bit is bit-identical to
    /// the PureSignal external-feedback select — PS owns it while armed
    /// (§3.4(2)).</summary>
    RxAuxInputs RxAuxInputs = RxAuxInputs.None,
    /// <summary>Board has a second RX antenna path (dual-Alex / dual-RX). True
    /// for the dual-ADC boards: ANAN-100D / 200D and the dual-RX 0x0A family.
    /// Gates the RX2-antenna selector.</summary>
    bool HasRx2AntennaPath = false,
    /// <summary>Board has the 4-bit user GPIO (<c>user_dig_out</c>) on its
    /// Protocol-1 register 0x0a (wire 0x14) C3[3:0] → MCP23008. Hermes-Lite 2
    /// only (verified Thetis-mi0bot networkproto1.c:774). Gates the HL2 user
    /// GPIO toggle group.</summary>
    bool HasHl2UserGpio = false,
    /// <summary>Alex / filter-board revision for the K36-BYPASS-direction
    /// branch (§3.4(4)). NOT wire-discoverable — defaults
    /// <see cref="Contracts.AlexRevision.Modern"/> (Rev 24+, PS routes to BYPASS,
    /// matching Zeus's current behaviour). Operator preference only.</summary>
    AlexRevision AlexRevision = AlexRevision.Modern)
{
    /// <summary>Safe defaults for an unrecognised / disconnected board.
    /// Single ADC, no extras — minimum-surprise capability set so a
    /// pre-connect UI doesn't show conditional panels for unknown
    /// hardware. <see cref="MaxPowerWatts"/> defaults to 100 W so the
    /// power meter has a usable axis range before the radio identifies
    /// itself.</summary>
    public static readonly BoardCapabilities UnknownDefaults = new(
        RxAdcCount: 1,
        MkiiBpf: false,
        AdcSupplyMv: 33,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: false,
        SupportsPathIllustrator: false,
        MaxPowerWatts: 100);
}
