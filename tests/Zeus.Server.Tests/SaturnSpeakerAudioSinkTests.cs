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
