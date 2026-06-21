// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

// CW-mode dial/LO offset helpers.
//
// In CW the operator's dial reads the carrier of the signal they're
// listening to; the radio LO is tuned cw_pitch below that for CWU and
// cw_pitch above for CWL so the carrier lands on the +cw_pitch / -cw_pitch
// audio passband WDSP filters around (FilterPresets.Cwu/Cwl). This is
// Thetis's convention (console.cs:31845-31858, the click-tune-display path).
//
// Without this offset the WDSP filter at +475..+725 Hz audio sits 600 Hz
// to the right of the dial in spectrum coordinates, which is what Zeus
// did up to this commit — and why operators had to manually tune
// 600 Hz off the signal to hear it.
internal static class CwOffset
{
    public const int CwPitchHz = CwDefaults.PitchHz;

    // Effective hardware LO for the supplied dial frequency. Non-CW modes
    // pass through unchanged so SSB / AM / FM / DIG behaviour is bit-for-bit
    // identical to before this seam was added.
    public static long EffectiveLoHz(RxMode mode, long vfoHz) => mode switch
    {
        RxMode.CWU => vfoHz - CwPitchHz,
        RxMode.CWL => vfoHz + CwPitchHz,
        _ => vfoHz,
    };

    public static long EffectiveLoHz(StateDto state) => EffectiveLoHz(state.Mode, state.VfoHz);

    // Dial-bump on mode transitions, matching Thetis console.cs
    // SetRX1Mode (lines 33982-34298). The intent: when an operator
    // flips USB↔CWU on the same physical signal, the LO doesn't move
    // (dial absorbs the cw_pitch step). Within CWU↔CWL the dial stays
    // put (operator wants the same dial number to mean "this side of the
    // carrier" before and after the sideband flip), accepting a 2× pitch
    // LO jump.
    public static long DialBumpForModeTransition(RxMode oldMode, RxMode newMode)
    {
        if (oldMode == newMode) return 0;

        if (newMode == RxMode.CWU)
        {
            if (oldMode == RxMode.LSB) return -CwPitchHz;
            if (oldMode == RxMode.CWL) return 0;
            return +CwPitchHz;
        }
        if (newMode == RxMode.CWL)
        {
            if (oldMode == RxMode.USB) return +CwPitchHz;
            if (oldMode == RxMode.CWU) return 0;
            return -CwPitchHz;
        }
        if (oldMode == RxMode.CWU)
        {
            if (newMode == RxMode.LSB) return +CwPitchHz;
            if (newMode == RxMode.CWL) return 0;
            return -CwPitchHz;
        }
        if (oldMode == RxMode.CWL)
        {
            if (newMode == RxMode.USB) return -CwPitchHz;
            if (newMode == RxMode.CWU) return 0;
            return +CwPitchHz;
        }
        return 0;
    }
}
