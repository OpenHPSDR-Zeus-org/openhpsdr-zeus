// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Protocol1;

/// <summary>
/// SPSC-ish ring of mono s16 audio samples linking the host-side RX audio
/// publisher (<c>RadioSpeakerAudioSink</c>, pushes one demodulated 48 kHz
/// AudioFrame at a time) to the P1 EP2 packer consumer (<see cref="ControlFrame"/>,
/// pulls 63 samples per USB frame every ~1.5 ms). The mirror image of
/// <see cref="TxIqRing"/> on the RX-audio half of the same EP2 frame.
///
/// Rate shape (both ends nominally 48 kHz):
///   producer:  one AudioFrame block (typically 512–2048 samples) per DSP tick
///   consumer:  63 samples per USB frame, 2 frames per EP2 packet, ~381 pkt/s
/// Producer and consumer are rate-matched at 48 kHz; the ring absorbs the block
/// vs per-frame granularity mismatch and any scheduler jitter. Drop-oldest on
/// overflow keeps latency bounded — staleness on the order of the ring depth is
/// the worst case, and the sink stops feeding on MOX so a transmission never
/// leaves a stale RX tail to play on unkey.
///
/// Reading an empty ring writes nothing and returns 0, so the caller leaves the
/// L/R slots zero — byte-identical to today's behaviour where Zeus never carried
/// RX audio. That makes enabling/disabling the feature a strict superset: when
/// nobody feeds the ring, the wire is unchanged.
///
/// Implemented with a plain lock, like <see cref="TxIqRing"/>: the enqueue path
/// runs a few hundred times a second and the dequeue ~760 times a second, so
/// contention is negligible and a lock is far simpler to reason about than a
/// lock-free variant.
/// </summary>
public sealed class RxAudioRing : IRxAudioSource
{
    // 16384 samples ≈ 340 ms at 48 kHz — matches TxIqRing's depth. Deep enough
    // to ride out a GC pause or a bursty DSP tick without dropping, shallow
    // enough that a steady-state backlog never adds audible latency to the
    // radio-side monitor.
    public const int DefaultCapacitySamples = 16384;

    private readonly short[] _buf;
    private readonly int _capacity;
    private readonly object _gate = new();
    private int _head;   // write index
    private int _count;  // number of valid samples
    private long _totalWritten;
    private long _totalRead;
    private long _dropped;

    public RxAudioRing(int capacitySamples = DefaultCapacitySamples)
    {
        if (capacitySamples <= 0) throw new ArgumentOutOfRangeException(nameof(capacitySamples));
        _capacity = capacitySamples;
        _buf = new short[capacitySamples];
    }

    public int Capacity => _capacity;
    public int Count { get { lock (_gate) return _count; } }
    public long TotalWritten { get { lock (_gate) return _totalWritten; } }
    public long TotalRead { get { lock (_gate) return _totalRead; } }
    public long Dropped { get { lock (_gate) return _dropped; } }

    /// <summary>
    /// Push one block of mono float samples (−1..+1) into the ring, saturating
    /// to s16. Overflow overwrites the oldest samples (drop-oldest).
    /// </summary>
    public void Write(ReadOnlySpan<float> mono)
    {
        if (mono.IsEmpty) return;

        lock (_gate)
        {
            foreach (float f in mono)
            {
                _buf[_head] = ToS16(f);
                _head = (_head + 1) % _capacity;
                if (_count < _capacity) _count++;
                else _dropped++;   // overwrote the oldest
            }
            _totalWritten += mono.Length;
        }
    }

    /// <summary>
    /// Drain up to <c>dest.Length</c> oldest samples into <paramref name="dest"/>.
    /// Returns the count written; the remainder of <paramref name="dest"/> is
    /// left untouched. Returns 0 when empty.
    /// </summary>
    public int Read(Span<short> dest)
    {
        if (dest.IsEmpty) return 0;

        lock (_gate)
        {
            int n = Math.Min(dest.Length, _count);
            int tail = (_head - _count + _capacity) % _capacity;
            for (int k = 0; k < n; k++)
            {
                dest[k] = _buf[tail];
                tail = (tail + 1) % _capacity;
            }
            _count -= n;
            _totalRead += n;
            return n;
        }
    }

    /// <summary>
    /// Drop all buffered samples. Called when a P1 session ends or the feature
    /// is toggled off so a later RX never replays a stale tail.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _count = 0;
            _head = 0;
        }
    }

    private static short ToS16(float v)
    {
        if (!float.IsFinite(v)) return 0;
        float clamped = v;
        if (clamped > 1.0f) clamped = 1.0f;
        else if (clamped < -1.0f) clamped = -1.0f;
        return (short)Math.Round(clamped * short.MaxValue);
    }
}
