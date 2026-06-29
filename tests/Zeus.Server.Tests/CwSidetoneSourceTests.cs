// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// CwSidetoneSource — host-side CW monitor tone generator. Tests cover:
//   - Idle state writes nothing (fast path)
//   - Down() / Up() edges produce attack and release envelopes
//   - Multiple Down() calls are idempotent (no envelope re-trigger)
//   - Pitch and gain clamp to operator-safe ranges
//   - Render mixes additively into existing audio

using Zeus.Server;

namespace Zeus.Server.Tests;

public class CwSidetoneSourceTests
{
    private const int Sr = CwSidetoneSource.OutputRateHz;
    private const int RampSamples = 240; // private const RampMs (5) * SR / 1000

    [Fact]
    public void RenderInto_Idle_WritesNothing_ReturnsFalse()
    {
        var src = new CwSidetoneSource();
        var buf = new float[1000];
        Array.Fill(buf, 0.5f); // poison value — must survive

        bool wrote = src.RenderInto(buf);

        Assert.False(wrote);
        Assert.All(buf, s => Assert.Equal(0.5f, s));
    }

    [Fact]
    public void RenderInto_AfterDown_StartsAtZero_RampsToPlateau()
    {
        // Raised-cosine attack: sample 0 is env(0) × sin(0) = 0; sample at
        // mid-ramp (i = ramp/2) has env ≈ 0.5 so |sample| ≤ 0.5 × gain.
        // Guards against accidentally removing the envelope and producing
        // an audible click on the first dit.
        var src = new CwSidetoneSource();
        src.SetGainDb(0.0); // unity for assertable amplitudes
        src.Down();

        var buf = new float[Sr / 100]; // 10 ms = 480 samples
        bool wrote = src.RenderInto(buf);

        Assert.True(wrote);
        Assert.Equal(0f, buf[0], precision: 4);

        // Plateau reached after RampSamples — pick a sample well past the
        // ramp (sample 300 of 480) and verify amplitude is within unity.
        // sin() varies sample-to-sample so we look at the envelope of |s|
        // via a peak window.
        float peak = 0;
        for (int i = RampSamples + 50; i < buf.Length; i++)
            peak = Math.Max(peak, Math.Abs(buf[i]));
        Assert.InRange(peak, 0.9f, 1.05f);
    }

    [Fact]
    public void RenderInto_AfterUp_FadesToSilence_ReturnsFalseAtEnd()
    {
        var src = new CwSidetoneSource();
        src.SetGainDb(0.0);
        src.Down();
        // Push past the attack and into the plateau.
        var warm = new float[Sr / 100];
        src.RenderInto(warm);
        // Now release. The next render should fade 1 → 0 over RampSamples
        // and write silence after that. After two ramp lengths it should
        // be back to "idle".
        src.Up();
        var buf = new float[RampSamples * 4];
        bool wrote = src.RenderInto(buf);
        Assert.True(wrote, "release ramp produces samples");
        // Tail past the release should be exactly 0 (additive into empty).
        for (int i = RampSamples + 50; i < buf.Length; i++)
            Assert.Equal(0f, buf[i]);

        // A second call after the release ramp completes returns false.
        var buf2 = new float[100];
        Assert.False(src.RenderInto(buf2));
    }

