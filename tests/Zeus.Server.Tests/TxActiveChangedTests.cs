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
    public void NativeAudioSink_OnTxActiveChanged_True_DoesNotForceDrainRing()
    {
        // #468 mute-don't-drain: TX-on must NOT synchronously clear the ring.
        // Thetis keeps its RX output stream warm and mutes at the mixer during
        // TX; force-draining the ring made the OS audio path re-prime from zero
        // on un-key (the ~1.7 s resume delay). The rising-edge handler now only
        // raises the mute flag — the ring is untouched until the warm playback
        // callback consumes it.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);
        var frame = BuildMonoFrame(600);
        sink.Publish(in frame);

        Assert.True(sink.CurrentRingDepth >= 600,
            $"setup precondition: ring should be filled. got {sink.CurrentRingDepth}");

        sink.OnTxActiveChanged(true);

        // The ring is NOT force-drained on the TX edge.
        Assert.True(sink.CurrentRingDepth >= 600,
            $"TX-on must not force-drain the ring; got {sink.CurrentRingDepth}");
    }

    [Fact]
    public void NativeAudioSink_WhileTxActive_PublishDropsRxAndOutputIsSilent()
    {
        // While keyed, no band RX may be enqueued and the playback callback
        // must output silence — even though the device stream stays warm.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);

        sink.OnTxActiveChanged(true);

        // Publish during TX is dropped at the door — nothing accumulates to
        // replay on un-key.
        var frame = BuildMonoFrame(600);
        sink.Publish(in frame);
        Assert.Equal(0, sink.CurrentRingDepth);

        // The playback callback yields pure silence while keyed.
        var output = new float[480];
        Array.Fill(output, 0.5f);   // poison so we can prove it was overwritten
        sink.RenderForTest(output, channels: 1);
        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void NativeAudioSink_OnUnkey_RxFlowsImmediately_FromWarmStream()
    {
        // The whole point of mute-don't-drain: after un-key, RX that was
        // published lands in a ring that was never force-emptied and reaches
        // the still-warm playback callback immediately — no re-prime gap.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);

        // Key up, then release.
        sink.OnTxActiveChanged(true);
        sink.OnTxActiveChanged(false);

        // Fresh RX after un-key.
        var frame = BuildMonoFrame(480);
        sink.Publish(in frame);
        Assert.True(sink.CurrentRingDepth >= 480);

        var output = new float[480];
        sink.RenderForTest(output, channels: 1);

        // RX is audible right away (0.1f samples from BuildMonoFrame).
        Assert.Contains(output, s => s != 0f);
    }

    [Fact]
    public void NativeAudioSink_OnTxActiveChanged_False_DoesNotMutateRing()
    {
        // Falling-edge TX (TX→RX) must NOT drain the ring — we want any
        // in-flight RX samples to play through immediately on a warm stream.
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
        // Hitting OnTxActiveChanged(true) twice in a row (e.g. MOX-on quickly
        // followed by TUN-on) must not throw or corrupt state — it just sets
        // an already-set flag. RX published during TX stays dropped either way.
        var sink = new NativeAudioSink(NullLogger<NativeAudioSink>.Instance);

        sink.OnTxActiveChanged(true);
        sink.OnTxActiveChanged(true);

        var frame = BuildMonoFrame(100);
        sink.Publish(in frame);
        Assert.Equal(0, sink.CurrentRingDepth);
    }
}
