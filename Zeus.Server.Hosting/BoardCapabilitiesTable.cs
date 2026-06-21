// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

/// <summary>
/// Dispatch from <see cref="HpsdrBoardKind"/> to the per-board static
/// <see cref="BoardCapabilities"/> fingerprint. Mirrors
/// <see cref="RadioCalibrations"/>'s seam — every board fact that the
/// frontend needs to gate UI on (RX2 attenuator mode, audio amp,
/// volts/amps telemetry, etc.) flows through this helper.
///
/// Source: Thetis <c>clsHardwareSpecific.cs:85-803</c>, cross-referenced
/// in <c>docs/references/protocol-1/thetis-board-matrix.md</c>.
///
/// The 0x0A wire collision (OrionMkII / 7000DLE / 8000DLE / G2 / G2-1K /
/// ANVELINA-PRO3 / Red Pitaya all share a single byte) is handled here by
/// picking the most common variant's facts (G2-class). Once the operator
/// override from issue #218 lands the dispatch will fan out per variant.
/// </summary>
internal static class BoardCapabilitiesTable
{
    /// <summary>
    /// Look up the static capability fingerprint for a connected board.
    /// Falls back to <see cref="BoardCapabilities.UnknownDefaults"/> for
    /// any future enum value that hasn't been wired yet.
    /// </summary>
    public static BoardCapabilities For(HpsdrBoardKind board) =>
        For(board, OrionMkIIVariant.G2);

    /// <summary>
    /// Variant-aware overload — when <paramref name="board"/> is
    /// <see cref="HpsdrBoardKind.OrionMkII"/>, the variant selects the
    /// matching capability fingerprint. The Apache OrionMkII original
    /// (<see cref="OrionMkIIVariant.OrionMkII"/>) lacks volts/amps/audio-amp
    /// telemetry per <c>clsHardwareSpecific.cs:249-262, 459-468</c>; every
    /// other 0x0A variant shares the Saturn-class Saturn fingerprint; G2
    /// variants additionally advertise the bench-confirmed 1536 kHz P2 RX/DDC
    /// ceiling instead of the conservative 384 kHz fallback.
    /// </summary>
    public static BoardCapabilities For(HpsdrBoardKind board, OrionMkIIVariant variant) => board switch
    {
        // --- Hermes-class single-RX, Alex-class BPF, Hermes-side L/R swap ---
        // Thetis clsHardwareSpecific.cs:87-121 sets RxADC=1, MKIIBPF=0,
        // ADCSupply=33, LRAudioSwap=1 for HERMES / ANAN10 / ANAN10E /
        // ANAN100 / ANAN100B. Path Illustrator supported (line 773-780
        // excludes only the high-power MkII family).
        HpsdrBoardKind.Metis      => HermesClass,
        HpsdrBoardKind.Hermes     => HermesClass,
        // ANAN-10E (HermesII firmware): ~30 W output, so it gets its own
        // capability snapshot with the larger meter axis.
        HpsdrBoardKind.HermesII    => HermesIIClass,
        // --- ANAN-100D: dual-ADC Hermes-supply ---
        // clsHardwareSpecific.cs:122-128 — first DDC family entrant.
        HpsdrBoardKind.Angelia    => Angelia,
        // --- ANAN-200D: dual-ADC, 50 mV supply ---
        // clsHardwareSpecific.cs:136-142 — first 50 mV / high-power board.
        HpsdrBoardKind.Orion      => Orion,
        // --- HermesLite2 (mi0bot) ---
        // Thetis MW0LGE leaves HL2 unconfigured; Zeus has its own HL2 path
        // (docs/lessons/wdsp-init-gotchas.md, hl2-drive-model.md). Single
        // RX, no Alex, no telemetry, no path illustrator.
        HpsdrBoardKind.HermesLite2 => HermesLite2,
        // --- 0x0A family ---
        // Operator-selected variant (issue #218) routes to the matching
        // Saturn vs Apache-OrionMkII-original fingerprint. The 8000DLE and
        // G2-1K variants additionally need a higher MaxPowerWatts than the
        // 120 W Saturn baseline, so they fan out here too.
        HpsdrBoardKind.OrionMkII  => variant switch
        {
            OrionMkIIVariant.OrionMkII    => OrionMkIIOriginal,
            OrionMkIIVariant.Anan8000DLE  => Saturn8000DLE,
            OrionMkIIVariant.G2           => SaturnG2,
            OrionMkIIVariant.G2_1K        => SaturnG2_1K,
            // Anvelina-PRO3 — Saturn-class facts plus the EU2AV DX OC
            // extension flag (issue #407). Every other Saturn variant
            // keeps SupportsAnvelinaDxOc=false so the wire path stays
            // closed on G2 / 7000DLE / Apache OrionMkII original / etc.
            OrionMkIIVariant.AnvelinaPro3 => SaturnAnvelinaPro3,
            // Red Pitaya — Saturn-class hardware but Thetis DISABLES the Orion
            // mic-bias panel there (HasMicBias=false) and it has no balanced XLR.
            OrionMkIIVariant.RedPitaya    => SaturnRedPitaya,
            // 7000DLE and the default 0x0A fall to the Saturn baseline: analog
            // line-in + Orion mic bias, NO balanced XLR (G2 / G2-1K only).
            _                             => Saturn,
        },
        // --- ANAN-G2E (HermesC10, N1GP) ---
        // clsHardwareSpecific.cs:129-135. Hybrid: single RX + 33 mV supply
        // (Hermes-class) BUT MKII BPF on + LR-swap off + telemetry +
        // audio amp (Saturn-class). One of the two odd boards.
        HpsdrBoardKind.HermesC10  => HermesC10,
        // Unknown / future enum value — safe defaults.
        _                          => BoardCapabilities.UnknownDefaults,
    };

