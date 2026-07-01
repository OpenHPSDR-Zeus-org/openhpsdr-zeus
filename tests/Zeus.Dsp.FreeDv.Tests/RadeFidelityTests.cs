// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

/// <summary>
/// Objective, native end-to-end RADE fidelity tests — the "next level" beyond the
/// sync-only loopback in <see cref="RadeModemTests"/>. Inspired by RADE's own
/// validation discipline (drowe67/radae "Testing RADE"): measure decoded-audio
/// quality and behaviour with calibrated, reproducible signals rather than
/// listening on air. Each runs the REAL native modem (zeus_rade + FARGAN) through
/// the full Zeus hot path, so they also guard the artifact-gate + resampler
/// changes against regressions in the actual decoder.
///
/// SkippableFact: requires the zeus_rade native library for this RID.
/// </summary>
public class RadeFidelityTests
{
    private const int Fs = 48000;
    private const int Block = 1920; // 40 ms @ 48 kHz

    /// <summary>
    /// Clean loopback: the decoded speech must carry real energy AND must not clip.
    /// FARGAN's float output is scaled by 32768 in the shim (matching upstream
    /// lpcnet_demo); this asserts that scaling leaves headroom in practice — the
    /// objective check behind the "is the decoder output clipping?" question from
    /// the radae playback-scaling note. A high clip rate here would manifest on air
    /// as harsh/"pinched" audio.
    /// </summary>
    [SkippableFact]
    public void Loopback_DecodedOutput_HasEnergy_AndDoesNotClip()
    {
        using var modem = new RadeModem();
        Skip.IfNot(modem.RadeAvailable, "zeus_rade native library not present for this RID.");
        modem.SetTxText("N9WAR");
        modem.Activate();

        var modemAudio = Encode(modem, seconds: 7);

        double sumSq = 0; long count = 0, clips = 0; double peak = 0;
        bool synced = false;
        var seg = new float[Block];
        for (int off = 0; off + Block <= modemAudio.Length; off += Block)
        {
            Array.Copy(modemAudio, off, seg, 0, Block);
            modem.ProcessRxInPlace(seg);
            if (modem.Synced) synced = true;
            if (!synced) continue; // only score the decoded (post-acquisition) region
            foreach (var s in seg)
            {
                float a = MathF.Abs(s);
                sumSq += (double)s * s; count++;
                if (a > peak) peak = a;
                if (a >= 0.999f) clips++;
            }
        }

        Assert.True(synced, "RADE never synced — cannot score decoded fidelity.");
        Assert.True(count > 0, "no decoded samples scored.");
        double rms = Math.Sqrt(sumSq / count);
        double clipRate = clips / (double)count;
        Assert.True(rms > 1e-3, $"decoded speech essentially silent (rms={rms:E2}) — decode/level path broken.");
        Assert.True(clipRate < 0.005,
            $"decoded speech clips {clipRate:P2} of samples (peak={peak:F3}) — output scaling has no headroom.");
    }

    /// <summary>
    /// THE headline artifact fix, validated through the real decoder: once a signal
    /// has been decoded (gate open, audible), feeding pure band-noise (the far
    /// station having unkeyed) must drive the decoded output to silence — the sync
    /// squelch closes and flushes, instead of the FARGAN decoder warbling on noise
    /// ("R2D2 tail"). Also a false-sync guard: noise must not be turned into audio.
    /// </summary>
    [SkippableFact]
    public void SyncGate_SilencesDecodedOutput_AfterSignalStops()
    {
        using var modem = new RadeModem();
        Skip.IfNot(modem.RadeAvailable, "zeus_rade native library not present for this RID.");
        modem.SetTxText("N9WAR");
        modem.Activate();

        var modemAudio = Encode(modem, seconds: 7);

        // Phase A: decode the real signal; measure the "voice present" decoded RMS
        // over blocks where sync is actually HELD (and so the gate is open). RADE's
        // acquisition state machine deliberately drops sync on a poor/out-of-
        // distribution signal — on a synthetic excitation that happens mid-stream,
        // and the gate correctly mutes there — so a fixed time window would wrongly
        // average in those (correctly) muted stretches. Scoring only synced blocks
        // gives the true open-gate voice level to compare the noise tail against.
        bool everSynced = false;
        double syncedSumSq = 0; long syncedCount = 0; int syncedBlocks = 0;
        var seg = new float[Block];
        int total = modemAudio.Length;
        for (int off = 0; off + Block <= total; off += Block)
        {
            Array.Copy(modemAudio, off, seg, 0, Block);
            modem.ProcessRxInPlace(seg);
            if (!modem.Synced) continue;
            everSynced = true;
            syncedBlocks++;
            foreach (var s in seg) { syncedSumSq += (double)s * s; syncedCount++; }
        }
        Assert.True(everSynced && syncedCount > 0, "RADE never synced — cannot validate the stop-talk gate.");
        double syncedRms = Math.Sqrt(syncedSumSq / syncedCount);
        Assert.True(syncedRms > 1e-3,
            $"no audible decoded voice while synced (rms={syncedRms:E2} over {syncedBlocks} synced blocks).");

        // Phase B: the far station stops — feed pure noise for ~3 s. The gate must
        // ride its hold, lose sync, then mute + flush. Score only the last ~0.7 s.
        var rng = new Random(20260629);
        int noiseBlocks = (3 * Fs) / Block;
        int scoreFrom = noiseBlocks - (int)(0.7 * Fs / Block);
        double tailSumSq = 0; long tailCount = 0; double tailPeak = 0;
        for (int b = 0; b < noiseBlocks; b++)
        {
            for (int i = 0; i < Block; i++) seg[i] = (float)((rng.NextDouble() * 2 - 1) * 0.2);
            modem.ProcessRxInPlace(seg);
            if (b >= scoreFrom)
                foreach (var s in seg)
                {
                    tailSumSq += (double)s * s; tailCount++;
                    float a = MathF.Abs(s); if (a > tailPeak) tailPeak = a;
                }
        }
        double tailRms = Math.Sqrt(tailSumSq / tailCount);

        Assert.True(tailRms < 0.1 * syncedRms,
            $"decoded output not muted after the signal stopped: tailRms={tailRms:E2} vs voiceRms={syncedRms:E2} " +
            $"(ratio={tailRms / syncedRms:P1}, peak={tailPeak:F3}) — the sync squelch is not killing the noise tail.");
    }

