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

        // One audio frame opens the socket lazily.
        sink.Publish(MakeMonoFrame(64));
        Assert.True(IsSocketOpen(sink));

        // Flip the operator opt-in off — the sink must release the socket so
        // sequence numbers reset cleanly on a later re-enable.
        settings.Set(false);
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

        radio.SetMox(true);
        // Keyed: a full packet's worth of frames must produce no outgoing packet.
        sink.Publish(MakeMonoFrame(64));
        Assert.Equal(0u, SequenceOf(sink));

        radio.SetMox(false);
        // Unkey: RX audio flows out again (the 4-byte sequence advances).
        sink.Publish(MakeMonoFrame(64));
        Assert.True(SequenceOf(sink) >= 1u, "expected at least one packet sent after unkey");

        await sink.StopAsync(default);
        sink.Dispose();
    }

    private static uint SequenceOf(SaturnSpeakerAudioSink sink)
    {
        var f = typeof(SaturnSpeakerAudioSink)
            .GetField("_sequence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return f?.GetValue(sink) is uint v ? v : 0u;
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