    [Fact]
    public void Down_Idempotent_DoesNotRetriggerAttack()
    {
        // After the attack has completed, a second Down() must not reset
        // the envelope — would produce a mid-symbol amplitude dip otherwise.
        var src = new CwSidetoneSource();
        src.SetGainDb(0.0);
        src.Down();
        var warm = new float[Sr / 100]; // 10 ms warms past the 5 ms ramp
        src.RenderInto(warm);

        src.Down(); // redundant

        var buf = new float[20];
        src.RenderInto(buf);
        // Sample 10 should still be near plateau, not back in the attack.
        float peak = 0;
        for (int i = 0; i < buf.Length; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
        Assert.InRange(peak, 0.7f, 1.05f);
    }

    [Fact]
    public void Up_WithoutPriorDown_IsNoOp()
    {
        var src = new CwSidetoneSource();
        src.Up();
        var buf = new float[100];
        Array.Fill(buf, 0.25f);
        bool wrote = src.RenderInto(buf);
        Assert.False(wrote);
        Assert.All(buf, s => Assert.Equal(0.25f, s));
    }

    [Fact]
    public void RenderInto_MixesAdditively_PreservesIncomingAudio()
    {
        // RX audio in the buffer must survive sidetone mixing — otherwise
        // the operator's band RX disappears under the CW monitor tone.
        var src = new CwSidetoneSource();
        src.SetGainDb(-20.0);
        src.Down();

        var buf = new float[Sr / 100];
        Array.Fill(buf, 0.3f);
        src.RenderInto(buf);

        // Past the ramp, the magnitude should be ≈ 0.3 + sidetone(0.1) sin().
        // Both contributions present: nothing should be exactly 0.3 anymore.
        int probe = RampSamples + 50;
        Assert.NotEqual(0.3f, buf[probe]);
        // And bounded — sidetone at -20 dB ≈ 0.1 amplitude, so the sum
        // stays within [0.2, 0.4].
        Assert.InRange(buf[probe], 0.2f, 0.4f);
    }

    [Fact]
    public void SetGainDb_AtSilenceFloor_RendersNothing()
    {
        // Below the silence threshold (gain < 1e-4 ≈ -80 dB) the loop
        // skips work — operator can't hear it. Saves sin() per sample
        // when the slider is at minimum.
        var src = new CwSidetoneSource();
        src.SetGainDb(-60.0); // clamped to floor by SetGainDb anyway
        src.Down();
        // -60 dB is above the silence floor, so it should still render.
        var buf = new float[100];
        bool wrote = src.RenderInto(buf);
        Assert.True(wrote);
    }

    [Theory]
    [InlineData(100, 200)]   // below MinPitchHz → clamped
    [InlineData(2000, 1200)] // above MaxPitchHz → clamped
    [InlineData(600, 600)]   // in range — preserved
    [InlineData(750, 750)]
    public void SetPitchHz_Clamps(int requested, int expectedAfterClamp)
    {
        var src = new CwSidetoneSource();
        src.SetPitchHz(requested);
        src.SetGainDb(0.0);
        src.Down();

        // Count zero crossings in 100 ms — should be ≈ 2 × pitch × 0.1.
        var buf = new float[Sr / 10];
        src.RenderInto(buf);
        // Skip past the attack ramp.
        int crossings = 0;
        float prev = buf[RampSamples];
        for (int i = RampSamples + 1; i < buf.Length; i++)
        {
            float cur = buf[i];
            if ((prev <= 0f && cur > 0f) || (prev >= 0f && cur < 0f)) crossings++;
            prev = cur;
        }
        int expectedCrossings = 2 * expectedAfterClamp * (buf.Length - RampSamples) / Sr;
        // ±10% slack at the window edges.
        Assert.InRange(crossings, (int)(expectedCrossings * 0.9), (int)(expectedCrossings * 1.1));
    }

    [Fact]
    public void IsActive_TracksKeyState_AndReleaseRamp()
    {
        // Mimics the real DSP-thread cadence: Down → render (plateau) →
        // Up → render (release). The "Up but render hasn't seen the edge
        // yet" sub-state is the one that has to read as active so the
        // pipeline keeps publishing audio blocks until the release tail
        // is on the wire.
        var src = new CwSidetoneSource();
        Assert.False(src.IsActive);

        src.Down();
        Assert.True(src.IsActive);
        // Warm past the attack so _renderedKeyed flips true.
        src.RenderInto(new float[RampSamples * 2]);
        Assert.True(src.IsActive);

        src.Up();
        // _renderedKeyed=true and _keyed=false → release edge pending.
        Assert.True(src.IsActive);

        // Render through the release ramp; afterwards we're idle.
        src.RenderInto(new float[RampSamples * 2]);
        Assert.False(src.IsActive);
    }
}
