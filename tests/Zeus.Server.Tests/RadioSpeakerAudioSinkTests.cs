// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Gating coverage for the Protocol-1 radio-speaker sink. The sink only feeds
// the RxAudioRing when ALL hold: opted in, a P1 client is connected (so the
// ring is drained), the board has a codec (not HL2), not transmitting, and the
// frame is 48 kHz mono. The positive "writes to a connected G2/Hermes" path is
// integration/bench (needs a live P1 connection); these unit tests pin the
// negative gates that must never leak audio onto the wire by mistake.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server.Tests;

public class RadioSpeakerAudioSinkTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-spksink-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
        try { if (File.Exists(_dbPath + ".dsp")) File.Delete(_dbPath + ".dsp"); } catch { }
        try { if (File.Exists(_dbPath + ".spk")) File.Delete(_dbPath + ".spk"); } catch { }
    }

    private RadioService NewDisconnectedRadio() =>
        new RadioService(
            NullLoggerFactory.Instance,
            new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp"),
            new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa"));

    private RadioSpeakerSettingsStore NewSettings() =>
        new(NullLogger<RadioSpeakerSettingsStore>.Instance, _dbPath + ".spk");

    private static AudioFrame MonoFrame(int samples = 64, byte channels = 1, uint rateHz = 48_000)
    {
        var buf = new float[samples * channels];
        for (int i = 0; i < buf.Length; i++) buf[i] = 0.5f;
        return new AudioFrame(
            Seq: 1, TsUnixMs: 0, RxId: 0,
            Channels: channels, SampleRateHz: rateHz,
            SampleCount: (ushort)samples, Samples: buf);
    }

    [Fact]
    public void Disabled_ByDefault_DoesNotWrite()
    {
        using var radio = NewDisconnectedRadio();
        using var settings = NewSettings();
        var ring = new RxAudioRing();
        var sink = new RadioSpeakerAudioSink(radio, ring, settings);

        sink.Publish(MonoFrame());

        Assert.Equal(0, ring.Count);   // default off — nothing forwarded
    }

    [Fact]
    public void Enabled_ButNoProtocol1Connection_DoesNotWrite()
    {
        using var radio = NewDisconnectedRadio();
        using var settings = NewSettings();
        settings.Set(true);
        var ring = new RxAudioRing();
        var sink = new RadioSpeakerAudioSink(radio, ring, settings);

        sink.Publish(MonoFrame());

        // No P1 client → ring would never be drained → must not feed it.
        Assert.Equal(0, ring.Count);
        Assert.False(sink.AvailableForConnectedBoard());
    }

    [Theory]
    [InlineData((byte)2, 48_000u)]   // stereo — not the RX mono frame
    [InlineData((byte)1, 44_100u)]   // wrong sample rate
    public void WrongFormat_DoesNotWrite(byte channels, uint rateHz)
    {
        using var radio = NewDisconnectedRadio();
        using var settings = NewSettings();
        settings.Set(true);
        var ring = new RxAudioRing();
        var sink = new RadioSpeakerAudioSink(radio, ring, settings);

        sink.Publish(MonoFrame(channels: channels, rateHz: rateHz));

        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void DisablingSettings_ClearsBufferedAudio()
    {
        using var radio = NewDisconnectedRadio();
        using var settings = NewSettings();
        var ring = new RxAudioRing();
        // Sink subscribes to settings.Changed and clears the ring on disable.
        _ = new RadioSpeakerAudioSink(radio, ring, settings);

        // Simulate a buffered tail (e.g. from a prior session) then flip off.
        ring.Write(new float[] { 0.5f, 0.5f, 0.5f });
        settings.Set(true);
        settings.Set(false);

        Assert.Equal(0, ring.Count);
    }

    // Issue #1122 — under Protocol 2 the speaker-output toggle must surface so
    // operators of a P2 codec radio (e.g. ANAN-10E / HermesII firmware) can
    // route audio to the radio's headphone jack. The actual wire path is
    // SaturnSpeakerAudioSink (UDP port 1028); this sink owns the P1 path but
    // shares the "available" reporting so /api/radio/speaker-output lights up
    // the single Settings → Radio toggle regardless of protocol.
    [Fact]
    public void AvailableForConnectedBoard_True_OnP2CodecBoard()
    {
        using var radio = NewDisconnectedRadio();
        using var settings = NewSettings();
        var ring = new RxAudioRing();
        var sink = new RadioSpeakerAudioSink(radio, ring, settings);

        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);

        Assert.True(sink.AvailableForConnectedBoard());
    }

    [Fact]
    public void Publish_OnP2_DoesNotWriteP1Ring()
    {
        using var radio = NewDisconnectedRadio();
        using var settings = NewSettings();
        settings.Set(true);
        var ring = new RxAudioRing();
        var sink = new RadioSpeakerAudioSink(radio, ring, settings);

        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesII);
        sink.Publish(MonoFrame());

        // P2 path is handled by SaturnSpeakerAudioSink; the P1 ring has no
        // consumer in this mode and must stay empty so nothing piles up.
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void AvailableForConnectedBoard_False_OnHl2()
    {
        using var radio = NewDisconnectedRadio();
        using var settings = NewSettings();
        var ring = new RxAudioRing();
        var sink = new RadioSpeakerAudioSink(radio, ring, settings);

        radio.MarkProtocol2Connected("127.0.0.1:1024", 192_000, client: null, boardKind: HpsdrBoardKind.HermesLite2);

        Assert.False(sink.AvailableForConnectedBoard());
    }
}
