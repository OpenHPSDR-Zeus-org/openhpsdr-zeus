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

    [Fact]
    public void Render_E_At20Wpm_ProducesOneDitOfSamples()
    {
        // 'E' = dit. At 20 WPM the PARIS unit is 1200/20 = 60 ms.
        // No leading or trailing inter-char gap (single char, single
        // character of buffer), so the IQ stream is exactly 60 ms long.
        var iq = CwEngine.RenderForTest("E", wpm: 20, mode: RxMode.CWU);

        int expectedSamples = 60 * Sr / 1000;     // 2880 samples
        Assert.Equal(2 * expectedSamples, iq.Length);
    }

    [Fact]
    public void Render_T_At20Wpm_ProducesOneDahOfSamples()
    {
        // 'T' = dah. 3 units × 60 ms = 180 ms.
        var iq = CwEngine.RenderForTest("T", wpm: 20, mode: RxMode.CWU);

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
        var iq = CwEngine.RenderForTest("CQ", wpm: 20, mode: RxMode.CWU);

        int expectedSamples = 1620 * Sr / 1000;   // 77 760
        Assert.Equal(2 * expectedSamples, iq.Length);
    }

    [Fact]
    public void Render_CWU_vs_CWL_FlipsQSign_OnTheCarrier()
    {
        // The pitch sign distinguishes the sideband: CWU emits +pitch,
        // CWL emits -pitch (CwOffset.CwPitchHz = 600 Hz). At the carrier
        // peak (envelope = 1, phase past the ramp), I[n]/Q[n] for CWU and
        // CWL should have the same I value and equal-and-opposite Q.
        var u = CwEngine.RenderForTest("T", wpm: 20, mode: RxMode.CWU);
        var l = CwEngine.RenderForTest("T", wpm: 20, mode: RxMode.CWL);

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
    public void Render_KeyUpGaps_AreSilent()
    {
        // 'EE' at 20 WPM: dit, 3-unit inter-char gap, dit.
        // Inter-char gap is samples [dit..dit+gap] of the stream — pure zeros.
        var iq = CwEngine.RenderForTest("EE", wpm: 20, mode: RxMode.CWU);

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
        var iq = CwEngine.RenderForTest("E", wpm: 20, mode: RxMode.CWU);

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