    /// <summary>
    /// The end-of-over garble fix (zeus-japz), validated through the real decoder:
    /// while keyed the pipeline keeps feeding WDSP RX into the modem, so it decodes
    /// a live backlog. <see cref="RadeModem.FlushRx"/> (called on the MOX edge) must
    /// leave the receiver empty and unsynced so the resuming RX starts silent
    /// instead of dumping that self-decoded backlog into Zeus's own audio. Here:
    /// decode until synced (a real backlog exists), flush, then feed one block of
    /// pure silence — the output must be silent and the modem unsynced.
    /// </summary>
    [SkippableFact]
    public void FlushRx_AfterSync_ResumesSilentAndUnsynced()
    {
        using var modem = new RadeModem();
        Skip.IfNot(modem.RadeAvailable, "zeus_rade native library not present for this RID.");
        modem.SetTxText("N9WAR");
        modem.Activate();

        var modemAudio = Encode(modem, seconds: 6);

        // Decode until the modem acquires sync (there is now a live decode backlog).
        bool synced = false;
        var seg = new float[Block];
        for (int off = 0; off + Block <= modemAudio.Length; off += Block)
        {
            Array.Copy(modemAudio, off, seg, 0, Block);
            modem.ProcessRxInPlace(seg);
            if (modem.Synced) { synced = true; break; }
        }
        Skip.IfNot(synced, "RADE never synced — cannot exercise FlushRx.");

        // Flush as the un-key MOX edge does, then feed one block of pure silence.
        modem.FlushRx();
        Array.Clear(seg, 0, seg.Length);
        modem.ProcessRxInPlace(seg);

        double sumSq = 0;
        foreach (var s in seg) sumSq += (double)s * s;
        double rms = Math.Sqrt(sumSq / seg.Length);

        Assert.False(modem.Synced, "FlushRx must leave the receiver unsynced.");
        Assert.True(rms < 1e-4,
            $"post-flush output not silent (rms={rms:E2}) — a self-decoded backlog leaked past FlushRx.");
    }

    // Encode `seconds` of wideband voiced-speech-like audio through the TX hot path,
    // returning the 48 kHz modem waveform.
    private static float[] Encode(RadeModem modem, int seconds)
    {
        int blocks = (seconds * Fs) / Block;
        var modemAudio = new List<float>(blocks * Block);
        double phase = 0;
        var buf = new float[Block];
        for (int b = 0; b < blocks; b++)
        {
            MakeSpeech(buf, ref phase);
            modem.ProcessTxInPlace(buf);
            modemAudio.AddRange(buf);
        }
        return modemAudio.ToArray();
    }

    // Voiced-ish speech at 48 kHz with content out to a few kHz: a low pitch with
    // harmonics + slow tremolo, plus a light fricative-band noise so the LPCNet
    // analyser sees broadband structure. Content is irrelevant to modem sync.
    private static void MakeSpeech(float[] b, ref double phase)
    {
        const double f0 = 130.0, fs = 48000.0;
        var rng = _speechNoise;
        for (int i = 0; i < b.Length; i++)
        {
            double t = (phase += 1.0 / fs);
            double trem = 0.6 + 0.4 * Math.Sin(2 * Math.PI * 3.0 * t);
            double s = 0;
            for (int h = 1; h <= 10; h++) s += (1.0 / h) * Math.Sin(2 * Math.PI * f0 * h * t);
            s += 0.05 * (rng.NextDouble() * 2 - 1); // light broadband fricative energy
            b[i] = (float)(trem * s * 0.16);
        }
    }

    private static readonly Random _speechNoise = new(424242);
}
