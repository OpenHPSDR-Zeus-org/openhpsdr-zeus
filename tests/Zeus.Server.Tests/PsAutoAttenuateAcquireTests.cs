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

namespace Zeus.Server.Tests;

/// <summary>
/// Unit coverage for the G2E "acquire-from-stall" rule
/// (<see cref="PsAutoAttenuateService.AcquireStepFromStall"/>): when calcc reports
/// no fit (feedback &lt;= 0) on the single-ADC G2E, auto-attenuate must walk byte 59
/// UP a little (never down) to bring a hot/clipped external tap into a fittable
/// range — the fix for "PS meter never moves on the G2E even with auto-attenuate
/// on" (#960). Scoped to HermesC10 so the working dual-ADC G2 and the 10E keep the
/// historical "wait" (byte-identical).
/// </summary>
public class PsAutoAttenuateAcquireTests
{
    [Fact]
    public void G2E_NoFit_BelowCeiling_WalksUpBySmallStep()
    {
        // feedback 0 (calcc could not fit) + room below max → add a little attenuation.
        int step = PsAutoAttenuateService.AcquireStepFromStall(
            HpsdrBoardKind.HermesC10, feedback: 0, currentAttnDb: 0);
        Assert.Equal(3, step);            // AcquireStepDb — the safe upward nudge
        Assert.True(step > 0, "must walk UP (more attenuation) to escape a hot coupler");
    }

    [Fact]
    public void G2E_NoFit_NearCeiling_ClampsToMax()
    {
        // One dB of headroom left (30→31) → step is clamped so we never exceed max.
        int step = PsAutoAttenuateService.AcquireStepFromStall(
            HpsdrBoardKind.HermesC10, feedback: 0, currentAttnDb: 30);
        Assert.Equal(1, step);
    }

    [Fact]
    public void G2E_NoFit_AtCeiling_Holds()
    {
        // At max attenuation and still no fit → no usable feedback (no tap / not
        // transmitting): hold, don't oscillate. The next real signal fits at max
        // and the tooQuiet dance walks it back down.
        int step = PsAutoAttenuateService.AcquireStepFromStall(
            HpsdrBoardKind.HermesC10, feedback: 0, currentAttnDb: 31);
        Assert.Equal(0, step);
    }

    [Fact]
    public void G2E_WithFit_DefersToNormalDance()
    {
        // Positive feedback → calcc fit → the normal up/down dance owns it, not acquire.
        int step = PsAutoAttenuateService.AcquireStepFromStall(
            HpsdrBoardKind.HermesC10, feedback: 200, currentAttnDb: 0);
        Assert.Equal(0, step);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.OrionMkII)]   // dual-ADC G2 — must stay byte-identical
    [InlineData(HpsdrBoardKind.HermesII)]    // 10E — deliberately unchanged (no bench)
    [InlineData(HpsdrBoardKind.Hermes)]
    public void NonG2E_NoFit_NeverAcquires(HpsdrBoardKind board)
    {
        // Every non-G2E board keeps the historical "wait" on a no-fit — the acquire
        // walk is scoped to HermesC10 so a working dual-ADC G2 PS is untouched.
        int step = PsAutoAttenuateService.AcquireStepFromStall(
            board, feedback: 0, currentAttnDb: 0);
        Assert.Equal(0, step);
    }
}
