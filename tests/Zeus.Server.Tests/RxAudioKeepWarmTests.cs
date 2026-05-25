// SPDX-License-Identifier: GPL-2.0-or-later
//
// Regression tests for the TX→RX RX-audio resume fix (issue #468, revived on
// top of the #521 WASAPI low-latency fix).
//
// Root cause: during TX the WDSP RXA is damped, so DspPipelineService.Tick
// read 0 audio samples and published NOTHING. The producer side stopped
// feeding NativeAudioSink's ring entirely for the whole key-down; the ring
// ran dry and the OS output endpoint idled. On un-key the backend had fresh
// RX audio in ~13 ms (proven by the rx.resume.probe t1/t2/t3 timings) but the
// starved output endpoint — especially an RDP "Remote Audio" channel — took
// ~2 s to wake from silence, which is the whole perceived gap. #521 shrank the
// WASAPI output buffer to ~20 ms but did NOT fix the wake-from-silence.
//
// Fix: DspPipelineService.PublishKeepWarmSilence feeds one tick's worth of
// silence to the RX sinks whenever the RX channel produced no audio (and TX
// monitor is off), so the ring stays non-empty and the endpoint never idles —
// mirroring Thetis keeping its RX audio mixer fed continuously through TX. The
// feed rate matches device consumption so on #521's tiny buffer it tracks the
// drain and does not accumulate a backlog.
//
// These tests pin the keep-warm behaviour against a real NativeAudioSink so a
// future change that stops feeding the ring during TX fails here. They also
// pin the "both edges" contract: the rising edge (RX→TX) still drains the ring
// to instant silence, and throughout TX the keep-warm feed keeps it warm.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class RxAudioKeepWarmTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-keepwarm-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private DspPipelineService BuildPipeline(params IRxAudioSink[] sinks)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        return new DspPipelineService(radio, hub, sinks, loggerFactory);
    }

    // Minimal sink that records the total samples published — lets us assert
    // the keep-warm feed actually reached the sink without standing up a real
    // miniaudio device. Captures the last block so we can confirm it's silence.
    private sealed class CountingSink : IRxAudioSink
    {
        public long TotalSamples;
        public int Frames;
        public float MaxAbsLastFrame;
        public void Publish(in AudioFrame frame)
        {
            TotalSamples += frame.SampleCount;
            Frames++;
            float max = 0f;
            var span = frame.Samples.Span;
            for (int i = 0; i < span.Length; i++)
            {
                float a = Math.Abs(span[i]);
                if (a > max) max = a;
            }
            MaxAbsLastFrame = max;
        }
    }

    [Fact]
    public void PublishKeepWarmSilence_FeedsOneTickOfSilence_ToSink()
    {
        var sink = new CountingSink();
        var pipeline = BuildPipeline(sink);
        var scratch = new float[2048];
        // Pre-fill with a non-zero pattern so we can confirm the published
        // block is actually silence (zeroed), not stale audio.
        for (int i = 0; i < scratch.Length; i++) scratch[i] = 0.5f;

        pipeline.PublishKeepWarmSilence(scratch, nowMs: 0);

        // 48 kHz / 30 Hz = 1600 samples per tick — matches device consumption.
        Assert.Equal(1, sink.Frames);
        Assert.Equal(1600, sink.TotalSamples);
        // The published block must be silence (all samples zeroed before send).
        Assert.Equal(0f, sink.MaxAbsLastFrame);
        // ...and the first 1600 scratch samples must have been zeroed in place.
        for (int i = 0; i < 1600; i++)
            Assert.Equal(0f, scratch[i]);
    }

    [Fact]
    public void PublishKeepWarmSilence_KeepsNativeSinkRingWarm()
    {
        // The load-bearing invariant: feeding keep-warm silence leaves the
        // NativeAudioSink ring NON-EMPTY, so the miniaudio playback callback
        // reads real data instead of underrunning the device into an idle
        // state. This is the consumer-side guarantee the resume fix depends on.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var pipeline = BuildPipeline(sink);
        var scratch = new float[2048];

        Assert.Equal(0, sink.CurrentRingDepth);   // starts dry

        pipeline.PublishKeepWarmSilence(scratch, nowMs: 0);

        // Ring now holds the silence block — the device will not starve.
        Assert.Equal(1600, sink.CurrentRingDepth);
    }

    [Fact]
    public void PublishKeepWarmSilence_NoSinks_IsNoOp()
    {
        // With no audio sinks (web mode), keep-warm must be a cheap no-op —
        // no allocation churn, no throw.
        var pipeline = BuildPipeline(); // no sinks
        var scratch = new float[2048];
        var ex = Record.Exception(() => pipeline.PublishKeepWarmSilence(scratch, nowMs: 0));
        Assert.Null(ex);
    }

    [Fact]
    public void PublishKeepWarmSilence_RepeatedFeeds_AccumulateInRing()
    {
        // Repeated ticks (a sustained key-down) keep topping the ring up so it
        // never drains — the steady-state behaviour during a long TX.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var pipeline = BuildPipeline(sink);
        var scratch = new float[2048];

        pipeline.PublishKeepWarmSilence(scratch, nowMs: 0);
        pipeline.PublishKeepWarmSilence(scratch, nowMs: 33);
        pipeline.PublishKeepWarmSilence(scratch, nowMs: 66);

        // Three ticks × 1600 = 4800 samples, all still queued (well under the
        // 65 536 ring capacity, nothing consuming in the test).
        Assert.Equal(4800, sink.CurrentRingDepth);
    }

    [Fact]
    public void BothEdges_RisingDrains_ThenKeepWarmRefillsThroughTx()
    {
        // The "both edges" contract for the resume fix:
        //   1. RISING edge (RX→TX): OnTxActiveChanged(true) drains the ring so
        //      the operator hears instant silence (RX→TX stays instant).
        //   2. THROUGHOUT TX: keep-warm silence refills the ring every tick so
        //      the output endpoint never idles and the un-key wake-from-silence
        //      gap is gone.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var pipeline = BuildPipeline(sink);
        var scratch = new float[2048];

        // Pre-TX: some band RX audio queued in the ring.
        var rx = new float[1600];
        for (int i = 0; i < rx.Length; i++) rx[i] = 0.25f;
        sink.Publish(new AudioFrame(
            Seq: 1, TsUnixMs: 0, RxId: 0, Channels: 1,
            SampleRateHz: 48_000, SampleCount: (ushort)rx.Length,
            Samples: rx));
        Assert.True(sink.CurrentRingDepth > 0);

        // Rising edge: ring drains to instant silence (RX→TX instant).
        sink.OnTxActiveChanged(true);
        Assert.Equal(0, sink.CurrentRingDepth);

        // Throughout TX (RXA damped → no RX audio): keep-warm feeds silence
        // every tick, so the ring stays non-empty and the endpoint stays warm.
        pipeline.PublishKeepWarmSilence(scratch, nowMs: 33);
        pipeline.PublishKeepWarmSilence(scratch, nowMs: 66);
        Assert.Equal(3200, sink.CurrentRingDepth);
    }
}

