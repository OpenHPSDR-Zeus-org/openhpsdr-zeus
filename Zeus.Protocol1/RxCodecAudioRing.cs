// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Protocol1;

/// <summary>
/// Bounded mono int16 ring buffer feeding the EP2 outbound L/R audio bytes
/// (issue #426). The DSP audio sink writes float32 [-1, +1] samples; the
/// EP2 packer reads (L, R) int16 pairs at 63 samples per USB frame. Mono
/// in → both L and R read the same sample.
///
/// Capacity sized for ~170 ms @ 48 kHz so a brief DSP-tick stall doesn't
/// underflow the wire. On overflow (radio sample clock slow vs DSP) the
/// oldest samples are dropped — that bounds latency on long sessions
/// where the two clocks drift apart, matching the rationale in
/// <see cref="Zeus.Server.NativeAudioSink"/>'s clock-drift handling.
///
/// Thread safety: a single object lock serialises Write / Next / Clear.
/// The contention window is tiny (one short array index per call) and the
/// read rate is ~1500 Hz at EP2 packet rate — well below where lock
/// overhead matters.
/// </summary>
public sealed class RxCodecAudioRing : IRxCodecAudioSource
{
    /// <summary>Default 8192-sample capacity ≈ 170 ms @ 48 kHz mono.</summary>
    public const int DefaultCapacitySamples = 8192;

    private readonly short[] _buf;
    private readonly object _sync = new();
    private int _head;
    private int _tail;
    private int _count;
    private long _droppedSamples;

    public RxCodecAudioRing(int capacitySamples = DefaultCapacitySamples)
    {
        if (capacitySamples <= 0) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        _buf = new short[capacitySamples];
    }

    /// <summary>
    /// Append mono float samples in [-1, +1]. Saturating-clamps to int16.
    /// On overflow (full ring) the oldest sample is dropped — bounds latency
    /// at the cost of a tiny audible glitch on sustained overruns.
    /// </summary>
    public void Write(ReadOnlySpan<float> monoSamples)
    {
        if (monoSamples.IsEmpty) return;
        lock (_sync)
        {
            int cap = _buf.Length;
            for (int i = 0; i < monoSamples.Length; i++)
            {
                float s = monoSamples[i];
                if (s > 1f) s = 1f;
                else if (s < -1f) s = -1f;
                short v = (short)(s * 32767f);
                _buf[_tail] = v;
                _tail++;
                if (_tail == cap) _tail = 0;
                if (_count == cap)
                {
                    _head++;
                    if (_head == cap) _head = 0;
                    _droppedSamples++;
                }
                else
                {
                    _count++;
                }
            }
        }
    }

    /// <inheritdoc />
    public (short L, short R) Next()
    {
        lock (_sync)
        {
            if (_count == 0) return (0, 0);
            short v = _buf[_head];
            _head++;
            if (_head == _buf.Length) _head = 0;
            _count--;
            return (v, v);
        }
    }

    /// <summary>Drop every queued sample. Used on disconnect.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _head = 0;
            _tail = 0;
            _count = 0;
        }
    }

    /// <summary>Sample count currently queued. Read-only snapshot.</summary>
    public int Count { get { lock (_sync) return _count; } }

    /// <summary>Cumulative samples dropped due to overflow since construction.</summary>
    public long DroppedSamples { get { lock (_sync) return _droppedSamples; } }
}
