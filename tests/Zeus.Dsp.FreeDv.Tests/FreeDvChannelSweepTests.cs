// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Zeus.Contracts;

namespace Zeus.Dsp.FreeDv.Tests;

/// <summary>
/// The RADE V2 READINESS GATE: an objective, reproducible SNR-sweep through the
/// REAL decoder. A clean modem waveform is produced by the TX hot path, passed
/// through <see cref="FreeDvChannelSim"/> (calibrated AWGN, optional 2-ray
/// multipath) at a range of SNRs, decoded by the REAL modem, and its sync-
/// acquisition + decoded-energy behaviour is recorded per SNR. This is the
/// managed adoption of radae's "Testing RADE" discipline — grade the modem with a
/// calibrated channel and documented thresholds, not on-air listening.
///
/// SkippableFact: requires the native modem libraries for this RID.
///
/// STABILITY / determinism: a FIXED noise seed per SNR point, and the assertions
/// lock the ROBUST shape — "syncs comfortably above threshold, and the harness
/// runs end to end" — while deep-negative SNR is allowed to fail (it is where the
/// modem is DESIGNED to drop sync). The exact cliff is logged to test output, not
/// asserted, so this is a regression gate and never flaky. Upstream RADE V1 quotes
/// ≈ −2 dB AWGN sync threshold; we assert sync at a comfortable +6/+3 dB margin.
/// </summary>
public class FreeDvChannelSweepTests
{
    private const int Fs = 48000;
    private const int Block = 1920; // 40 ms @ 48 kHz — matches RadeFidelityTests

    // The sweep. High → low; deep-negative points are informational (allowed to
    // fail to sync) so the gate stays stable.
    private static readonly double[] SweepSnrDb = { 10, 6, 3, 0, -2, -4 };

    private readonly ITestOutputHelper _out;
    public FreeDvChannelSweepTests(ITestOutputHelper output) => _out = output;

    private readonly record struct SweepPoint(double SnrDb, bool Synced, double DecodedRms);

    [SkippableFact]
    public void Rade_AwgnSweep_SyncsAboveThreshold_AndRunsEndToEnd()
    {
        using var probe = new RadeModem();
        Skip.IfNot(probe.RadeAvailable, "zeus_rade native library not present for this RID.");

        // Produce one clean modem waveform, reuse it across the sweep so each SNR
        // point sees the identical signal + a deterministic noise draw.
        float[] clean = EncodeRade(seconds: 8);

        var results = RunSweep(clean, multipath: false, seedBase: 0x5A5E_0001);
        LogSweep("RADE AWGN sweep", results);

        AssertSweepShape(results, syncMarginSnrDb: 6.0);
    }

    [SkippableFact]
    public void Rade_MultipathSweep_RunsEndToEnd_AndSyncsAtHighSnr()
    {
        using var probe = new RadeModem();
        Skip.IfNot(probe.RadeAvailable, "zeus_rade native library not present for this RID.");

        float[] clean = EncodeRade(seconds: 8);

        // 2-ray multipath (2 ms delay, 0.5 second-path) THEN AWGN. radae quotes a
        // ~0 dB MPP threshold, so multipath is strictly harder than pure AWGN; we
        // only assert sync at a comfortable high-SNR margin and that the sweep runs
        // clean end to end. The full curve is logged.
        var results = RunSweep(clean, multipath: true, seedBase: 0x5A5E_0002);
        LogSweep("RADE 2-ray multipath + AWGN sweep", results);

        // Multipath is harder — require sync only well above threshold (+10 dB),
        // and never assert on the deep-negative tail.
        AssertSweepShape(results, syncMarginSnrDb: 10.0, requireMonotoneEnergy: false);
    }

    /// <summary>
    /// Optional codec2 700D variant of the AWGN sweep — the classic FreeDV modem
    /// (not RADE). Gated on the codec2 native lib. codec2 700D squelch is disabled
    /// so a synced-but-low-SNR decode still yields samples; we grade on the sync
    /// flag and decoded level, same as the RADE sweep.
    /// </summary>
    [SkippableFact]
    public void Codec2_700D_AwgnSweep_SyncsAboveThreshold()
    {
        using var probe = new FreeDvModem();
        Skip.IfNot(probe.NativeAvailable, "codec2 native library not present for this RID.");

        float[] clean = EncodeCodec2(FreeDvSubmode.Mode700D, seconds: 10);

        var results = RunCodec2Sweep(FreeDvSubmode.Mode700D, clean, seedBase: 0x5A5E_0003);
        LogSweep("codec2 700D AWGN sweep", results);

        // codec2 700D's own SNR sync threshold is around −2 dB; assert sync holds at
        // a comfortable +3 dB margin and the sweep runs end to end.
        AssertSweepShape(results, syncMarginSnrDb: 3.0, requireMonotoneEnergy: false);
    }

