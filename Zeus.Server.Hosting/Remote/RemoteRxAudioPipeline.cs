// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Concentus;
using Concentus.Enums;
using Zeus.Contracts;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Encodes the radio's RX audio into 20 ms Opus packets for the remote operator's
/// WebRTC audio track — the state-of-the-art low-latency, loss-concealed RX path
/// that replaces shipping raw 48 kHz f32 PCM over the unreliable data channel.
/// Opus on a media track gets the browser's native adaptive jitter buffer + PLC
/// + RTCP, and drops the wire rate ~50× (≈1.5 Mbps → ≈48 kbps), which is what
/// makes RX audio robust over a real internet path.
///
/// This is the exact mirror of <see cref="RemoteMicAudioPipeline"/> (which
/// DECODES the inbound voice track) running in the opposite direction. One
/// instance per remote session; NOT thread-safe — it is fed only from the single
/// <see cref="RemoteFrameSink"/> drain task.
/// </summary>
public sealed class RemoteRxAudioPipeline
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BlockSamples = 960;          // 20 ms @ 48 kHz — one Opus frame
    /// <summary>RTP timestamp units advanced per emitted packet (Opus clock = 48 kHz).</summary>
    public const uint BlockRtpUnits = BlockSamples;

    private const int MaxPacketBytes = 1275;      // largest Opus packet

    private readonly IOpusEncoder _encoder =
        OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
    private readonly short[] _pcm = new short[BlockSamples];
    private readonly byte[] _packet = new byte[MaxPacketBytes];
    private int _filled;
    private readonly Action<ReadOnlyMemory<byte>> _onPacket;

    /// <param name="onPacket">
    /// Invoked synchronously per completed 20 ms Opus packet. The buffer is reused
    /// between calls — copy it (e.g. <c>.ToArray()</c>) if retained past the call.
    /// </param>
    public RemoteRxAudioPipeline(Action<ReadOnlyMemory<byte>> onPacket)
    {
        _onPacket = onPacket;
        // Loss-resilient, low-latency settings tuned for an internet path. Wrapped
        // so an unavailable knob in a given Concentus build falls back to defaults
        // rather than failing the session.
        try
        {
            _encoder.Bitrate = 48000;
            _encoder.Complexity = 5;          // balance CPU (Raspberry Pi) vs quality
            _encoder.UseInbandFEC = true;     // forward error correction for packet loss
            _encoder.PacketLossPercent = 5;
        }
        catch { /* keep encoder defaults */ }
    }

    /// <summary>Feed one RX <see cref="AudioFrame"/>; emits 20 ms Opus packets as
    /// the 960-sample block fills. Stereo input is downmixed to mono.</summary>
    public void Encode(in AudioFrame frame)
    {
        var samples = frame.Samples.Span;
        int ch = frame.Channels < 1 ? 1 : frame.Channels;
        for (int i = 0; i + ch <= samples.Length; i += ch)
        {
            float v;
            if (ch == 1)
            {
                v = samples[i];
            }
            else
            {
                float sum = 0f;
                for (int c = 0; c < ch; c++) sum += samples[i + c];
                v = sum / ch;
            }
            int s = (int)(v * 32767f);
            _pcm[_filled++] = (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
            if (_filled == BlockSamples) Flush();
        }
    }

    private void Flush()
    {
        int n;
        // Never throw out of the drain path — a bad block is dropped, not fatal.
        try { n = _encoder.Encode(_pcm.AsSpan(0, BlockSamples), BlockSamples, _packet.AsSpan(), _packet.Length); }
        catch { _filled = 0; return; }
        _filled = 0;
        if (n > 0) _onPacket(_packet.AsMemory(0, n));
    }
}
