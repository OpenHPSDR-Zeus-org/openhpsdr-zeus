// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Distributed under the GNU General Public License v2 or later. See the
// LICENSE file at the root of this repository for full text.

using System.Buffers;
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
/// Frame-size decoupling: TCI clients send TX audio in arbitrary block sizes.
/// The receiver forwards complete 960-sample mic frames immediately and keeps
/// only the partial carry block for the next call.
/// </summary>
internal sealed class TciTxAudioReceiver : IDisposable
{
    public const int OutputSampleRate = 48_000;
    public const int MinInputSampleRate = 8_000;
    public const int MaxInputSampleRate = OutputSampleRate;

    /// <summary>Mic block size downstream <see cref="TxAudioIngest"/> consumes (20 ms @ 48 kHz).</summary>
    public const int OutputBlockSamples = 960;
    private const int OutputBlockBytes = OutputBlockSamples * 4;

    private readonly Action<ReadOnlyMemory<byte>> _forward;
    private readonly ILogger _log;
    private readonly Action<int>? _onMonoSamplesQueued;

    private readonly object _sync = new();
    private readonly float[] _monoAccumulator = new float[OutputBlockSamples];
    private int _monoFill;
    private readonly byte[] _outputBuffer = new byte[OutputBlockBytes];

    private long _totalFramesAccepted;
    private long _totalFramesDropped;
    private long _totalSamplesForwarded;
    private int _lastInputSampleRate = OutputSampleRate;
    private double _resamplePhase;

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
    /// Sample rates below 48 kHz are linearly resampled to the fixed downstream
    /// 48 kHz mic-block contract. Rates above 48 kHz are rejected rather than
    /// downsampled without an anti-alias filter.
    /// </summary>
    public void AcceptTxAudio(
        ReadOnlySpan<byte> samplePayload,
        TciSampleType sampleType,
        uint declaredFloatCount,
        int channels,
        int sampleRate)
    {
        if (sampleRate < MinInputSampleRate || sampleRate > MaxInputSampleRate)
        {
            lock (_sync) _totalFramesDropped++;
            _log.LogDebug("tci.tx.audio dropped sampleRate={Rate} (supported {Min}..{Max})",
                sampleRate, MinInputSampleRate, MaxInputSampleRate);
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

        var rentedMono = ArrayPool<float>.Shared.Rent(monoSampleCount);
        try
        {
            lock (_sync)
            {
                var mono = rentedMono.AsSpan(0, monoSampleCount);
                if (channels == 2)
                {
                    for (int i = 0; i < monoSampleCount; i++)
                    {
                        float l = BinaryPrimitives.ReadSingleLittleEndian(samplePayload.Slice((i * 2 + 0) * 4, 4));
                        float r = BinaryPrimitives.ReadSingleLittleEndian(samplePayload.Slice((i * 2 + 1) * 4, 4));
                        mono[i] = 0.5f * (l + r);
                    }
                }
                else
                {
                    for (int i = 0; i < monoSampleCount; i++)
                    {
                        mono[i] = BinaryPrimitives.ReadSingleLittleEndian(samplePayload.Slice(i * 4, 4));
                    }
                }

                var (queued, forwarded) = AppendMonoAtOutputRate(mono, sampleRate);
                _totalFramesAccepted++;
                _onMonoSamplesQueued?.Invoke(queued - forwarded);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedMono);
        }
    }

    private (int Queued, int Forwarded) AppendMonoAtOutputRate(ReadOnlySpan<float> mono, int sampleRate)
    {
        if (sampleRate != _lastInputSampleRate)
        {
            _lastInputSampleRate = sampleRate;
            _resamplePhase = 0;
        }

        int queued = 0;
        int forwarded = 0;
        if (sampleRate == OutputSampleRate)
        {
            _resamplePhase = 0;
            for (int i = 0; i < mono.Length; i++)
            {
                forwarded += AppendOutputSample(mono[i]);
            }
            return (mono.Length, forwarded);
        }

        double step = sampleRate / (double)OutputSampleRate;
        double phase = _resamplePhase;
        while (phase < mono.Length)
        {
            int idx = (int)phase;
            double frac = phase - idx;
            float a = mono[idx];
            float b = idx + 1 < mono.Length ? mono[idx + 1] : a;
            forwarded += AppendOutputSample(a + (float)((b - a) * frac));
            queued++;
            phase += step;
        }
        _resamplePhase = phase - mono.Length;
        return (queued, forwarded);
    }

    private int AppendOutputSample(float sample)
    {
        if (_monoFill >= _monoAccumulator.Length)
        {
            _log.LogWarning("tci.tx.audio overflow fill={Fill} cap={Cap}", _monoFill, _monoAccumulator.Length);
            _monoFill = 0;
            _totalFramesDropped++;
        }
        _monoAccumulator[_monoFill++] = sample;
        if (_monoFill < OutputBlockSamples) return 0;

        for (int i = 0; i < OutputBlockSamples; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                _outputBuffer.AsSpan(i * 4, 4),
                _monoAccumulator[i]);
        }
        _forward(_outputBuffer);
        _monoFill = 0;
        _totalSamplesForwarded += OutputBlockSamples;
        return OutputBlockSamples;
    }

    /// <summary>
    /// Drop any in-flight mic samples. Called on MOX falling edge so the next
    /// keyed-up TX starts from silence rather than replaying a tail from the
    /// previous transmission.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _monoFill = 0;
            _lastInputSampleRate = OutputSampleRate;
            _resamplePhase = 0;
        }
    }

    public void Dispose() { /* nothing to release */ }
}
