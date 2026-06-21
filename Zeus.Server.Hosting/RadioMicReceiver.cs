// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>
/// Decoder + 960-sample re-blocker for the Saturn radio-mic stream (UDP 1026,
/// external-ports plan, §3). The radio digitises its analog jack (mic / line-in
/// / balanced-XLR) and ships 64 samples per packet at 48 kHz; downstream
/// <see cref="TxAudioIngest.OnMicPcmBytesFromRadioMic"/> hard-rejects anything
/// that isn't exactly 960 samples / 3840 bytes (<c>MicBlockBytes</c>). A raw
/// 64-sample block would be counted as a dropped frame → dead air. So, exactly
/// as <see cref="Zeus.Server.Tci.TciTxAudioReceiver"/> does for arbitrary TCI
/// chunk sizes, this buffers the 64-sample/packet f32-mono stream and emits only
/// full 3840-byte blocks, carrying the &lt;960-sample remainder across packets.
/// No resampler — the 1026 stream is natively 48 kHz; only the block SIZE is
/// repacked.
///
/// Packet format (verified against pihpsdr <c>src/new_protocol.c</c>
/// <c>process_mic_data</c>, lines 2694-2714; <c>MIC_LINE_TO_HOST_PORT = 1026</c>,
/// <c>MIC_SAMPLES = 64</c>):
///   bytes 0..3  : 4-byte big-endian sequence number
///   bytes 4..131: 64 × int16 BIG-ENDIAN samples → f32 mono via × (1 / 2^15)
///   total = 4 + 64×2 = 132 bytes
///
/// Threading: <see cref="Accept"/> runs on the Protocol2Client RX loop thread.
/// The mono accumulator is guarded by <see cref="_sync"/> so a <see cref="Reset"/>
/// (issued on any source switch) doesn't tear a packet mid-decode. The forward
/// delegate is invoked under the lock — it lands in
/// <see cref="TxAudioIngest.OnMicPcmBytesFromRadioMic"/>, whose own _sync is a
/// different object, so there is no lock nesting on a shared monitor.
/// </summary>
internal sealed class RadioMicReceiver
{
    /// <summary>Sample count carried in one 1026 packet (pihpsdr MIC_SAMPLES).</summary>
    public const int PacketSamples = 64;
    /// <summary>Header (4 B seq) + 64 × int16 = 132 bytes.</summary>
    public const int PacketBytes = 4 + PacketSamples * 2;

    /// <summary>Mic block size <see cref="TxAudioIngest"/> consumes (20 ms @ 48 kHz).</summary>
    public const int OutputBlockSamples = 960;
    private const int OutputBlockBytes = OutputBlockSamples * 4;

    // Worst-case headroom: 960-sample block + one full 64-sample packet.
    private const int AccumulatorCapacity = OutputBlockSamples + PacketSamples + 64;

    // int16 full-scale → f32. Matches pihpsdr's literal 0.00003051 (≈ 1/32768).
    private const float Int16ToFloat = 1.0f / 32768.0f;

    private readonly Action<ReadOnlyMemory<byte>> _forward;
    private readonly ILogger _log;

    private readonly object _sync = new();
    private readonly float[] _monoAccumulator = new float[AccumulatorCapacity];
    private int _monoFill;
    private readonly byte[] _outputBuffer = new byte[OutputBlockBytes];

    private long _totalPacketsAccepted;
    private long _totalPacketsDropped;
    private long _totalBlocksForwarded;

    public RadioMicReceiver(Action<ReadOnlyMemory<byte>> forwardF32leMicBlock, ILogger log)
    {
        _forward = forwardF32leMicBlock ?? throw new ArgumentNullException(nameof(forwardF32leMicBlock));
        _log = log;
    }

    public long TotalPacketsAccepted { get { lock (_sync) return _totalPacketsAccepted; } }
    public long TotalPacketsDropped { get { lock (_sync) return _totalPacketsDropped; } }
    public long TotalBlocksForwarded { get { lock (_sync) return _totalBlocksForwarded; } }

    /// <summary>
    /// Decode one UDP-1026 packet (4-byte BE seq + 64 × int16 BE @ 48 kHz),
    /// append the 64 mono samples to the re-block accumulator, and forward every
    /// full 960-sample block. The sequence header is skipped — gap detection is
    /// not needed for TX audio (a dropped packet is a 1.3 ms silence the
    /// accumulator simply doesn't see).
    /// </summary>
    public void Accept(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketBytes)
        {
            lock (_sync) _totalPacketsDropped++;
            return;
        }

        lock (_sync)
        {
            if (_monoFill + PacketSamples > _monoAccumulator.Length)
            {
                // Producer outran the WDSP consumer (shouldn't happen at 48 kHz
                // / 64-sample cadence vs. WDSP block drain). Flush + drop.
                _log.LogWarning("radiomic overflow fill={Fill} cap={Cap}", _monoFill, _monoAccumulator.Length);
                _monoFill = 0;
                _totalPacketsDropped++;
                return;
            }

            int b = 4; // skip the 4-byte BE sequence header
            for (int i = 0; i < PacketSamples; i++, b += 2)
            {
                short s = BinaryPrimitives.ReadInt16BigEndian(packet.Slice(b, 2));
                _monoAccumulator[_monoFill + i] = s * Int16ToFloat;
            }
            _monoFill += PacketSamples;
            _totalPacketsAccepted++;

            int writeOffset = 0;
            while (_monoFill - writeOffset >= OutputBlockSamples)
            {
                for (int i = 0; i < OutputBlockSamples; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(
                        _outputBuffer.AsSpan(i * 4, 4),
                        _monoAccumulator[writeOffset + i]);
                }
                _forward(_outputBuffer);
                writeOffset += OutputBlockSamples;
                _totalBlocksForwarded++;
            }

            // Shift the remainder (< 960 samples) to the head.
            int remainder = _monoFill - writeOffset;
            if (remainder > 0 && writeOffset > 0)
                Array.Copy(_monoAccumulator, writeOffset, _monoAccumulator, 0, remainder);
            _monoFill = remainder;
        }
    }

    /// <summary>
    /// Drop any in-flight (&lt; 960-sample) remainder. Called on ANY TX-audio
    /// source switch so pre-switch radio audio is never stitched onto the
    /// post-switch source (external-ports plan, §3: "clear/reset this
    /// accumulator on any source switch").
    /// </summary>
    public void Reset()
    {
        lock (_sync) _monoFill = 0;
    }
}
