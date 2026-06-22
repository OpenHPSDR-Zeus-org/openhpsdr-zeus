// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers.Binary;
using Concentus;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Decodes the remote operator's Opus voice (the inbound WebRTC audio track)
/// into the front-end's native TX-mic cadence: 960-sample, 20 ms, 48 kHz mono
/// <c>f32le</c> blocks — byte-identical to what <see cref="TxAudioIngest"/>
/// consumes from a local <c>/ws</c> 0x20 mic frame. So a remote talker's voice
/// rides the exact same TX chain (leveler / CFC / VST / WDSP TXA) as someone
/// sitting at the desk.
///
/// One instance per remote session. NOT thread-safe: SIPSorcery delivers a
/// peer's RTP on a single receive path, and the emitted block buffer is reused,
/// so the <c>onBlock</c> consumer must read it synchronously (TxAudioIngest copies
/// the samples into its WDSP accumulator inside the call, satisfying this).
///
/// A mono Opus decoder is used deliberately: libopus/Concentus down-mixes a
/// stereo stream and passes a mono one through, so it is robust whether the
/// browser encodes 1 or 2 channels.
/// </summary>
public sealed class RemoteMicAudioPipeline
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BlockSamples = 960;          // 20 ms @ 48 kHz — matches TxAudioIngest.MicBlockSamples
    private const int BlockBytes = BlockSamples * 4;
    private const int MaxFrameSamples = 5760;     // 120 ms @ 48 kHz — the largest Opus frame

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
    private readonly short[] _decoded = new short[MaxFrameSamples];
    private readonly byte[] _block = new byte[BlockBytes];
    private int _blockBytes;
    private readonly Action<ReadOnlyMemory<byte>> _onBlock;

    /// <param name="onBlock">
    /// Invoked synchronously for each completed 960-sample f32le block. The buffer
    /// is reused between calls — copy it if you retain it past the call.
    /// </param>
    public RemoteMicAudioPipeline(Action<ReadOnlyMemory<byte>> onBlock) => _onBlock = onBlock;

    /// <summary>Decode one received Opus packet and emit any completed blocks.</summary>
    public void Decode(ReadOnlySpan<byte> opusPayload)
    {
        if (opusPayload.IsEmpty) return;
        int n;
        // Never throw out of the RTP receive callback — a malformed packet is
        // dropped, not fatal.
        try { n = _decoder.Decode(opusPayload, _decoded.AsSpan(), MaxFrameSamples, false); }
        catch { return; }
        Emit(_decoded.AsSpan(0, n));
    }

    /// <summary>
    /// Conceal a single lost packet via Opus PLC (decode with no input) and emit
    /// the synthesised samples. Call once per detected sequence-number gap so a
    /// dropped voice packet is a soft artefact, not a hard click.
    /// </summary>
    public void DecodeLost()
    {
        int n;
        try { n = _decoder.Decode(ReadOnlySpan<byte>.Empty, _decoded.AsSpan(0, BlockSamples), BlockSamples, false); }
        catch { return; }
        Emit(_decoded.AsSpan(0, n));
    }

    private void Emit(ReadOnlySpan<short> samples)
    {
        foreach (var s in samples)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_block.AsSpan(_blockBytes, 4), s / 32768f);
            _blockBytes += 4;
            if (_blockBytes == BlockBytes)
            {
                _onBlock(_block);
                _blockBytes = 0;
            }
        }
    }
}
