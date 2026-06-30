// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class SaturnSpeakerAudioSinkTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-saturnspk-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".pa", ".dsp", ".spk" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.NegativeInfinity, -32768)]
    [InlineData(-2.0f, -32768)]
    [InlineData(-1.0f, -32768)]
    [InlineData(-0.5f, -16384)]
    [InlineData(0.0f, 0)]
    [InlineData(0.5f, 16384)]
    [InlineData(1.0f, 32767)]
    [InlineData(2.0f, 32767)]
    [InlineData(float.PositiveInfinity, 32767)]
    public void FloatToPcm16_ClampsToFullSignedAudioRange(float sample, short expected)
    {
        Assert.Equal(expected, SaturnSpeakerAudioSink.FloatToPcm16(sample));
    }

    // Issue #1122 — the P2 speaker sink must respect the operator opt-in
    // (default OFF) so a Zeus user who already hears RX audio host-side doesn't
    // get doubled output when they connect to a codec radio over P2.
    [Fact]
    public async Task DefaultDisabled_DoesNotOpenSocket()
    {
        using var radio = new RadioService(
            NullLoggerFactory.Instance,
            new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp"),
            new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa"));
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        await sink.StartAsync(default);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);

        // Default-off: publishing audio must not have opened the UDP target.
        var frame = MakeMonoFrame(64);
        sink.Publish(frame);
        WaitForIdle(sink);

        Assert.False(IsSocketOpen(sink));

        await sink.StopAsync(default);
        sink.Dispose();
    }

    [Fact]
    public async Task DisablingMidSession_ClosesSocket()
    {
        using var radio = new RadioService(
            NullLoggerFactory.Instance,
            new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp"),
            new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa"));
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        await sink.StartAsync(default);
        settings.Set(true);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);

        // One audio frame opens the socket lazily (handled on the worker).
        sink.Publish(MakeMonoFrame(64));
        WaitForIdle(sink);
        Assert.True(IsSocketOpen(sink));

        // Flip the operator opt-in off — the sink must release the socket so
        // sequence numbers reset cleanly on a later re-enable.
        settings.Set(false);
        WaitForIdle(sink);
        Assert.False(IsSocketOpen(sink));

        await sink.StopAsync(default);
        sink.Dispose();
    }

    // Issue #1122 audit — protocol cross-fire. The P2 UDP→1028 path must NOT
    // open under a non-Protocol-2 (P1) connection, even with the opt-in on and a
    // codec board: P1 firmware doesn't bind port 1028, and the P1 EP2 path
    // (RadioSpeakerAudioSink) already handles that connection.
    [Fact]
    public async Task NotProtocol2_DoesNotOpenSocket_EvenWhenEnabledAndCodecBoard()
    {
        using var radio = NewRadio();
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        await sink.StartAsync(default);
        settings.Set(true);
        // Connected to a codec radio, but NOT via Protocol 2.
        radio.MarkConnectedNonP2ForTest("127.0.0.1:1024");
        Assert.False(radio.IsProtocol2Active);

        sink.Publish(MakeMonoFrame(64));
        WaitForIdle(sink);
        Assert.False(IsSocketOpen(sink));

        await sink.StopAsync(default);
        sink.Dispose();
    }

    // HasOnboardCodec gate — HL2 has no stream codec, so the P2 speaker path
    // must stay closed for it even on P2 with the opt-in on.
    [Fact]
    public async Task HermesLite2_DoesNotOpenSocket_EvenWhenEnabled()
    {
        using var radio = NewRadio();
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        await sink.StartAsync(default);
        settings.Set(true);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesLite2);

        sink.Publish(MakeMonoFrame(64));
        WaitForIdle(sink);
        Assert.False(IsSocketOpen(sink));

        await sink.StopAsync(default);
        sink.Dispose();
    }

    // MOX mute — while transmitting, the P2 sink must not forward TX-monitor /
    // sidetone to the radio speaker (mirrors the P1 sink). The socket stays open
    // (opened on connect), but no packets are sent while keyed.
    [Fact]
    public async Task Mox_SendsNoPackets_WhileKeyed()
    {
        using var radio = NewRadio();
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        await sink.StartAsync(default);
        settings.Set(true);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);
        WaitForIdle(sink);

        radio.SetMox(true);
        // Keyed: a full packet's worth of frames must produce no outgoing packet.
        sink.Publish(MakeMonoFrame(64));
        WaitForIdle(sink);
        Assert.Equal(0u, SequenceOf(sink));

        radio.SetMox(false);
        // Unkey: RX audio flows out again (the 4-byte sequence advances).
        sink.Publish(MakeMonoFrame(64));
        WaitForIdle(sink);
        Assert.True(SequenceOf(sink) >= 1u, "expected at least one packet sent after unkey");

        await sink.StopAsync(default);
        sink.Dispose();
    }

    // Issue #1148 — the producer (DSP RX) thread must not be blocked on UDP
    // syscalls while the sender does its 16 packets per audio frame. Verify
    // Publish returns in well under a frame-time even when the socket target
    // is unreachable (so packets queue / drop on the worker side without
    // back-pressuring the DSP thread that also has to refill the host
    // soundcard ring on time).
    [Fact]
    public async Task Publish_DoesNotBlockProducerOnSocketWork()
    {
        using var radio = NewRadio();
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        await sink.StartAsync(default);
        settings.Set(true);
        // 192.0.2.0/24 is TEST-NET-1 (RFC 5737) — packets get dropped on the
        // way out, so the sender thread will spin retries / WOULD_BLOCK while
        // the producer keeps pushing.
        radio.MarkProtocol2Connected("192.0.2.7:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);
        WaitForIdle(sink);

        // 1024 samples is one DSP-tick batch at 48 kHz mono (~21.3 ms of audio).
        // Producing 50 of these back-to-back is ~1 s of audio; the call must
        // return in a small fraction of that since work is deferred.
        var frame = MakeMonoFrame(1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) sink.Publish(frame);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 200,
            $"Publish loop took {sw.ElapsedMilliseconds} ms; expected the DSP-thread path to be near-free");

        // ...and the deferred work must actually have happened: the sender
        // thread drained the ring and emitted packets (sequence advanced). A
        // regression where the worker silently skips all sends would keep the
        // timing assertion green but fail here.
        WaitForIdle(sink);
        Assert.True(SequenceOf(sink) > 0u,
            "expected the sender thread to have emitted packets after 50 Publish calls");

        await sink.StopAsync(default);
        sink.Dispose();
    }

    // Issue #1148 follow-up — the wake event is disposed in Dispose, but the
    // sink stays registered as an IRxAudioSink, so a late RX frame can still be
    // fanned to Publish during host shutdown. Signalling a disposed
    // ManualResetEventSlim must not throw ObjectDisposedException onto the
    // real-time audio thread.
    [Fact]
    public async Task Publish_AfterDispose_DoesNotThrow()
    {
        using var radio = NewRadio();
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        await sink.StartAsync(default);
        settings.Set(true);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);

        // Dispose without StopAsync — the harness can tear the sink down while
        // the DSP pipeline is still ticking frames at it.
        sink.Dispose();

        // A frame fanned in after disposal must be a no-op, not a crash on the
        // (now disposed) wake event.
        var ex = Record.Exception(() => sink.Publish(MakeMonoFrame(64)));
        Assert.Null(ex);
    }

    // Issue #1148 pacing (Deliverable 2) — egress must be SPREAD at the codec's
    // 750 pkt/s cadence, not fired in one burst per DSP tick. These drive a
    // virtual clock + feed the ring directly: no worker thread, no sockets, no
    // real time, fully deterministic.

    [Fact]
    public void Pacing_ReleasesOnePacketPerInterval_NeverBursts()
    {
        using var radio = NewRadio();
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        long t = 1_000_000_000;
        sink.ClockForTest = () => t;
        int sent = 0;
        sink.SentObserverForTest = _ => sent++;
        long interval = SaturnSpeakerAudioSink.PacketIntervalTicksForTest;

        // 100 packets of audio buffered up front (ring caps at 128 packets).
        sink.WriteRingForTest(new float[64 * 100]);

        // First drain anchors the schedule to "now": exactly one packet is due,
        // even though 100 are buffered. A regression to the old behaviour would
        // emit all 100 here.
        sink.DrainForTest();
        Assert.Equal(1, sent);

        // Each interval that elapses yields exactly one more packet.
        for (int i = 0; i < 50; i++)
        {
            t += interval;
            sink.DrainForTest();
        }

        Assert.Equal(51, sent);
        Assert.Equal(1, sink.MaxBurstForTest);
    }

    [Fact]
    public void Pacing_CatchUpIsBounded_DoesNotDumpWholeRing()
    {
        using var radio = NewRadio();
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        long t = 1_000_000_000;
        sink.ClockForTest = () => t;
        int sent = 0;
        sink.SentObserverForTest = _ => sent++;
        long interval = SaturnSpeakerAudioSink.PacketIntervalTicksForTest;
        long maxBehind = SaturnSpeakerAudioSink.MaxCatchupBehindTicksForTest;

        // Fill the ring to capacity (requesting far more than it holds).
        sink.WriteRingForTest(new float[64 * 400]);

        sink.DrainForTest();           // prime: schedule anchored, 1 packet out
        int afterPrime = sent;
        Assert.Equal(1, afterPrime);

        // Simulate a long scheduler stall, then a single drain. The catch-up
        // burst MUST be clamped to ~MaxCatchupBehindTicks / interval packets —
        // it must not dump the whole 128-packet ring at once.
        t += interval * 1000;
        sink.DrainForTest();

        int burst = sent - afterPrime;
        long cap = maxBehind / interval;
        Assert.InRange(burst, 1, (int)cap + 1);
        Assert.True(afterPrime + burst < 128,
            $"catch-up emitted {afterPrime + burst} packets — it dumped the whole ring");
    }

    [Fact]
    public void Pacing_RealignsAfterIdle_NoStoredBacklog()
    {
        using var radio = NewRadio();
        using var settings = new RadioSpeakerSettingsStore(
            NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");
        var sink = new SaturnSpeakerAudioSink(radio, settings, NullLogger<SaturnSpeakerAudioSink>.Instance);

        long t = 1_000_000_000;
        sink.ClockForTest = () => t;
        int sent = 0;
        sink.SentObserverForTest = _ => sent++;

        // Drain with an empty ring (idle), then let a long gap pass.
        sink.DrainForTest();
        Assert.Equal(0, sent);
        t += SaturnSpeakerAudioSink.PacketIntervalTicksForTest * 5000;

        // One packet of fresh audio must ship on the very next drain — the idle
        // gap must not have accrued a backlog of "due" slots (which would burst).
        sink.WriteRingForTest(new float[64]);
        sink.DrainForTest();
        Assert.Equal(1, sent);
    }

    private static uint SequenceOf(SaturnSpeakerAudioSink sink)
    {
        var f = typeof(SaturnSpeakerAudioSink)
            .GetField("_sequence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return f?.GetValue(sink) is uint v ? v : 0u;
    }

    // Wait for the sender thread (added in #1148) to flush every pending
    // signal — settings change, MOX flip, state change, queued audio — so
    // assertions on socket / sequence reflect the worker's view rather than
    // racing it. Tight timeout: every signal in these tests is a flag flip
    // that the worker handles in microseconds once it wakes.
    private static void WaitForIdle(SaturnSpeakerAudioSink sink)
    {
        Assert.True(sink.WaitForIdleForTest(TimeSpan.FromSeconds(2)),
            "sender thread did not become idle within 2 s");
    }

    private RadioService NewRadio() => new(
        NullLoggerFactory.Instance,
        new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp"),
        new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa"));

    private static AudioFrame MakeMonoFrame(int samples)
    {
        var buf = new float[samples];
        for (int i = 0; i < buf.Length; i++) buf[i] = 0.5f;
        return new AudioFrame(
            Seq: 1, TsUnixMs: 0, RxId: 0,
            Channels: 1, SampleRateHz: 48_000,
            SampleCount: (ushort)samples, Samples: buf);
    }

    private static bool IsSocketOpen(SaturnSpeakerAudioSink sink)
    {
        var field = typeof(SaturnSpeakerAudioSink)
            .GetField("_socket", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field is not null && field.GetValue(sink) is not null;
    }
}