    // ---- sweep drivers ----

    private static List<SweepPoint> RunSweep(float[] clean, bool multipath, int seedBase)
    {
        var results = new List<SweepPoint>(SweepSnrDb.Length);
        for (int k = 0; k < SweepSnrDb.Length; k++)
        {
            double snr = SweepSnrDb[k];
            float[] chan = ApplyChannel(clean, snr, multipath, seedBase + k);

            // A FRESH modem per SNR point so acquisition state never carries over.
            using var modem = new RadeModem();
            modem.Activate();

            var pt = DecodeRade(modem, chan);
            results.Add(new SweepPoint(snr, pt.Synced, pt.DecodedRms));
        }
        return results;
    }

    private static List<SweepPoint> RunCodec2Sweep(FreeDvSubmode submode, float[] clean, int seedBase)
    {
        var results = new List<SweepPoint>(SweepSnrDb.Length);
        for (int k = 0; k < SweepSnrDb.Length; k++)
        {
            double snr = SweepSnrDb[k];
            float[] chan = ApplyChannel(clean, snr, multipath: false, seedBase + k);

            using var modem = new FreeDvModem();
            modem.SetSubmode(submode);
            modem.SetSquelch(enabled: false, threshDb: null);
            modem.Activate();

            var pt = DecodeCodec2(modem, chan);
            results.Add(new SweepPoint(snr, pt.Synced, pt.DecodedRms));
        }
        return results;
    }

    private static float[] ApplyChannel(float[] clean, double snrDb, bool multipath, int seed)
    {
        float[] chan = (float[])clean.Clone();
        if (multipath)
            chan = FreeDvChannelSim.AddMultipath(chan, delayMs: 2.0, secondPathGain: 0.5);
        FreeDvChannelSim.AddAwgn(chan, snrDb, new Random(seed));
        return chan;
    }

    // ---- decode + score (only the synced region contributes to decoded RMS) ----

    private readonly record struct DecodeResult(bool Synced, double DecodedRms);

    private static DecodeResult DecodeRade(RadeModem modem, float[] channelAudio)
    {
        bool everSynced = false;
        double sumSq = 0; long count = 0;
        var seg = new float[Block];
        for (int off = 0; off + Block <= channelAudio.Length; off += Block)
        {
            Array.Copy(channelAudio, off, seg, 0, Block);
            modem.ProcessRxInPlace(seg);
            if (!modem.Synced) continue;
            everSynced = true;
            foreach (var s in seg) { sumSq += (double)s * s; count++; }
        }
        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0.0;
        return new DecodeResult(everSynced, rms);
    }

    private static DecodeResult DecodeCodec2(FreeDvModem modem, float[] channelAudio)
    {
        bool everSynced = false;
        double sumSq = 0; long count = 0;
        var seg = new float[Block];
        for (int off = 0; off + Block <= channelAudio.Length; off += Block)
        {
            Array.Copy(channelAudio, off, seg, 0, Block);
            modem.ProcessRxInPlace(seg);
            if (!modem.Synced) continue;
            everSynced = true;
            foreach (var s in seg) { sumSq += (double)s * s; count++; }
        }
        double rms = count > 0 ? Math.Sqrt(sumSq / count) : 0.0;
        return new DecodeResult(everSynced, rms);
    }

    // ---- shape assertions (robust, non-brittle) ----

