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
    bool HasHl2OptionalToggles = false)
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
