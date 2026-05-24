// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// CwEngine — host-side CW IQ generator. The encoder's correctness is
// covered by MorseEncoderTests; this file covers the *render path*: that
// the engine produces the right number of samples for a given message at
// a given WPM, and that the carrier sideband flips between CWU and CWL.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class CwEngineTests
{
    private const int Sr = CwEngine.SampleRateHz;   // 48 000

    // RenderForTest takes a SIGNED baseband Hz that matches the engine's
    // live (RadioLoHz − VfoHz) computation. The sign reflects the HL2's
    // "I − jQ" IQ convention: positive baseband puts the carrier BELOW the
    // LO, negative baseband puts it above. So for CWU without CTUN the
    // RadioService programs LO = VFO − 600, giving LO − VFO = −600 (carrier
    // at LO + 600 = VFO). For CWL the LO is VFO + 600, giving +600 (carrier
    // at LO − 600 = VFO).
    private const int CwuNoCtun = -600;
    private const int CwlNoCtun = +600;

    [Fact]
    public void Render_E_At20Wpm_ProducesOneDitOfSamples()
    {
        // 'E' = dit. At 20 WPM the PARIS unit is 1200/20 = 60 ms.
        // No leading or trailing inter-char gap (single char, single
        // character of buffer), so the IQ stream is exactly 60 ms long.
        var iq = CwEngine.RenderForTest("E", wpm: 20, basebandHz: CwuNoCtun);

        int expectedSamples = 60 * Sr / 1000;     // 2880 samples
        Assert.Equal(2 * expectedSamples, iq.Length);
    }

    [Fact]
    public void Render_T_At20Wpm_ProducesOneDahOfSamples()
    {
        // 'T' = dah. 3 units × 60 ms = 180 ms.
        var iq = CwEngine.RenderForTest("T", wpm: 20, basebandHz: CwuNoCtun);

        int expectedSamples = 180 * Sr / 1000;    // 8640
        Assert.Equal(2 * expectedSamples, iq.Length);
    }

    [Fact]
    public void Render_CQ_At20Wpm_ProducesExpectedTotalDuration()
    {
        // 'CQ' at 20 WPM:
        //   C = -.-.  → 3 + 1 + 1 + 1 + 3 + 1 + 1 = 11 units
        //   inter-char gap                          =  3 units
        //   Q = --.-  → 3 + 1 + 3 + 1 + 1 + 1 + 3 = 13 units
        //   ------------------------------------------
        //   total                                  = 27 units = 1620 ms
        var iq = CwEngine.RenderForTest("CQ", wpm: 20, basebandHz: CwuNoCtun);

        int expectedSamples = 1620 * Sr / 1000;   // 77 760
        Assert.Equal(2 * expectedSamples, iq.Length);
    }

    [Fact]
    public void Render_CWU_vs_CWL_FlipsQSign_OnTheCarrier()
    {
        // The baseband sign distinguishes the sideband: CWU without CTUN
        // emits +600 Hz, CWL emits -600 Hz. At the carrier peak (envelope =
        // 1, phase past the ramp), I[n]/Q[n] for CWU and CWL should have
        // the same I value and equal-and-opposite Q.
        var u = CwEngine.RenderForTest("T", wpm: 20, basebandHz: CwuNoCtun);
        var l = CwEngine.RenderForTest("T", wpm: 20, basebandHz: CwlNoCtun);

        Assert.Equal(u.Length, l.Length);

        // Sample at the midpoint of the dah — well past the 5 ms ramp,
        // envelope is 1.0, so I/Q reflect the raw carrier.
        int sampleIdx = (u.Length / 2) & ~1;   // even (I-position)
        float iU = u[sampleIdx], qU = u[sampleIdx + 1];
        float iL = l[sampleIdx], qL = l[sampleIdx + 1];

        // I matches because cos(±θ) = cos(θ).
        Assert.Equal(iU, iL, precision: 4);
        // Q is opposite-signed: sin(-θ) = -sin(θ).
        Assert.Equal(qU, -qL, precision: 4);
        // And the carrier has real amplitude (sanity: not all zero).
        Assert.True(Math.Abs(iU) > 0.5f || Math.Abs(qU) > 0.5f,
            $"expected envelope near 1.0 at sample {sampleIdx}, got I={iU} Q={qU}");
    }

    [Fact]
    public void Render_CtunOffset_ShiftsCarrierBaseband()
    {
        // Regression for the 2026-05-24 EA5IUE bench-test bug: with CTUN
        // active the HL2 LO sits independent of the dial, and the engine
        // must compensate so the carrier still lands at the operator's
        // dial freq. Verify by comparing two renders that differ only in
        // baseband Hz — the carrier-phase rate at the midpoint of the dah
        // should match the supplied frequency.
        var slow = CwEngine.RenderForTest("T", wpm: 20, basebandHz: 600);
        var fast = CwEngine.RenderForTest("T", wpm: 20, basebandHz: 9600);

        Assert.Equal(slow.Length, fast.Length);

        // Count zero-crossings of I[n] in the steady-state region of the
        // dah (skip the 5 ms ramp at start and end). At 600 Hz over an
        // 80 ms window we expect ~48 crossings; at 9600 Hz, ~768.
        int rampSamples = 5 * Sr / 1000;
        int windowStart = rampSamples;
        int windowEnd = (slow.Length / 2) - rampSamples;
        int slowCrossings = CountZeroCrossingsI(slow, windowStart, windowEnd);
        int fastCrossings = CountZeroCrossingsI(fast, windowStart, windowEnd);

        // 9600 Hz is 16× the 600 Hz rate, so we expect ~16× the crossings.
        // Allow ±5% slack for boundary effects at the window edges.
        double ratio = (double)fastCrossings / slowCrossings;
        Assert.InRange(ratio, 16.0 * 0.95, 16.0 * 1.05);
    }

    private static int CountZeroCrossingsI(float[] iq, int sampleStart, int sampleEnd)
    {
        int n = 0;
        float prev = iq[2 * sampleStart];
        for (int s = sampleStart + 1; s < sampleEnd; s++)
        {
            float cur = iq[2 * s];
            if ((prev <= 0f && cur > 0f) || (prev >= 0f && cur < 0f)) n++;
            prev = cur;
        }
        return n;
    }

    // --- Raw-key path (TCI keyer:1 / keyer:0, zeus-j3t) -----------------

    [Fact]
    public void RenderRawKey_FullDuration_ProducesAttackPlateauRelease()
    {
        // 100 ms raw-key: 5 ms attack (raised-cosine from 0 to 1), ~90 ms
        // plateau at amplitude 1, 5 ms release (raised-cosine back to 0).
        // Output buffer length = duration + ramp (the release tail is appended
        // *after* the duration window — duration is the "key-held" interval,
        // ramp is the no-click fall).
        var iq = CwEngine.RenderRawKeyForTest(durationMs: 100, basebandHz: CwuNoCtun);

        int durationSamples = 100 * Sr / 1000;
        int rampSamples = 5 * Sr / 1000;
        Assert.Equal(2 * (durationSamples + rampSamples), iq.Length);
    }

    [Fact]
    public void RenderRawKey_AttackEdge_StartsFromZero()
    {
        // First sample of the attack ramp = env(0) × cos(0) = 0. Sanity that
        // the raised-cosine envelope is present — if anyone removes the
        // shaper, the first sample jumps straight to amplitude 1 and the
        // on-air signal develops audible CW clicks.
        var iq = CwEngine.RenderRawKeyForTest(durationMs: 100, basebandHz: CwuNoCtun);

        Assert.Equal(0f, iq[0], precision: 5);
        Assert.Equal(0f, iq[1], precision: 5);
    }

    [Fact]
    public void RenderRawKey_ReleaseTail_EndsAtZero()
    {
        // The final IQ pair belongs to the release ramp's last sample —
        // env approaches zero as `into → rampSamples`, so the magnitude
        // there must be near zero. Guards against the inverse of the
        // attack-edge regression: a fade-out that truncates would leave
        // a residual carrier that thumps when MOX falls.
        var iq = CwEngine.RenderRawKeyForTest(durationMs: 100, basebandHz: CwuNoCtun);

        int last = iq.Length - 2;
        float mag = MathF.Sqrt(iq[last] * iq[last] + iq[last + 1] * iq[last + 1]);
        Assert.InRange(mag, 0f, 0.05f);
    }

    [Fact]
    public void RenderRawKey_PlateauMidpoint_HasFullAmplitude()
    {
        // Sample comfortably inside the plateau (past the 5 ms attack, well
        // before the release tail) — sqrt(I² + Q²) should be ~1.0. Confirms
        // that the steady-state carrier reaches full scale and isn't
        // accidentally being held at half-amplitude.
        var iq = CwEngine.RenderRawKeyForTest(durationMs: 100, basebandHz: CwuNoCtun);

        int durationSamples = 100 * Sr / 1000;
        // Halfway through the plateau region — safely past the 5 ms attack.
        int probe = durationSamples / 2;
        float mag = MathF.Sqrt(iq[2 * probe] * iq[2 * probe] + iq[2 * probe + 1] * iq[2 * probe + 1]);
        Assert.InRange(mag, 0.95f, 1.05f);
    }

    [Fact]
    public void RenderRawKey_CarrierFrequencyMatchesBaseband()
    {
        // Same zero-crossing check as the CTUN offset test, applied to the
        // raw-key path — guards against the LO-align logic in PlayJobAsync
        // and PlayRawKeyAsync drifting apart silently. At 600 Hz baseband
        // we expect ~12 crossings per 10 ms of plateau.
        var iq = CwEngine.RenderRawKeyForTest(durationMs: 50, basebandHz: 600);

        // Skip the 5 ms attack and release; measure within the plateau.
        int rampSamples = 5 * Sr / 1000;
        int durationSamples = 50 * Sr / 1000;
        int windowStart = rampSamples;
        int windowEnd = durationSamples - rampSamples;
        int crossings = CountZeroCrossingsI(iq, windowStart, windowEnd);

        // 600 Hz × (40 ms window) = 24 cycles → ~48 zero-crossings.
        // Allow ±10% for boundary effects.
        Assert.InRange(crossings, 43, 53);
    }

    [Fact]
    public void Render_KeyUpGaps_AreSilent()
    {
        // 'EE' at 20 WPM: dit, 3-unit inter-char gap, dit.
        // Inter-char gap is samples [dit..dit+gap] of the stream — pure zeros.
        var iq = CwEngine.RenderForTest("EE", wpm: 20, basebandHz: CwuNoCtun);

        int ditSamples = 60 * Sr / 1000;
        int gapSamples = 3 * 60 * Sr / 1000;

        // Inspect a sample comfortably inside the gap (skip the very first
        // sample at the boundary in case of rounding). 1000 samples in.
        int probe = ditSamples + 1000;
        Assert.True(probe < ditSamples + gapSamples, "probe must land inside the gap");

        Assert.Equal(0f, iq[2 * probe]);
        Assert.Equal(0f, iq[2 * probe + 1]);
    }

    [Fact]
    public void Render_HasRaisedCosineRiseEdge_NoHardClick()
    {
        // The first key-down sample should be near zero (start of the raised-
        // cosine ramp), not a hard step to full scale. This guards against
        // accidentally removing the envelope shaper.
        var iq = CwEngine.RenderForTest("E", wpm: 20, basebandHz: CwuNoCtun);

        // Sample 0: I = cos(0) × env(0) = 1 × 0 = 0. Q = sin(0) × env(0) = 0.
        // Both must be effectively zero — the envelope IS the click suppressor.
        Assert.Equal(0f, iq[0], precision: 5);
        Assert.Equal(0f, iq[1], precision: 5);

        // Mid-ramp (sample 120 = 2.5 ms in, half-way through the 5 ms / 240-
        // sample ramp): envelope is 0.5(1 - cos(π/2)) = 0.5, so combined
        // amplitude sqrt(I² + Q²) ≈ 0.5.
        int mid = 120;
        float mag = MathF.Sqrt(iq[2 * mid] * iq[2 * mid] + iq[2 * mid + 1] * iq[2 * mid + 1]);
        Assert.InRange(mag, 0.3f, 0.7f);
    }
}
