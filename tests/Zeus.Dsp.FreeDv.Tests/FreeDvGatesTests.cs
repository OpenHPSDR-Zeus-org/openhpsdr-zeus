// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

// Pure-math dynamics blocks — no native library needed. These verify the
// "artifacts when a user stops talking" fixes: the RX sync squelch and the
// pre-vocoder mic noise gate.
public class FreeDvGatesTests
{
    private const int Fs = 48000;

    private static float[] Const(int n, float v)
    {
        var b = new float[n];
        for (int i = 0; i < n; i++) b[i] = v;
        return b;
    }

    private static float PeakAbs(ReadOnlySpan<float> b)
    {
        float p = 0f;
        foreach (var x in b) { float a = x < 0 ? -x : x; if (a > p) p = a; }
        return p;
    }

    // ---------------- RxSquelchGate ----------------

    [Fact]
    public void RxGate_StartsClosed_FadesInWhenSynced()
    {
        var gate = new RxSquelchGate(Fs, holdMs: 10, attackMs: 1, releaseMs: 5);
        // First synced block fades up from the closed (reset) state.
        var first = Const(480, 1f);
        gate.Process(first, synced: true);
        Assert.True(gate.Gain > 0f && gate.Gain < 1f, $"expected partial open, got {gate.Gain}");

        // After enough synced audio the gate is fully open and transparent.
        for (int i = 0; i < 20; i++) gate.Process(Const(480, 1f), synced: true);
        Assert.True(gate.Gain > 0.99f);
        var blk = Const(480, 0.5f);
        gate.Process(blk, synced: true);
        Assert.True(PeakAbs(blk) > 0.49f); // passed through essentially unchanged
    }

    [Fact]
    public void RxGate_MutesAfterSyncLoss_AndReportsFullyClosed()
    {
        var gate = new RxSquelchGate(Fs, holdMs: 10, attackMs: 1, releaseMs: 5);
        for (int i = 0; i < 40; i++) gate.Process(Const(480, 1f), synced: true);
        Assert.True(gate.Gain > 0.99f);

        // Lose sync: within the hold the gate stays open (good copy isn't chopped).
        gate.Process(Const(480, 1f), synced: false); // 10ms of samples = 480; hold=10ms=480
        Assert.True(gate.IsOpen, "gate should ride the hold window before closing");

        // Past the hold the gate closes; eventually reports fully closed and mutes.
        bool fullyClosed = false;
        for (int i = 0; i < 200 && !fullyClosed; i++)
            fullyClosed = gate.Process(Const(480, 1f), synced: false);
        Assert.True(fullyClosed, "gate never reported fully closed after sustained sync loss");

        var blk = Const(480, 1f);
        gate.Process(blk, synced: false);
        Assert.True(PeakAbs(blk) < 1e-3f, $"muted block should be ~silent, peak={PeakAbs(blk)}");
    }

    [Fact]
    public void RxGate_Reset_ReturnsToClosed()
    {
        var gate = new RxSquelchGate(Fs, holdMs: 10, attackMs: 1, releaseMs: 5);
        for (int i = 0; i < 40; i++) gate.Process(Const(480, 1f), synced: true);
        gate.Reset();
        Assert.Equal(0f, gate.Gain);
        Assert.False(gate.IsOpen);
    }

    // ---------------- MicNoiseGate ----------------

    private static MicNoiseGate NewMicGate() => new(
        Fs, openMarginDb: 14, closeMarginDb: 8, envAttackMs: 2, envReleaseMs: 40,
        floorDownMs: 20, floorUpMs: 2000, hangMs: 30, gateAttackMs: 3, gateReleaseMs: 20,
        minFloorDb: -90);

    [Fact]
    public void MicGate_PassesSpeechAboveNoiseFloor()
    {
        var gate = NewMicGate();
        // Establish a low noise floor first (gate adapts + closes on it).
        for (int i = 0; i < 80; i++) gate.Process(Const(480, 0.002f));
        // Then speech well above the floor opens the gate and passes.
        float gain = 0f;
        for (int i = 0; i < 30; i++) { var b = Const(480, 0.2f); gain = gate.Process(b); }
        Assert.True(gain > 0.99f, $"speech well above the floor should be fully open, gain={gain}");
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void MicGate_SilencesBackgroundNoiseAfterHang()
    {
        var gate = NewMicGate();
        // Sustained low-level noise: the floor converges to it, so the envelope
        // sits below the (relative) close threshold and the gate shuts.
        const float noise = 0.002f;
        float gain = 1f;
        for (int i = 0; i < 200; i++) gain = gate.Process(Const(480, noise));
        Assert.True(gain < 0.05f, $"steady background noise should be gated to silence, gain={gain}");

        var blk = Const(480, noise);
        gate.Process(blk);
        Assert.True(PeakAbs(blk) < noise, "gated block should be attenuated below the input noise level");
    }

    [Fact]
    public void MicGate_StartsOpen_FirstSyllableNotClipped()
    {
        var gate = NewMicGate();
        // The very first block of an over should pass at (near) unity — the reset
        // state is open, so a word onset at t=0 isn't swallowed.
        var blk = Const(480, 0.3f);
        gate.Process(blk);
        Assert.True(PeakAbs(blk) > 0.29f, $"first block should pass through, peak={PeakAbs(blk)}");
    }
}