    private static void AssertSweepShape(
        List<SweepPoint> results, double syncMarginSnrDb, bool requireMonotoneEnergy = true)
    {
        // 1) The harness ran end to end for every SNR point.
        Assert.Equal(SweepSnrDb.Length, results.Count);

        // 2) The modem MUST acquire sync at a comfortable margin above the quoted
        //    threshold — the load-bearing readiness assertion.
        var high = results.Find(p => Math.Abs(p.SnrDb - syncMarginSnrDb) < 1e-9);
        Assert.True(high.SnrDb == syncMarginSnrDb,
            $"sweep is missing the {syncMarginSnrDb} dB reference point.");
        Assert.True(high.Synced,
            $"modem failed to sync at {syncMarginSnrDb:F0} dB SNR — a comfortable margin above the " +
            $"quoted threshold. The channel-sim readiness gate regressed.");
        Assert.True(high.DecodedRms > 1e-3,
            $"no decoded energy at {syncMarginSnrDb:F0} dB SNR (rms={high.DecodedRms:E2}) — decode path broken.");

        // 3) The very-high-SNR anchor (+10 dB, top of the sweep) must ALSO sync and
        //    decode — a near-clean channel is the strongest guarantee.
        var top = results[0];
        Assert.True(top.Synced && top.DecodedRms > 1e-3,
            $"top-of-sweep {top.SnrDb:F0} dB failed to sync/decode (synced={top.Synced}, rms={top.DecodedRms:E2}).");

        // 4) Monotonic energy trend where required: the mean decoded RMS over the
        //    upper half of the sweep should be >= the lower half. This locks the
        //    "cleaner channel ⇒ at-least-as-good decode" shape without a brittle
        //    per-point number. Deep-negative points may be 0 (allowed to fail) and
        //    only drag the lower-half mean down, so this can never flip false on a
        //    healthy modem.
        if (requireMonotoneEnergy)
        {
            int half = results.Count / 2;
            double upper = 0, lower = 0;
            for (int i = 0; i < half; i++) upper += results[i].DecodedRms;
            for (int i = half; i < results.Count; i++) lower += results[i].DecodedRms;
            upper /= half; lower /= (results.Count - half);
            Assert.True(upper >= lower,
                $"decoded energy did not trend down with SNR: upper-half mean rms={upper:E2} < lower-half {lower:E2}.");
        }
    }

    private void LogSweep(string title, List<SweepPoint> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine("  SNR(dB)  synced  decodedRMS");
        foreach (var p in results)
            sb.AppendLine($"  {p.SnrDb,6:F1}  {(p.Synced ? "yes" : "NO "),6}  {p.DecodedRms,10:E3}");
        // Report the approximate cliff (lowest SNR that still synced).
        double cliff = double.NaN;
        foreach (var p in results) if (p.Synced) cliff = p.SnrDb;
        sb.AppendLine($"  lowest-SNR-that-synced ≈ {(double.IsNaN(cliff) ? "(none)" : cliff.ToString("F1") + " dB")}");
        _out.WriteLine(sb.ToString());
    }

    // ---- clean-waveform encoders (TX hot path), mirrors RadeFidelityTests ----

    private static float[] EncodeRade(int seconds)
    {
        using var modem = new RadeModem();
        modem.SetTxText("N9WAR");
        modem.Activate();
        int blocks = (seconds * Fs) / Block;
        var audio = new List<float>(blocks * Block);
        double phase = 0;
        var buf = new float[Block];
        for (int b = 0; b < blocks; b++)
        {
            MakeSpeech(buf, ref phase);
            modem.ProcessTxInPlace(buf);
            audio.AddRange(buf);
        }
        return audio.ToArray();
    }

    private static float[] EncodeCodec2(FreeDvSubmode submode, int seconds)
    {
        using var modem = new FreeDvModem();
        modem.SetSubmode(submode);
        modem.Activate();
        int blocks = (seconds * Fs) / Block;
        var audio = new List<float>(blocks * Block);
        double phase = 0;
        var buf = new float[Block];
        for (int b = 0; b < blocks; b++)
        {
            MakeSpeech(buf, ref phase);
            modem.ProcessTxInPlace(buf);
            audio.AddRange(buf);
        }
        return audio.ToArray();
    }

    // Voiced-ish speech at 48 kHz (mirrors RadeFidelityTests.MakeSpeech). Content
    // is irrelevant to modem sync — the modem keys off the OFDM pilots — but keeps
    // the vocoder fed with non-silence so frames are produced.
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
            s += 0.05 * (rng.NextDouble() * 2 - 1);
            b[i] = (float)(trem * s * 0.16);
        }
    }

    private static readonly Random _speechNoise = new(424242);
}
