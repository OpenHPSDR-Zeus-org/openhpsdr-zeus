// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Contracts;

/// <summary>
/// The set of auxiliary RX inputs a board's Alex / filter board exposes
/// (external-ports plan — antenna slice, issue #804). A <c>[Flags]</c> set
/// because a board advertises which aux feeds are wired; the per-band
/// operator selection picks exactly one (see the server-side
/// <c>RxAuxInputSel</c> single-choice enum). <see cref="All"/> is the full
/// ANAN / Saturn-BPF set; <see cref="None"/> is Hermes-Lite 2 (single jack,
/// no aux relays).
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
    /// <summary>Maximum supported RX/DDC sample rate in Hz for this board
    /// capability fingerprint. Protocol 1 still caps at 384 kHz, but
    /// Protocol 2 G2-class radios can expose the full 48..1536 kHz ladder
    /// when the hardware is known to support it. Unknown / conservative
    /// boards report 384 kHz so persisted or UI-driven wideband requests do
    /// not overrun hardware that has not been bench-confirmed.</summary>
    int MaxRxSampleRateHz = 384_000,
    /// <summary>True when the board exposes the HL2-only optional toggles
    /// surfaced by <c>/api/radio/hl2-options</c> (Band Volts PWM enable,
    /// future mi0bot HL2 toggles). The frontend gates the HL2 settings panel
    /// on this flag so the controls don't appear for boards that ignore
    /// them. True for <see cref="HpsdrBoardKind.HermesLite2"/> only — Square
    /// SDR ships HL2-class firmware so it inherits via the same enum value.
    /// Issue #279.</summary>
    bool HasHl2OptionalToggles = false,
    /// <summary>True when the board exposes the ANAN-G2/Saturn ADC dither
    /// and digital-output randomizer controls in Protocol-2 CmdRx bytes 5/6.
    /// The frontend gates the G2 radio-options panel on this flag so the
    /// controls appear only for verified G2-class variants that use the
    /// Thetis-compatible Saturn receive-specific packet layout.</summary>
    bool SupportsG2AdcOptions = false,
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
    // ---- External antenna ports (external-ports plan — antenna slice, #804) --
    /// <summary>True when the board has switchable TX antenna relays (ANT1/2/3).
    /// Two wire paths: the 0x0A / Saturn OrionMkII family (G2 / G2-1K / 7000DLE /
    /// 8000DLE / Apache OrionMkII original / ANVELINA-PRO3 / Red Pitaya) emit the
    /// alex0[26:24] TX-antenna bits over Protocol 2; the full-Alex dual-ADC ANAN
    /// boards (ANAN-100D / ANAN-200D) emit them over Protocol 1 on Config-frame
    /// C4[1:0] (Thetis networkproto1.c:463-468; external-port parity audit,
    /// GAP-P1-1). Every other board is ANT1-hardwired on transmit (Hermes / Metis
    /// / ANAN-10 / ANAN-10E — no full Alex; ANAN-G2E — hardwired; Hermes-Lite 2 —
    /// single jack). The frontend gates the TX-antenna selector on this flag; the
    /// server returns 409 to a non-ANT1 TX request when it is false. The P1 wire
    /// encode + clamp live in ControlFrame.EncodeTxAntennaC4Bits /
    /// P1BoardHasTxAntennaRelays, which MUST stay in step with the per-board
    /// values here. External-ports plan — antenna slice (issue #804).</summary>
    bool HasTxAntennaRelays = false,
    /// <summary>True when the board has switchable RX antenna relays (ANT1/2/3).
    /// Every ANAN / Hermes-class Protocol-1 board does (RX-antenna rides
    /// Config-frame C3[7:5]); the 0x0A family does on the Alex word. FALSE only
    /// for Hermes-Lite 2 — its single antenna jack forwards to the N2ADR pad and
    /// is clamped to ANT1 at the wire layer
    /// (<c>ControlFrame.EncodeRxAntennaC3Bits</c>). The server returns 409 to a
    /// non-ANT1 RX request when false. External-ports plan — antenna slice
    /// (issue #804).</summary>
    bool HasRxAntennaRelays = false,
    /// <summary>The auxiliary RX inputs the board's Alex / filter board exposes
    /// (EXT1/EXT2/XVTR/BYPASS-K36). <see cref="Contracts.RxAuxInputs.All"/> for
    /// the ANAN / Saturn family; <see cref="Contracts.RxAuxInputs.None"/> for
    /// Hermes-Lite 2. The BYPASS (K36) bit is the SAME alex0 bit (11) PureSignal
    /// external feedback uses — the wire path ORs PS routing AFTER the operator
    /// aux so PS always wins while armed (the PS-K36 firewall). External-ports
    /// plan — antenna slice (issue #804).</summary>
    RxAuxInputs RxAuxInputs = RxAuxInputs.None,
    /// <summary>True when the board has a dedicated RX2 antenna path (the
    /// dual-ADC DDC family: ANAN-100D / ANAN-200D and the 0x0A Saturn family).
    /// Informational for the frontend; the antenna wire path itself is RX1 /
    /// shared-relay only in this slice. External-ports plan — antenna slice
    /// (issue #804).</summary>
    bool HasRx2AntennaPath = false,
    // ---- TX audio front-end (external-audio-jacks re-port) ----------------
    /// <summary>Board decodes the host→radio audio STREAM (TLV320 codec) — the
    /// path the radio's own analog mic / line-in jack uses. True for Hermes-
    /// class and every ANAN board. False for Hermes-Lite 2, which has no stream
    /// codec. STREAM codec only; does NOT gate the HL2 mic front-end (see
    /// <see cref="HermesLite2MicFrontEnd"/>). Gates the Radio Mic / Line-In /
    /// XLR options on the wire and in the UI.</summary>
    bool HasOnboardCodec = false,
    /// <summary>Hermes-Lite 2 has an analog mic front-end on Protocol-1
    /// register 0x0a (wire byte 0x14): mic_trs, mic_bias and a 5-bit
    /// line_in_gain. Distinct from <see cref="HasOnboardCodec"/> — HL2 has the
    /// mic front-end but not the stream codec. Kept INERT in v1 (plumbing only)
    /// until confirmed on hardware; mic_bias defaults OFF. True for HL2 only.</summary>
    bool HermesLite2MicFrontEnd = false,
    /// <summary>Board has a switchable analog line-in jack as a selectable
    /// TX-audio source (<see cref="Contracts.TxAudioSource.RadioLineIn"/>). True
    /// for the ANAN-10E (HermesII), 100D/200D and the 0x0A Saturn family.</summary>
    bool HasRadioLineIn = false,
    /// <summary>Board has a switchable balanced XLR microphone input
    /// (<see cref="Contracts.TxAudioSource.RadioBalancedXlr"/>). True ONLY for
    /// the Saturn-FPGA G2 / G2-1K. Use this flag (never
    /// <see cref="HasAudioAmplifier"/>) to gate XLR.</summary>
    bool HasBalancedXlr = false,
    /// <summary>Board can enable the Orion electret mic-bias supply on its mic
    /// jack. Do NOT derive this from <see cref="HasAudioAmplifier"/>. mic_bias
    /// defaults OFF (a floating connector with bias can hang PTT). True for
    /// 100D/200D/7000DLE/8000DLE/G2/G2-1K.</summary>
    bool HasMicBias = false,
    /// <summary>Hermes-Lite 2 exposes a 4-bit user GPIO (<c>user_dig_out</c>)
    /// on the IO/DB9 connector, driven over the Protocol-1 register 0x0a
    /// (wire 0x14) frame C3[3:0] → MCP23008 (Thetis-mi0bot
    /// <c>networkproto1.c</c> case 11; ramdor Thetis identical). The frontend
    /// gates the User-GPIO card and the <c>/api/radio/hl2-gpio</c> endpoint
    /// returns 409 on a non-HL2 board. True for Hermes-Lite 2 only. Default 0 →
    /// byte-identical to a board that never drives the lines. External-port
    /// parity audit (re-port of external-ports plan Phase 5).</summary>
    bool HasHl2UserGpio = false)
{
    /// <summary>Safe defaults for an unrecognised / disconnected board.
    /// Single ADC, no extras — minimum-surprise capability set so a
    /// pre-connect UI doesn't show conditional panels for unknown
    /// hardware. <see cref="MaxPowerWatts"/> defaults to 100 W so the
    /// power meter has a usable axis range before the radio identifies
    /// itself, and <see cref="MaxRxSampleRateHz"/> defaults to 384 kHz so
    /// P2-only wideband rates stay locked until the board is identified.</summary>
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
        MaxPowerWatts: 100,
        MaxRxSampleRateHz: 384_000);
}
