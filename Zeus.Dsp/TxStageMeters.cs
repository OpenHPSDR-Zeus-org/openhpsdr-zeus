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

namespace Zeus.Dsp;

/// <summary>
/// Per-block TXA stage readings sampled from the WDSP metering ring. Level
/// fields (*Pk/*Av) are dBFS; gain-reduction fields (*Gr) are positive dB
/// of reduction (0 = no reduction, 6 = 6 dB cut). When TXA is not processing
/// (MOX off, or engine lacks TXA), all level fields are
/// <see cref="float.NegativeInfinity"/> and all gain-reduction fields are 0.
///
/// Both peak and average are captured for each active stage. The operator
/// uses average to judge level and peak to spot clipping-induced distortion
/// that hides inside the average window's ~100 ms smoothing. Indices tracked
/// per <c>native/wdsp/TXA.h:49-66</c> txaMeterType.
///
/// Sign convention for *Gr fields: WDSP returns <c>TXA_*_GAIN</c> as
/// <c>20*log10(linear_gain)</c>, which is ≤ 0 when the stage is reducing.
/// Callers must negate before storing here so downstream consumers see a
/// monotonic "how much are we cutting?" scale.
///
/// CFC / COMP readings will sit at the WDSP silence sentinel (≈ −400 dBFS)
/// until their stages are engaged; the frontend treats the sentinel as
/// "bypassed" (P1.4 sentinel handling). Leaving the fields in the record
/// keeps the DSP→wire pipeline uniform regardless of which stages are on.
/// </summary>
public readonly record struct TxStageMeters(
    float MicPk,
    float MicAv,
    float EqPk,
    float EqAv,
    float LvlrPk,
    float LvlrAv,
    float LvlrGr,
    float CfcPk,
    float CfcAv,
    float CfcGr,
    float CompPk,
    float CompAv,
    float AlcPk,
    float AlcAv,
    float AlcGr,
    float OutPk,
    float OutAv)
{
    public static readonly TxStageMeters Silent = new(
        MicPk: float.NegativeInfinity,
        MicAv: float.NegativeInfinity,
        EqPk: float.NegativeInfinity,
        EqAv: float.NegativeInfinity,
        LvlrPk: float.NegativeInfinity,
        LvlrAv: float.NegativeInfinity,
        LvlrGr: 0f,
        CfcPk: float.NegativeInfinity,
        CfcAv: float.NegativeInfinity,
        CfcGr: 0f,
        CompPk: float.NegativeInfinity,
        CompAv: float.NegativeInfinity,
        AlcPk: float.NegativeInfinity,
        AlcAv: float.NegativeInfinity,
        AlcGr: 0f,
        OutPk: float.NegativeInfinity,
        OutAv: float.NegativeInfinity);
}
