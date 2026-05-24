// SPDX-License-Identifier: GPL-2.0-or-later
//
// Regression tests for the TX→RX RX-audio resume fix (issue #468).
//
// Root cause: during TX the WDSP RXA is damped, so DspPipelineService.Tick
// read 0 audio samples and published NOTHING. The producer side stopped
// feeding NativeAudioSink's ring entirely for the whole key-down; the ring
// ran dry and the OS output endpoint idled. On un-key the backend had fresh
// RX audio in ~13 ms (proven by the rx.resume.probe t1/t2/t3 timings) but the
// starved output endpoint took ~2 s to wake — the whole perceived gap.
//
// Fix: DspPipelineService.PublishKeepWarmSilence feeds one tick's worth of
// silence to the RX sinks whenever the RX channel produced no audio (and TX
// monitor is off), so the ring stays non-empty and the endpoint never idles —
// mirroring Thetis keeping its RX audio mixer fed continuously through TX.
//
// These tests pin the keep-warm behaviour against a real NativeAudioSink so a
// future change that stops feeding the ring during TX fails here.

using Microsoft.Extensions.Logging.Abstractions;
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
    // miniaudio device.
    private sealed class CountingSink : IRxAudioSink
    {
        public long TotalSamples;
        public int Frames;
        public void Publish(in AudioFrame frame)
        {
            TotalSamples += frame.SampleCount;
            Frames++;
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
        // The first 1600 scratch samples must have been zeroed before publish.
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
}