    // Hermes / Metis / ANAN-10 — small-signal Hermes-class single-RX. ~10 W.
    // Brick2 SDR (Sergey EU1SW) announces as wire byte 0x01 → HpsdrBoardKind.Hermes
    // and inherits this fingerprint. Brick2's hardware reality (1 ADC, no Alex
    // BPF, no Apollo LPF, no on-board telemetry, internal step attenuator
    // 0-31 dB on RX1 only) matches the row exactly; see Zeus issue #171.
    // MaxPowerWatts=10 is conservative for Brick2 (rated 15 W); operator
    // overrides per-rig in the PA panel.
    private static readonly BoardCapabilities HermesClass = new(
        RxAdcCount: 1,
        MkiiBpf: false,
        AdcSupplyMv: 33,
        LrAudioSwap: true,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: false, // single-RX: RX2 doesn't exist
        SupportsPathIllustrator: true,
        MaxPowerWatts: 10,
        // Hermes-class boards carry the Alex/filter RX-antenna relays (C3[7:5])
        // and the full EXT1/EXT2/XVTR/BYPASS aux set — antenna slice (#804). No
        // TX-antenna relay (P1, ANT1-hardwired on transmit); single RX → no RX2
        // antenna path.
        HasRxAntennaRelays: true,
        RxAuxInputs: RxAuxInputs.All,
        // Hermes-class codec board: radio mic jack selectable. No line-in / XLR
        // / mic-bias surface (Hermes/ANAN-10/100 line-in is scoped OUT).
        HasOnboardCodec: true);

    // ANAN-10E (HermesII firmware) — same Hermes-class fingerprint but the
    // 10E hardware is rated to ~30 W, so its meter axis gets the bigger top.
    private static readonly BoardCapabilities HermesIIClass = new(
        RxAdcCount: 1,
        MkiiBpf: false,
        AdcSupplyMv: 33,
        LrAudioSwap: true,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: false,
        SupportsPathIllustrator: true,
        MaxPowerWatts: 30,
        HasRxAntennaRelays: true,
        RxAuxInputs: RxAuxInputs.All,
        // ANAN-10E (HermesII): codec + analog line-in jack (issue #667 fix is
        // HermesII-only; Hermes/ANAN-10/100 line-in stays scoped OUT). No XLR /
        // mic-bias.
        HasOnboardCodec: true,
        HasRadioLineIn: true);

    // ANAN-100D — 100 W class. Meter axis 120 W gives some headroom past
    // the rated rail without truncating PEP overshoots.
    private static readonly BoardCapabilities Angelia = new(
        RxAdcCount: 2,
        MkiiBpf: false,
        AdcSupplyMv: 33,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: true,
        SupportsPathIllustrator: true,
        MaxPowerWatts: 120,
        // Dual-ADC DDC board: RX-antenna relays + full aux set + a dedicated RX2
        // antenna path — antenna slice (#804). P1, so no TX-antenna relay.
        HasRxAntennaRelays: true,
        RxAuxInputs: RxAuxInputs.All,
        HasRx2AntennaPath: true,
        // ANAN-100D: codec + analog line-in jack + Orion mic bias. No XLR.
        HasOnboardCodec: true,
        HasRadioLineIn: true,
        HasMicBias: true);