// Regression tests for the resume-probe provenance gate (issue #468). The
// probe must NOT stamp t4 on stale/keep-warm content before fresh RX audio
// has been published (t3) — otherwise it logs a misleading "t4=0.9ms while
// t1..t3=n/a" line and the real fresh-RX resume timing is lost. These live
// in the AudioResumeProbe collection so they serialise against the shared
// process-wide probe state.
[Collection("AudioResumeProbe")]
public class RxResumeProbeProvenanceTests
{
    public RxResumeProbeProvenanceTests() => AudioResumeProbe.ResetForTest();

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public void KeepWarmSilenceBeforePublish_DoesNotStampT4()
    {
        // Models the exact failure: after un-key, the keep-warm feed keeps the
        // ring fed and the playback worker reports audible output BEFORE the
        // fresh-audio pipeline has published (t3). t4 must not stamp; no line.
        var log = new CapturingLogger();
        AudioResumeProbe.ArmUnkey(log);
        AudioResumeProbe.MarkFirstIq();           // t1
        AudioResumeProbe.MarkFirstReadAudio();    // t2
        // (no t3 yet — only stale/keep-warm content has reached the device)
        AudioResumeProbe.MarkFirstAudibleOutput();

        Assert.Empty(log.Messages);

        // Now fresh RX audio is published and the next audible output is real.
        AudioResumeProbe.MarkFirstPublish();      // t3
        AudioResumeProbe.MarkFirstAudibleOutput();

        var lines = log.Messages.ToArray();
        Assert.Single(lines);
        // Every reachable stage fired, so the line carries meaningful timings.
        Assert.DoesNotContain("t1=n/a", lines[0]);
        Assert.DoesNotContain("t2=n/a", lines[0]);
        Assert.DoesNotContain("t3=n/a", lines[0]);
        Assert.DoesNotContain("t4=n/a", lines[0]);
        Assert.Contains("buffer-drain=", lines[0]);
    }
}
