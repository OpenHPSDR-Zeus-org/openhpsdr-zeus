// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>
/// 960-sample re-blocker for the Protocol-1 codec mic / line-in stream
/// (issue #992 — ANAN-10E original "line-in selection silent" bug).
///
/// Unlike Protocol-2's <see cref="RadioMicReceiver"/> — which decodes a
/// dedicated UDP-1026 stream at a fixed 64-sample / 132-byte packet shape —
/// the Protocol-1 codec mic samples are embedded directly in the EP6 receive
/// frames (bytes 6..7 of every 8-byte sample group, see
/// <c>PacketParser.ExtractMicSamples</c>). Each EP6 packet carries 126 mic
/// samples; the codec rate is always 48 kHz, so at IQ rates above 48 kHz the
/// gateware duplicates each mic sample N = iqRateHz / 48000 times across
/// consecutive sample groups (Hermes USB protocol V1.58 NOTE 2).
///
/// Responsibilities:
///   - decimate raw 126-sample packets back to 48 kHz mono int16,
///   - convert int16 → f32 with the same 1/32768 scale Protocol-2 uses,
///   - accumulate into 960-sample (20 ms @ 48 kHz) blocks for
///     <see cref="TxAudioIngest.OnMicPcmBytesFromRadioMic"/>, which hard-rejects
///     anything that isn't exactly 960 samples / 3840 bytes.
///
/// Threading: <see cref="Accept"/> runs on the Protocol1Client RX loop thread.
/// The mono accumulator is guarded by <see cref="_sync"/> so a
/// <see cref="Reset"/> (issued on any TX-audio source switch) doesn't tear a
/// packet mid-decode. The forward delegate is invoked under the lock — it
/// lands in <see cref="TxAudioIngest.OnMicPcmBytesFromRadioMic"/>, whose own
/// _sync is a different object, so there is no lock nesting on a shared
/// monitor.
/// </summary>
internal sealed class P1RadioMicReceiver
{
    public const int OutputBlockSamples = 960;
    private const int OutputBlockBytes = OutputBlockSamples * 4;

    // Worst-case headroom: one 960-sample block + one full 48 kHz EP6 packet
    // (126 mic samples — the max we ever decimate to at 48 kHz IQ).
    private const int AccumulatorCapacity = OutputBlockSamples + 128;

    private const float Int16ToFloat = 1.0f / 32768.0f;

    private readonly Action<ReadOnlyMemory<byte>> _forward;
    private readonly ILogger _log;

    private readonly object _sync = new();
    private readonly float[] _monoAccumulator = new float[AccumulatorCapacity];
    private int _monoFill;
    private readonly byte[] _outputBuffer = new byte[OutputBlockBytes];

    private long _totalSamplesAccepted;
    private long _totalSamplesDropped;
    private long _totalBlocksForwarded;

    public P1RadioMicReceiver(Action<ReadOnlyMemory<byte>> forwardF32leMicBlock, ILogger log)
    {
        _forward = forwardF32leMicBlock ?? throw new ArgumentNullException(nameof(forwardF32leMicBlock));
        _log = log;
    }

    public long TotalSamplesAccepted { get { lock (_sync) return _totalSamplesAccepted; } }
    public long TotalSamplesDropped { get { lock (_sync) return _totalSamplesDropped; } }
    public long TotalBlocksForwarded { get { lock (_sync) return _totalBlocksForwarded; } }

    /// <summary>
    /// Accept one packet's worth of raw codec mic samples (int16, host endian
    /// from PacketParser.ExtractMicSamples). Decimates by N = iqRateHz / 48000
    /// down to the codec's native 48 kHz, converts to f32, accumulates, and
    /// forwards every full 960-sample block.
    /// </summary>
    public void Accept(ReadOnlySpan<short> samples, int iqRateHz)
    {
        if (samples.Length == 0) return;
        int decim = iqRateHz <= 48_000 ? 1 : Math.Max(1, iqRateHz / 48_000);

        lock (_sync)
        {
            for (int src = 0; src < samples.Length; src += decim)
            {
                if (_monoFill >= _monoAccumulator.Length)
                {
                    // Producer outran the WDSP consumer — flush + drop the
                    // tail of this packet. Should not happen at 48 kHz / WDSP
                    // 20 ms block cadence; here so a stuck consumer can't
                    // index past the buffer.
                    _log.LogWarning("p1.radiomic overflow fill={Fill} cap={Cap}", _monoFill, _monoAccumulator.Length);
                    _totalSamplesDropped += samples.Length - src;
                    _monoFill = 0;
                    return;
                }
                _monoAccumulator[_monoFill++] = samples[src] * Int16ToFloat;
                _totalSamplesAccepted++;
            }

            int writeOffset = 0;
            while (_monoFill - writeOffset >= OutputBlockSamples)
            {
                for (int i = 0; i < OutputBlockSamples; i++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                        _outputBuffer.AsSpan(i * 4, 4),
                        _monoAccumulator[writeOffset + i]);
                }
                _forward(_outputBuffer);
                writeOffset += OutputBlockSamples;
                _totalBlocksForwarded++;
            }

            int remainder = _monoFill - writeOffset;
            if (remainder > 0 && writeOffset > 0)
                Array.Copy(_monoAccumulator, writeOffset, _monoAccumulator, 0, remainder);
            _monoFill = remainder;
        }
    }

    /// <summary>Drop any in-flight (&lt; 960-sample) remainder. Called on any
    /// TX-audio source switch so pre-switch radio audio is never stitched onto
    /// the post-switch source.</summary>
    public void Reset()
    {
        lock (_sync) _monoFill = 0;
    }
}