    // ANAN-200D — 200 W class but axis matches the 100/200/G2 family at
    // 120 W: half-axis at 100 W is the natural reading point.
    private static readonly BoardCapabilities Orion = new(
        RxAdcCount: 2,
        MkiiBpf: false,
        AdcSupplyMv: 50,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: true,
        SupportsPathIllustrator: true,
        MaxPowerWatts: 120,
        HasRxAntennaRelays: true,
        RxAuxInputs: RxAuxInputs.All,
        HasRx2AntennaPath: true,
        // ANAN-200D (Orion): codec + analog line-in jack + Orion mic bias
        // (Thetis ORION bias panel Enabled). No balanced XLR.
        HasOnboardCodec: true,
        HasRadioLineIn: true,
        HasMicBias: true);

    // 0x0A family conservative baseline (7000DLE / 8000DLE / OrionMkII /
    // ANVELINA-PRO3 / Red Pitaya). Saturn-class facts. 100–200 W typical →
    // 120 W axis. Keep MaxRxSampleRateHz at the safe 384 kHz baseline here;
    // explicit G2 variants below unlock the 1536 kHz P2 DDC ladder.
    private static readonly BoardCapabilities Saturn = new(
        RxAdcCount: 2,
        MkiiBpf: true,
        AdcSupplyMv: 50,
        LrAudioSwap: false,
        HasVolts: true,
        HasAmps: true,
        HasAudioAmplifier: true,
        HasSteppedAttenuationRx2: true,
        SupportsPathIllustrator: false,
        MaxPowerWatts: 120,
        // 0x0A / Saturn family: switchable TX antenna relays (alex0[26:24], P2),
        // RX-antenna relays, full EXT1/EXT2/XVTR/BYPASS aux set, and an RX2
        // antenna path — antenna slice (#804). All Saturn variants ('with'
        // derivations below) inherit these.
        HasTxAntennaRelays: true,
        HasRxAntennaRelays: true,
        RxAuxInputs: RxAuxInputs.All,
        HasRx2AntennaPath: true,
        // 0x0A Saturn baseline (7000DLE / 8000DLE / default): codec + analog
        // line-in + Orion mic bias. NO balanced XLR — that is G2 / G2-1K only.
        HasOnboardCodec: true,
        HasRadioLineIn: true,
        HasMicBias: true);

    // ANAN-G2 — local Thetis parity notes pin RX1/RX2 at
    // 48/96/192/384/768/1536 kHz over Protocol 2. Saturn-FPGA G2 / G2-1K
    // additionally carry the switchable balanced-XLR mic input.
    private static readonly BoardCapabilities SaturnG2 = Saturn with
    {
        MaxRxSampleRateHz = 1_536_000,
        SupportsG2AdcOptions = true,
        HasBalancedXlr = true,
    };

    // ANAN-8000DLE — 250 W per Apache spec; axis snaps to that. Saturn-class
    // audio (line-in + mic bias), NO balanced XLR (G2 / G2-1K only).
    private static readonly BoardCapabilities Saturn8000DLE = Saturn with
    {
        MaxPowerWatts = 250,
    };

    // Red Pitaya (DH1KLM) — Saturn-class hardware but Thetis DISABLES the Orion
    // mic-bias panel for it (DIY connector foot-gun) and it has no balanced XLR.
    // Analog line-in only.
    private static readonly BoardCapabilities SaturnRedPitaya = Saturn with
    {
        HasMicBias = false,
    };

    // ANAN-G2-1K — kilowatt-class with internal 1 kW PA. Same Saturn fingerprint
    // but the meter axis needs the room.
    private static readonly BoardCapabilities SaturnG2_1K = SaturnG2 with
    {
        MaxPowerWatts = 1000,
    };

    // ANVELINA-PRO3 (EU2AV). Saturn-class fingerprint plus the DX OC
    // extension (USEROUT7..10) wired into P2 byte 1397 bits [4:1] —
    // issue #407, EU2AV's Open_Collector_Anvelina_DX for Thetis spec.
    // This is the only variant where SupportsAnvelinaDxOc flips true.
    // ANVELINA-PRO3 (EU2AV) — Saturn-class plus DX OC. Thetis disables the Orion
    // mic-bias panel here (DIY connector), so HasMicBias is forced false; no XLR.
    private static readonly BoardCapabilities SaturnAnvelinaPro3 = Saturn with
    {
        SupportsAnvelinaDxOc = true,
        HasMicBias = false,
    };

