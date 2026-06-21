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
/// Per-tick RXA stage readings sampled from the WDSP metering ring. Indices
/// match Thetis <c>rxaMeterType</c> (RXA.h:47-57): signal peak/avg, ADC
/// peak/avg, AGC gain, AGC envelope peak/avg.
///
/// Units (all uncalibrated — caller applies the per-board offset before
/// putting them on the wire):
/// <list type="bullet">
///   <item><c>SignalPk</c> / <c>SignalAv</c>: dBm, RXA_S_PK / RXA_S_AV</item>
///   <item><c>AdcPk</c> / <c>AdcAv</c>: dBFS, RXA_ADC_PK / RXA_ADC_AV</item>
///   <item><c>AgcGain</c>: signed dB (positive = AGC is boosting),
///         RXA_AGC_GAIN</item>
///   <item><c>AgcEnvPk</c> / <c>AgcEnvAv</c>: dBm, RXA_AGC_PK / RXA_AGC_AV</item>
/// </list>
///
/// Sign convention for <c>AgcGain</c> deliberately differs from
/// <see cref="TxStageMeters"/>'s <c>*Gr</c> fields. WDSP's RXA AGC genuinely
/// swings both ways: positive when boosting a weak signal, negative when
/// cutting a hot one. Storing the signed value preserves operator
/// information; flipping to a one-sided "reduction" scale would lose the
/// "AGC is boosting +30 dB on weak SSB" reading the operator wants.
///
/// Sentinel: when a meter index hasn't been ticked (channel state &lt; 1
/// or the WDSP worker thread hasn't run yet), <c>GetRXAMeter</c> returns
/// approximately −400 ("meter didn't run") — see
/// <c>docs/lessons/wdsp-init-gotchas.md</c>. The value passes through
/// unchanged into <see cref="RxStageMeters"/> and downstream into the
/// wire frame; the frontend treats values ≤ −200 as "bypassed".
/// </summary>
public readonly record struct RxStageMeters(
    float SignalPk,
    float SignalAv,
    float AdcPk,
    float AdcAv,
    float AgcGain,
    float AgcEnvPk,
    float AgcEnvAv)
{
    public static readonly RxStageMeters Silent = new(
        SignalPk: -200f,
        SignalAv: -200f,
        AdcPk: -200f,
        AdcAv: -200f,
        AgcGain: 0f,
        AgcEnvPk: -200f,
        AgcEnvAv: -200f);
}
