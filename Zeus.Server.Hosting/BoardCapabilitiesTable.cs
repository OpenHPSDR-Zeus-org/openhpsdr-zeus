// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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
    /// other 0x0A variant shares the Saturn-class Saturn fingerprint.
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
            OrionMkIIVariant.OrionMkII   => OrionMkIIOriginal,
            OrionMkIIVariant.Anan8000DLE => Saturn8000DLE,
            OrionMkIIVariant.G2_1K       => SaturnG2_1K,
            _                            => Saturn,
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
        MaxPowerWatts: 10);

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
        MaxPowerWatts: 30);

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
        MaxPowerWatts: 120);

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
        MaxPowerWatts: 120);

    // 0x0A family default (G2 / 7000DLE / OrionMkII / ANVELINA-PRO3 /
    // Red Pitaya). Saturn-class facts. 100–200 W typical → 120 W axis.
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
        MaxPowerWatts: 120);

    // ANAN-8000DLE — 250 W per Apache spec; axis snaps to that.
    private static readonly BoardCapabilities Saturn8000DLE = Saturn with
    {
        MaxPowerWatts = 250,
    };

    // ANAN-G2-1K — kilowatt-class with internal 1 kW PA. Same Saturn fingerprint
    // but the meter axis needs the room.
    private static readonly BoardCapabilities SaturnG2_1K = Saturn with
    {
        MaxPowerWatts = 1000,
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
        MaxPowerWatts: 120);

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
        MaxPowerWatts: 120);

    // HermesLite2 — rated 5 W stock but operators routinely run to 10 W with
    // adequate cooling, so the meter axis is 10 W to cover the realistic
    // operating range without leaving the bar visually pegged at half scale.
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
        MaxPowerWatts: 10);
}