    private static readonly BoardCapabilities HermesC10 = new(
        RxAdcCount: 1,
        MkiiBpf: true,
        AdcSupplyMv: 33,
        LrAudioSwap: false,
        HasVolts: true,
        HasAmps: true,
        HasAudioAmplifier: true,
        HasSteppedAttenuationRx2: false, // single-RX: RX2 doesn't exist
        SupportsPathIllustrator: false,
        MaxPowerWatts: 120,
        // ANAN-G2E (HermesC10) runs Protocol 1 with the MkII BPF board: RX-antenna
        // relays + full aux set, but ANT1-hardwired on transmit (no P2 Alex word)
        // and single-RX (no RX2 antenna path) — antenna slice (#804).
        HasRxAntennaRelays: true,
        RxAuxInputs: RxAuxInputs.All,
        // ANAN-G2E: codec board (radio mic). No line-in / XLR (Thetis hard-gates
        // balanced to G2 / G2-1K and excludes G2E) / mic-bias surface in v1.
        HasOnboardCodec: true);

    // Apache OrionMkII original (Orion-MkII firmware, 100 W) — Saturn-class
    // hardware fingerprint but without on-board telemetry / audio amp per
    // clsHardwareSpecific.cs:249-262 (HasVolts/Amps lists exclude
    // ORIONMKII) and :459-468 (HasAudioAmplifier excludes it too).
    private static readonly BoardCapabilities OrionMkIIOriginal = new(
        RxAdcCount: 2,
        MkiiBpf: true,
        AdcSupplyMv: 50,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: true,
        SupportsPathIllustrator: false,
        MaxPowerWatts: 120,
        // Apache OrionMkII original is a 0x0A / P2 board: TX + RX antenna relays,
        // full aux set, RX2 antenna path — antenna slice (#804). Same antenna
        // semantics as the Saturn baseline; only the telemetry/audio-amp facts
        // differ.
        HasTxAntennaRelays: true,
        HasRxAntennaRelays: true,
        RxAuxInputs: RxAuxInputs.All,
        HasRx2AntennaPath: true,
        // Apache OrionMkII original: codec + analog line-in jack, NO balanced
        // XLR (Saturn-FPGA G2 / G2-1K only) and HasMicBias stays FALSE per
        // clsHardwareSpecific (ORIONMKII excluded from the bias panel).
        HasOnboardCodec: true,
        HasRadioLineIn: true);

    // HermesLite2 — rated 5 W stock but operators routinely run to 10 W with
    // adequate cooling, so the meter axis is 10 W to cover the realistic
    // operating range without leaving the bar visually pegged at half scale.
    //
    // HasHl2OptionalToggles is true here only — HL2 is the only board that
    // exposes the mi0bot fork's HL2-specific Protocol-1 toggles (Band Volts
    // PWM on the FAN connector, future siblings). The frontend uses this to
    // gate the HL2 settings panel so the controls don't appear for boards
    // that would silently ignore them. Issue #279.
    private static readonly BoardCapabilities HermesLite2 = new(
        RxAdcCount: 1,
        MkiiBpf: false,
        AdcSupplyMv: 33,
        LrAudioSwap: false,
        HasVolts: false,
        HasAmps: false,
        HasAudioAmplifier: false,
        HasSteppedAttenuationRx2: false,
        SupportsPathIllustrator: false,
        MaxPowerWatts: 10,
        HasHl2OptionalToggles: true,
        // HL2 has a SINGLE antenna jack forwarding to the N2ADR pad: NO RX
        // antenna relays, NO TX antenna relays, NO aux inputs — antenna slice
        // (#804). Explicit (not just defaulted) because the wire-layer clamp in
        // ControlFrame.EncodeRxAntennaC3Bits depends on this being false: a stale
        // per-band ANT2/3 (shared band rows are board-agnostic) must collapse to
        // ANT1 so the N2ADR pad never flips.
        HasTxAntennaRelays: false,
        HasRxAntennaRelays: false,
        RxAuxInputs: RxAuxInputs.None,
        HasRx2AntennaPath: false,
        // HL2 has no stream codec, so HasOnboardCodec stays false — the Radio
        // Mic / Line-In / XLR options are gated OFF. It DOES have a 0x14 mic
        // front-end, exposed only as inert plumbing in v1 (default Host); the
        // frontend surfaces nothing for it until confirmed on hardware.
        HermesLite2MicFrontEnd: true);
}
