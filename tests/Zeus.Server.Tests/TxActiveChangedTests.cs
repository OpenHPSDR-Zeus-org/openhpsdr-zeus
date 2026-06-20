// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for TxService.TxActiveChanged + NativeAudioSink.OnTxActiveChanged.
// The pairing exists to drain the RX audio ring on TX rising edges, fixing
// the Windows-only "I hear RX for 2-3 seconds after pressing MOX" symptom
// reported in issue #403. On Mac / Linux the drain is a no-op (the ring is
// near-empty in steady state) but on Windows the WASAPI clock drifts vs the
// radio clock and the ring accumulates seconds of backlog over a session.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class TxActiveChangedTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-txactive-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private TxService BuildTxService()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        return new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
    }

    private static AudioFrame BuildMonoFrame(int sampleCount)
    {
        var samples = new float[sampleCount];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.1f;
        return new AudioFrame(
            Seq: 0,
            TsUnixMs: 0,
            RxId: 0,
            Channels: 1,
            SampleRateHz: 48_000,
            SampleCount: (ushort)sampleCount,
            Samples: samples);
    }

    [Fact]
    public void TxActiveChanged_DoesNotFire_WhenTrySetMoxRejectedOnNotConnected()
    {
        var tx = BuildTxService();
        int fires = 0;
        tx.TxActiveChanged += _ => fires++;

        // No radio is connected, so MOX-on is rejected by the connect
        // interlock BEFORE any state mutates. The event must NOT fire on
        // a no-op rejection, otherwise subscribers would clear their state
        // (in NativeAudioSink's case, drain the ring) every time the
        // operator misclicks the MOX button while disconnected.
        bool ok = tx.TrySetMox(true, out var err);

        Assert.False(ok);
        Assert.NotNull(err);
        Assert.Equal(0, fires);
    }

    [Fact]
    public void NativeAudioSink_OnTxActiveChanged_True_ClearsRing()
    {
        // Push samples into the sink so the ring is non-empty (~600 samples).
        // Then fire the TX-on event and assert the ring is drained.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var frame = BuildMonoFrame(600);
        sink.Publish(in frame);

        Assert.True(sink.CurrentRingDepth >= 600,
            $"setup precondition: ring should be filled. got {sink.CurrentRingDepth}");

        sink.OnTxActiveChanged(true);

        Assert.Equal(0, sink.CurrentRingDepth);
    }

    [Fact]
    public void NativeAudioSink_OnTxActiveChanged_False_DoesNotMutateRing()
    {
        // Falling-edge TX (TX→RX) must NOT drain the ring — there's nothing
        // useful to drain (the rising-edge handler already did) and we want
        // any in-flight RX samples to play through immediately.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var frame = BuildMonoFrame(600);
        sink.Publish(in frame);

        int depthBefore = sink.CurrentRingDepth;
        sink.OnTxActiveChanged(false);

        Assert.Equal(depthBefore, sink.CurrentRingDepth);
    }

    [Fact]
    public void NativeAudioSink_OnTxActiveChanged_RepeatedTrue_IsIdempotent()
    {
        // Belt-and-suspenders: hitting OnTxActiveChanged(true) twice in a
        // row (e.g. MOX-on quickly followed by TUN-on, both producing
        // rising edges in the combined state) must not throw or corrupt
        // ring state. Second call is just a redundant clear of an
        // already-empty ring.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var frame = BuildMonoFrame(100);
        sink.Publish(in frame);

        sink.OnTxActiveChanged(true);
        sink.OnTxActiveChanged(true);

        Assert.Equal(0, sink.CurrentRingDepth);
    }

    [Fact]
    public void NativeAudioSink_RebuffersUntilEnoughRxSamplesAreQueued()
    {
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        // Reference the cushion so the test tracks the constant (raised to ~60 ms
        // in issue #733) instead of hardcoding it.
        int prebuffer = sink.GetDiagnostics().PrebufferSamples;

        // A fragment below the cushion plays as silence (still rebuffering).
        var shortFrame = BuildMonoFrame(200);
        sink.Publish(in shortFrame);

        var output = new float[480 * 2];
        sink.RenderPlaybackForTest(output, 480, 2);

        Assert.All(output, s => Assert.Equal(0f, s));
        Assert.Equal(200, sink.CurrentRingDepth);

        // Refill past the cushion → playback resumes on the next render.
        var refill = BuildMonoFrame(prebuffer);
        sink.Publish(in refill);
        sink.RenderPlaybackForTest(output, 480, 2);

        Assert.Contains(output, s => s > 0f);
        Assert.Equal(200 + prebuffer - 480, sink.CurrentRingDepth);
    }

    [Fact]
    public void NativeAudioSink_RebuffersInsteadOfSplicingPartialRxTailIntoSilence()
    {
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        int prebuffer = sink.GetDiagnostics().PrebufferSamples;

        // Publish just over the cushion (with a tail that won't divide evenly
        // into 480-sample renders) so playback starts.
        int initial = prebuffer + 40;
        var frame = BuildMonoFrame(initial);
        sink.Publish(in frame);

        var output = new float[480 * 2];

        // Drain in 480-sample reads while a full render's worth remains.
        int depth = initial;
        while (depth >= 480)
        {
            sink.RenderPlaybackForTest(output, 480, 2);
            depth -= 480;
            Assert.Equal(depth, sink.CurrentRingDepth);
        }

        // Tail < one render → the sink rebuffers (silence) rather than splicing
        // the partial tail; the tail stays queued.
        sink.RenderPlaybackForTest(output, 480, 2);
        Assert.All(output, s => Assert.Equal(0f, s));
        Assert.Equal(depth, sink.CurrentRingDepth);
    }
}
