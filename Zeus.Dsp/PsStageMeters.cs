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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

namespace Zeus.Dsp;

/// <summary>
/// PureSignal calcc-stage readings sampled from <c>GetPSInfo</c> and
/// <c>GetPSMaxTX</c>. Captured at the same 10 Hz cadence as the TX stage
/// meters but only emitted to the wire when PsEnabled is true.
/// </summary>
/// <param name="FeedbackLevel">info[4] — feedback envelope level, 0..256
/// raw. UI normalises to 0..1 via /256 for the bar.</param>
/// <param name="CalState">info[15] — cal-state enum: 0 RESET, 1 WAIT,
/// 2 MOXDELAY, 3 SETUP, 4 COLLECT, 5 MOXCHECK, 6 CALC, 7 DELAY, 8 STAYON,
/// 9 TURNON. Drives the cal-state badge in the UI.</param>
/// <param name="Correcting">info[14] != 0 — the iqc stage is actively
/// applying a correction curve.</param>
/// <param name="CorrectionDb">Derived metric: RMS of the calcc output curve
/// in dB. Zero when not correcting; useful as a "depth" indicator.</param>
/// <param name="MaxTxEnvelope"><c>GetPSMaxTX</c> — peak TX envelope
/// magnitude since the last reset. Used by auto-attenuate to know when to
/// step the attenuator down.</param>
/// <param name="CalibrationAttempts">info[5] — cumulative count of completed
/// calibration fits (calc() invocations that produced a result, regardless
/// of whether scheck accepted them). Thetis <c>PSForm.cs:1097-1099</c>
/// gates AutoAttenuate's <c>timer2code</c> on this counter incrementing
/// (<c>CalibrationAttemptsChanged</c>) — only step the attenuator after
/// calcc has finished a fit, otherwise the loop changes the envelope mid-
/// calc and cm jumps trigger info[6]=0x40 (cm changed too much) every
/// iteration, forcing perpetual LRESET.</param>
public readonly record struct PsStageMeters(
    float FeedbackLevel,
    byte CalState,
    bool Correcting,
    float CorrectionDb,
    float MaxTxEnvelope,
    int CalibrationAttempts)
{
    public static readonly PsStageMeters Silent = new(
        FeedbackLevel: 0f,
        CalState: 0,
        Correcting: false,
        CorrectionDb: 0f,
        MaxTxEnvelope: 0f,
        CalibrationAttempts: 0);
}
