// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Regression tests for the FT8/WSPR decode self-heal (pop-out #1080 regression:
// decode silently stopped while IsEnabled stayed true; only disable->enable
// revived it). The decode AUDIO TAP never drops — these prove the WORKER behind
// it does, and that the watchdog rebuilds it in place so decode resumes with NO
// operator action and NO transmit. Both services are driven through their test
// constructors with an injected fake decoder + clock; no native library, no real
// radio, fully deterministic time.
//
// SCOPE / honesty note: the wedge these tests force (a decoder that blocks forever
// inside Decode) is SYNTHETIC. The field root cause is still unproven on the bench
// — the watchdog is cause-agnostic recovery, and the new decode-timing /
// rebuild-count / leaked-worker logging in Ft8Service/WsprService is what will
// capture the true mechanism on the G2. These tests therefore cover the watchdog
// MECHANISM and the reconfigure/idempotency paths, plus that a persistent stall is
// BOUNDED (does not leak a worker/native-context on every tick forever).

using System.Collections.Concurrent;
using Zeus.Dsp.Ft8;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class DigitalDecodeWatchdogTests
{
    // A fake decoder that returns one known decode per call, and can be made to
    // WEDGE (block forever) on its first decode to reproduce the stall.
    private sealed class FakeFt8Decoder : IFt8Decoder
    {
        private readonly ManualResetEventSlim? _block;
        public volatile bool DecodeEntered;
        public int DecodeCalls;

        public FakeFt8Decoder(ManualResetEventSlim? block) => _block = block;

        public IReadOnlyList<Ft8DecodeResult> Decode(
            float[] samples, Ft8Protocol protocol = Ft8Protocol.Ft8, int passes = 1, int maxResults = 64)
        {
            Interlocked.Increment(ref DecodeCalls);
            DecodeEntered = true;
            _block?.Wait(); // wedge here when gated — mimics a hung native decode
            return new[] { new Ft8DecodeResult(-12f, 0.2f, 1500f, 100, 0, "CQ RK9AX GJ0K") };
        }

        public void Reset() { }
        public void Dispose() { }
    }

    private static float[] AudioBlock() => new float[4800]; // 0.1 s @ 48 kHz, decimates to ~1200

    private static bool SpinUntil(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return cond();
    }

    [Fact]
    public void Ft8_WedgedWorker_StaysStalled_ThenWatchdogResumesDecoding()
    {
        var now = new DateTime(2026, 6, 27, 0, 0, 0, DateTimeKind.Utc);
        Func<DateTime> clock = () => now;

        int created = 0;
        using var firstWedge = new ManualResetEventSlim(false);
        var decoders = new List<FakeFt8Decoder>();
        Func<IFt8Decoder> factory = () =>
        {
            // Only the FIRST decoder wedges; the rebuilt one decodes normally.
            var d = new FakeFt8Decoder(created == 0 ? firstWedge : null);
            created++;
            decoders.Add(d);
            return d;
        };

        var batches = new ConcurrentQueue<Ft8DecodeBatch>();
        using var svc = new Ft8Service(factory, () => now);
        svc.DecodesReady += b => batches.Enqueue(b);

        Assert.True(svc.Enable(0, Ft8Protocol.Ft8));

        // Drive a slot to completion so the (wedging) worker picks it up.
        svc.FeedAudio(0, 48_000, AudioBlock());          // first block of slot N
        now = now.AddSeconds(15);
        svc.FeedAudio(0, 48_000, AudioBlock());          // crosses into N+1 -> slot N queued
        Assert.True(SpinUntil(() => decoders.Count >= 1 && decoders[0].DecodeEntered),
            "worker never started decoding the first slot");

        // The worker is now wedged inside Decode. Keep audio flowing and complete
        // more slots: WITHOUT the watchdog, decode stays dead forever.
        now = now.AddSeconds(15);
        svc.FeedAudio(0, 48_000, AudioBlock());
        now = now.AddSeconds(15);
        svc.FeedAudio(0, 48_000, AudioBlock());
        Assert.False(SpinUntil(() => !batches.IsEmpty, 300),
            "decode should be wedged (no batch) before the watchdog runs");

        // Audio is fresh; the worker has made no progress for > 2.5 slots (37.5 s).
        now = now.AddSeconds(20); // ~50 s since the wedge began
        svc.FeedAudio(0, 48_000, AudioBlock());           // keep audio "fresh"
        Assert.True(svc.RunWatchdogOnce(now), "watchdog should detect the stall and rebuild");
        Assert.True(created >= 2, "watchdog should have built a fresh decoder");

        // A fresh session is running. Drive a new slot to completion and assert
        // decode RESUMED — with no disable/enable and no TX.
        now = now.AddSeconds(15);
        svc.FeedAudio(0, 48_000, AudioBlock());
        now = now.AddSeconds(15);
        svc.FeedAudio(0, 48_000, AudioBlock());
        Assert.True(SpinUntil(() => !batches.IsEmpty),
            "decode did not resume after the watchdog rebuilt the worker");

        firstWedge.Set(); // release the leaked wedged worker thread
    }

    [Fact]
    public void Ft8_Watchdog_NoOp_WhenWorkerHealthy()
    {
        var now = new DateTime(2026, 6, 27, 1, 0, 0, DateTimeKind.Utc);
        using var svc = new Ft8Service(() => new FakeFt8Decoder(null), () => now);
        var batches = new ConcurrentQueue<Ft8DecodeBatch>();
        svc.DecodesReady += b => batches.Enqueue(b);
        Assert.True(svc.Enable(0, Ft8Protocol.Ft8));

        // Feed a healthy stream that produces a decode each slot.
        for (int i = 0; i < 4; i++)
        {
            svc.FeedAudio(0, 48_000, AudioBlock());
            now = now.AddSeconds(15);
            svc.FeedAudio(0, 48_000, AudioBlock());
            Assert.True(SpinUntil(() => batches.Count >= i + 1), $"slot {i} did not decode");
            // Worker is making progress -> watchdog must NOT rebuild.
            Assert.False(svc.RunWatchdogOnce(now), "watchdog fired on a healthy worker");
        }
    }

    [Fact]
    public void Ft8_ReEnableSameParams_IsIdempotent_DoesNotDropDecoder()
    {
        var now = new DateTime(2026, 6, 27, 2, 0, 0, DateTimeKind.Utc);
        int created = 0;
        using var svc = new Ft8Service(() => { created++; return new FakeFt8Decoder(null); }, () => now);
        Assert.True(svc.Enable(0, Ft8Protocol.Ft8));
        Assert.Equal(1, created);

        // Re-enabling on the same receiver+protocol (setPasses / hydration churn)
        // must NOT tear the session down and build a new decoder.
        Assert.True(svc.Enable(0, Ft8Protocol.Ft8));
        Assert.True(svc.Enable(0, Ft8Protocol.Ft8));
        Assert.Equal(1, created);

        // Switching protocol DOES rebuild (FT8/FT4 sub-bands differ).
        Assert.True(svc.Enable(0, Ft8Protocol.Ft4));
        Assert.Equal(2, created);
    }

    [Fact]
    public void Ft8_RepeatedReconfigureWhileEnabled_KeepsDecoding()
    {
        // The actual field failure path: the pop-out band-follow effect re-issues
        // configure-while-enabled churn. Interleaving redundant same-params Enable
        // calls with the live audio stream must NEVER drop the decoder — decode has
        // to keep producing a batch every slot, with the decoder built exactly once.
        var now = new DateTime(2026, 6, 27, 4, 0, 0, DateTimeKind.Utc);
        int created = 0;
        using var svc = new Ft8Service(() => { created++; return new FakeFt8Decoder(null); }, () => now);
        var batches = new ConcurrentQueue<Ft8DecodeBatch>();
        svc.DecodesReady += b => batches.Enqueue(b);
        Assert.True(svc.Enable(0, Ft8Protocol.Ft8));

        for (int i = 0; i < 3; i++)
        {
            svc.Enable(0, Ft8Protocol.Ft8);   // redundant reconfigure (band-follow churn)
            svc.Enable(0, Ft8Protocol.Ft8);
            svc.FeedAudio(0, 48_000, AudioBlock());
            now = now.AddSeconds(15);
            svc.FeedAudio(0, 48_000, AudioBlock());
            Assert.True(SpinUntil(() => batches.Count >= i + 1),
                $"decode stopped after reconfigure round {i}");
            Assert.False(svc.RunWatchdogOnce(now), "watchdog fired despite a healthy worker");
        }
        Assert.Equal(1, created); // never rebuilt across all the reconfigure churn
    }

    [Fact]
    public void Ft8_PersistentStall_RebuildsAreBounded_ThenDegraded_ClearedByReEnable()
    {
        // If a stall NEVER clears (a truly-hung native decode), the watchdog must
        // NOT rebuild on every tick forever — each rebuild abandons one worker +
        // native context. Prove rebuilds are capped, a degraded state is surfaced,
        // and an operator disable->enable clears it.
        var now = new DateTime(2026, 6, 27, 3, 0, 0, DateTimeKind.Utc);
        int created = 0;
        using var svc = new Ft8Service(() => { created++; return new FakeFt8Decoder(null); }, () => now);
        Assert.True(svc.Enable(0, Ft8Protocol.Ft8));
        Assert.Equal(1, created);

        int rebuilds = 0;
        for (int i = 0; i < 12; i++)
        {
            now = now.AddSeconds(50);                // progress goes stale (>37.5 s)
            svc.FeedAudio(0, 48_000, AudioBlock());  // fresh audio into a fresh accumulator -> no slot completes -> worker never makes progress
            if (svc.RunWatchdogOnce(now)) rebuilds++;
        }

        Assert.True(rebuilds <= 5, $"watchdog rebuilt {rebuilds} times — leak is not bounded");
        Assert.True(svc.IsDegraded, "a persistent stall should surface a degraded state");
        Assert.False(svc.RunWatchdogOnce(now.AddSeconds(50)), "degraded watchdog must not keep rebuilding");

        // Operator disable->enable is the manual recovery — it clears degraded.
        svc.Disable();
        Assert.True(svc.Enable(0, Ft8Protocol.Ft8));
        Assert.False(svc.IsDegraded, "re-enable should clear the degraded state");
    }

    // --- WSPR mirror ---

    [Fact]
    public void Wspr_WedgedWorker_WatchdogResumesDecoding()
    {
        var now = new DateTime(2026, 6, 27, 0, 0, 0, DateTimeKind.Utc);
        using var firstWedge = new ManualResetEventSlim(false);
        var spots = new ConcurrentQueue<WsprSpotBatch>();
        int decodeCalls = 0;
        Func<float[], double, IReadOnlyList<WsprSpot>> decodeFn = (s, d) =>
        {
            int n = Interlocked.Increment(ref decodeCalls);
            if (n == 1) firstWedge.Wait(); // first decode wedges
            return new[] { new WsprSpot(-20f, 0.5f, (float)(d + 1.5e-6), 0, "RK9AX KO85 37") };
        };

        using var svc = new WsprService(decodeFn, () => now);
        svc.SpotsReady += b => spots.Enqueue(b);
        Assert.True(svc.Enable(0, 14.0956));

        svc.FeedAudio(0, 48_000, AudioBlock());
        now = now.AddSeconds(120);
        svc.FeedAudio(0, 48_000, AudioBlock());           // slot queued -> worker wedges
        Assert.True(SpinUntil(() => Volatile.Read(ref decodeCalls) >= 1),
            "WSPR worker never started decoding");

        now = now.AddSeconds(120);
        svc.FeedAudio(0, 48_000, AudioBlock());
        Assert.False(SpinUntil(() => !spots.IsEmpty, 300), "WSPR should be wedged before watchdog");

        // > 2.5 slots (300 s) since the worker's last progress, with audio fresh.
        now = now.AddSeconds(200);
        svc.FeedAudio(0, 48_000, AudioBlock());
        Assert.True(svc.RunWatchdogOnce(now), "WSPR watchdog should rebuild the wedged worker");

        now = now.AddSeconds(120);
        svc.FeedAudio(0, 48_000, AudioBlock());
        now = now.AddSeconds(120);
        svc.FeedAudio(0, 48_000, AudioBlock());
        Assert.True(SpinUntil(() => !spots.IsEmpty), "WSPR decode did not resume after rebuild");

        firstWedge.Set();
    }

    [Fact]
    public void Wspr_ReEnableSameDial_KeepsDecoding_NewDialRetunes()
    {
        // Mirror of the FT8 idempotency test: redundant same-dial Enable churn must
        // not disturb the worker (decode keeps producing), while a NEW dial retunes.
        var now = new DateTime(2026, 6, 27, 5, 0, 0, DateTimeKind.Utc);
        var spots = new ConcurrentQueue<WsprSpotBatch>();
        Func<float[], double, IReadOnlyList<WsprSpot>> decodeFn =
            (s, d) => new[] { new WsprSpot(-20f, 0.5f, (float)(d + 1.5e-6), 0, "RK9AX KO85 37") };
        using var svc = new WsprService(decodeFn, () => now);
        svc.SpotsReady += b => spots.Enqueue(b);

        Assert.True(svc.Enable(0, 14.0956));
        Assert.True(svc.Enable(0, 14.0956)); // redundant same dial — no-op
        Assert.True(svc.Enable(0, 14.0956));
        Assert.Equal(14.0956, svc.DialFreqMhz);

        svc.FeedAudio(0, 48_000, AudioBlock());
        now = now.AddSeconds(120);
        svc.FeedAudio(0, 48_000, AudioBlock());
        Assert.True(SpinUntil(() => !spots.IsEmpty), "WSPR decode stopped after redundant re-enable");
        Assert.False(svc.RunWatchdogOnce(now), "WSPR watchdog fired on a healthy worker");

        Assert.True(svc.Enable(0, 7.0386)); // a NEW dial retunes the session
        Assert.Equal(7.0386, svc.DialFreqMhz);
    }
}
