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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

/// <summary>
/// Per-radio-model constants for TX forward / reflected power calibration and
/// the safe PA ceiling used for meter scaling. The power math is the same
/// across boards — <c>watts = volts² / bridge_volt</c> where
/// <c>volts = (adc − cal_offset) / 4095 · ref_voltage</c> — only the constants
/// differ. Thetis <c>console.cs:25053-25118</c> (computeAlexFwdPower) is the
/// authoritative reference.
/// </summary>
public sealed record RadioCalibration(
    double BridgeVolt,
    double RefVoltage,
    int AdcCalOffset,
    double MaxWatts)
{
    /// <summary>
    /// Hermes-Lite 2 defaults. Thetis <c>console.cs:25973-25977</c> uses
    /// <c>bridge_volt = 1.5</c> for HL2 specifically — its onboard RF detector
    /// has a very different transfer function from the classic Alex bridge
    /// (which is 0.09). Using the Alex value reads ~16× too high.
    /// MaxWatts is the 5 W PA rating — meter scaling only, not protection.
    /// </summary>
    public static readonly RadioCalibration HermesLite2 = new(
        BridgeVolt: 1.5,
        RefVoltage: 3.3,
        AdcCalOffset: 6,
        MaxWatts: 5.0);

    /// <summary>
    /// Hermes (board id 0x01) / Metis / Griffin / ANAN-10 / ANAN-10E
    /// fallback. Thetis' <c>computeAlexFwdPower</c> default branch
    /// (<c>console.cs:25095-25099</c>) when <c>HardwareSpecific.Model</c>
    /// doesn't match a more specific case: bridge 0.09, ref 3.3, offset 6.
    /// </summary>
    public static readonly RadioCalibration Hermes = new(
        BridgeVolt: 0.09,
        RefVoltage: 3.3,
        AdcCalOffset: 6,
        MaxWatts: 10.0);

    /// <summary>
    /// ANAN-100 / 100B / 100D (board id 0x04 = Angelia) — Thetis
    /// <c>console.cs:25063-25072</c>. Bridge 0.095, ref 3.3, offset 6.
    /// 6 m alternate bridge ignored here — meter calibration on 6 m is a
    /// Phase-3 detail; the issue body explicitly excluded per-band tweaks.
    /// </summary>
    public static readonly RadioCalibration Anan100 = new(
        BridgeVolt: 0.095,
        RefVoltage: 3.3,
        AdcCalOffset: 6,
        MaxWatts: 100.0);

    /// <summary>
    /// ANAN-200D (board id 0x05 = Orion) — Thetis
    /// <c>console.cs:25074-25078</c>. Bridge 0.108, ref 5.0, offset 4.
    /// </summary>
    public static readonly RadioCalibration Anan200 = new(
        BridgeVolt: 0.108,
        RefVoltage: 5.0,
        AdcCalOffset: 4,
        MaxWatts: 200.0);

    /// <summary>
    /// ANAN-7000DLE / ANAN-G1 / ANAN-G2 / ANAN-G2-1K / Anvelina Pro3 /
    /// RedPitaya — Thetis <c>console.cs:25079-25088</c>. Bridge 0.12,
    /// ref 5.0, offset 32. KB2UKA's test G2 MkII reports board id 0x0A
    /// (which Zeus collapses into <c>HpsdrBoardKind.OrionMkII</c>); the G2
    /// hardware uses these constants, not the Thetis "ORIONMKII" /
    /// ANAN-8000D bridge. See <see cref="OrionMkIIAnan8000"/> for the
    /// other bucket and the dispatch caveat in <c>RadioCalibrations.For</c>.
    /// </summary>
    public static readonly RadioCalibration OrionMkII = new(
        BridgeVolt: 0.12,
        RefVoltage: 5.0,
        AdcCalOffset: 32,
        MaxWatts: 100.0);

    /// <summary>
    /// ANAN-8000D / Thetis "ORIONMKII" enum — Thetis
    /// <c>console.cs:25089-25093</c>. Bridge 0.08, ref 5.0, offset 18.
    /// Board id 0x0A in <c>HpsdrBoardKind</c> aliases both ANAN-8000D and
    /// G2; the operator selects the variant via
    /// <see cref="OrionMkIIVariant"/> and <c>RadioCalibrations.For</c>
    /// dispatches to this bucket when <see cref="OrionMkIIVariant.Anan8000DLE"/>
    /// is chosen. Apache OrionMkII (original) shares these constants with a
    /// separate bucket (<see cref="OrionMkIIOriginal"/>) for the 100 W
    /// rated-watts override.
    /// </summary>
    public static readonly RadioCalibration OrionMkIIAnan8000 = new(
        BridgeVolt: 0.08,
        RefVoltage: 5.0,
        AdcCalOffset: 18,
        MaxWatts: 200.0);

    /// <summary>
    /// Apache OrionMkII (original 100 W radio, Orion-MkII firmware) —
    /// Thetis <c>console.cs:25089-25093</c>. Same bridge constants as
    /// <see cref="OrionMkIIAnan8000"/> but 100 W rated rather than 200 W.
    /// Selected via <see cref="OrionMkIIVariant.OrionMkII"/>.
    /// </summary>
    public static readonly RadioCalibration OrionMkIIOriginal = new(
        BridgeVolt: 0.08,
        RefVoltage: 5.0,
        AdcCalOffset: 18,
        MaxWatts: 100.0);

    /// <summary>
    /// ANAN-G2-1K (Saturn FPGA, 1 kW PA). Same bridge constants as
    /// <see cref="OrionMkII"/> but 1000 W rated for meter scaling.
    /// Selected via <see cref="OrionMkIIVariant.G2_1K"/>. G8NJJ noted in
    /// Thetis (<c>clsHardwareSpecific.cs:171</c>) that the 1K variant
    /// "likely needs further changes for PA" — the bridge constants here
    /// match G2 for parity until a real-radio operator dials them in.
    /// </summary>
    public static readonly RadioCalibration AnanG21K = new(
        BridgeVolt: 0.12,
        RefVoltage: 5.0,
        AdcCalOffset: 32,
        MaxWatts: 1000.0);
}
