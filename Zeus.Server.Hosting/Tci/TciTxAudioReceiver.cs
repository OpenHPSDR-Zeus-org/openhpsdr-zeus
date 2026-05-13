// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers.Binary;

namespace Zeus.Server.Tci;

/// <summary>
/// Per-session decoder for inbound TCI TX audio binary frames
/// (StreamType = TxAudioStream / 2). Mixes stereo input to mono, repackages
/// into the 960-sample / 20 ms / 48 kHz f32le blocks expected by
/// <see cref="TxAudioIngest"/>, and forwards them to the same hot-path the
/// browser mic uplink uses.
///
/// Threading: <see cref="AcceptTxAudio"/> runs on the WebSocket receive
/// thread. The mono accumulator is guarded by <see cref="_sync"/> so that
/// <see cref="Reset"/> calls (issued on MOX falling edge by the session)
/// don't tear a frame mid-decode.
///
/// Frame-size decoupling: TCI clients send TX audio in arbitrary block sizes
/// (default 2048 frames / 4096 stereo floats from the v2.5.1 client). Those
/// don't divide evenly into the 960-sample mic frames downstream expects —
/// the leftover (typically 128 mono samples) is buffered in
/// <see cref="_monoAccumulator"/> and consumed on the next call.
/// </summary>
internal sealed class TciTxAudioReceiver : IDisposable
{
    /// <summary>Mic block size downstream <see cref="TxAudioIngest"/> consumes (20 ms @ 48 kHz).</summary>
    public const int OutputBlockSamples = 960;
    private const int OutputBlockBytes = OutputBlockSamples * 4;

    /// <summary>
    /// Accumulator capacity in mono samples. Sized for a worst-case
    /// 4 × default-2048-stereo-frame burst (8192 mono samples) with headroom.
    /// On overflow the accumulator is dropped — backpressure is the client's
    /// responsibility per spec §3.4 (TX_CHRONO is the pacing signal).
    /// </summary>
    private const int AccumulatorCapacity = 16384;

    private readonly Action<ReadOnlyMemory<byte>> _forward;
    private readonly ILogger _log;
    private readonly Action<int>? _onMonoSamplesQueued;

    private readonly object _sync = new();
    private readonly float[] _monoAccumulator = new float[AccumulatorCapacity];
    private int _monoFill;
    private readonly byte[] _outputBuffer = new byte[OutputBlockBytes];

    private long _totalFramesAccepted;
    private long _totalFramesDropped;
    private long _totalSamplesForwarded;

    public TciTxAudioReceiver(Action<ReadOnlyMemory<byte>> forwardF32leMicBlock, ILogger log, Action<int>? onMonoSamplesQueued = null)
    {
        _forward = forwardF32leMicBlock ?? throw new ArgumentNullException(nameof(forwardF32leMicBlock));
        _log = log;
        _onMonoSamplesQueued = onMonoSamplesQueued;
    }

    public long TotalFramesAccepted { get { lock (_sync) return _totalFramesAccepted; } }
    public long TotalFramesDropped { get { lock (_sync) return _totalFramesDropped; } }
    public long TotalSamplesForwarded { get { lock (_sync) return _totalSamplesForwarded; } }

    /// <summary>
    /// Accept one TX audio binary frame's sample payload (everything after the
    /// 64-byte header). <paramref name="declaredFloatCount"/> comes from header
    /// offset 20 (total scalar floats: frames × channels for stereo, frames for
    /// mono per markdown spec §7.5). <paramref name="channels"/> is the
    /// per-session negotiated audio_stream_channels value (1 or 2; default 2).
    /// Sample rate must be 48 kHz — resampling is not implemented.
    /// </summary>
    public void AcceptTxAudio(
        ReadOnlySpan<byte> samplePayload,
        TciSampleType sampleType,
        uint declaredFloatCount,
        int channels,
        int sampleRate)
    {
        if (sampleRate != 48000)
        {
            lock (_sync) _totalFramesDropped++;
            _log.LogDebug("tci.tx.audio dropped sampleRate={Rate} (only 48000 supported)", sampleRate);
            return;
        }
        if (sampleType != TciSampleType.Float32)
        {
            // Int16/24/32 inbound TX audio is in the spec but no real client
            // emits it. Drop with a hint so a future enabling change is easy.
            lock (_sync) _totalFramesDropped++;
            _log.LogDebug("tci.tx.audio dropped sampleType={Type} (only Float32 supported)", sampleType);
            return;
        }
        if (channels != 1 && channels != 2)
        {
            lock (_sync) _totalFramesDropped++;
            _log.LogDebug("tci.tx.audio dropped channels={Channels}", channels);
            return;
        }

        // WSJT-X allocates a buffer of length*sizeof(float)*2 bytes but
        // readAudioData only writes to the first length floats (length/2
        // stereo frames via the load() function). The tail is zeroes from
        // QByteArray::resize. Use declaredFloatCount as the cap so we don't
        // decode silence as audio.
        int floatCount = Math.Min((int)(samplePayload.Length / 4), (int)declaredFloatCount);
        if (floatCount <= 0)
        {
            lock (_sync) _totalFramesDropped++;
            return;
        }
        if (channels == 2 && (floatCount & 1) != 0)
        {
            floatCount--;
        }

        int monoSampleCount = (channels == 2) ? floatCount / 2 : floatCount;
        if (monoSampleCount <= 0)
        {
            lock (_sync) _totalFramesDropped++;
            return;
        }

        lock (_sync)
        {
            if (_monoFill + monoSampleCount > _monoAccumulator.Length)
            {
                // Producer outran the WDSP consumer — flush state and drop.
                _log.LogWarning("tci.tx.audio overflow fill={Fill} incoming={Incoming} cap={Cap}",
                    _monoFill, monoSampleCount, _monoAccumulator.Length);
                _monoFill = 0;
                _totalFramesDropped++;
                return;
            }

            // Decode + mix into the accumulator.
            if (channels == 2)
            {
                for (int i = 0; i < monoSampleCount; i++)
                {
                    float l = BinaryPrimitives.ReadSingleLittleEndian(samplePayload.Slice((i * 2 + 0) * 4, 4));
                    float r = BinaryPrimitives.ReadSingleLittleEndian(samplePayload.Slice((i * 2 + 1) * 4, 4));
                    _monoAccumulator[_monoFill + i] = 0.5f * (l + r);
                }
            }
            else
            {
                for (int i = 0; i < monoSampleCount; i++)
                {
                    _monoAccumulator[_monoFill + i] = BinaryPrimitives.ReadSingleLittleEndian(samplePayload.Slice(i * 4, 4));
                }
            }
            _monoFill += monoSampleCount;
            _totalFramesAccepted++;

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
                _totalSamplesForwarded += OutputBlockSamples;
            }

            int forwarded = writeOffset;
            int netQueued = monoSampleCount - forwarded;
            _onMonoSamplesQueued?.Invoke(netQueued);

            // Shift the remainder (always < 960 samples) to the head.
            int remainder = _monoFill - writeOffset;
            if (remainder > 0 && writeOffset > 0)
            {
                Array.Copy(_monoAccumulator, writeOffset, _monoAccumulator, 0, remainder);
            }
            _monoFill = remainder;
        }
    }

    /// <summary>
    /// Drop any in-flight mic samples. Called on MOX falling edge so the next
    /// keyed-up TX starts from silence rather than replaying a tail from the
    /// previous transmission.
    /// </summary>
    public void Reset()
    {
        lock (_sync) _monoFill = 0;
    }

    public void Dispose() { /* nothing to release */ }
}
