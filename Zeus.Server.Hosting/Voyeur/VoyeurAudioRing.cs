// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details. See ATTRIBUTIONS.md for full provenance.

using System.Runtime.CompilerServices;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Lock-free single-producer / single-consumer float ring buffer for Voyeur
/// Mode (issue zeus-la5). The PRODUCER is the DSP/RX thread inside
/// <c>DspPipelineService.Tick</c> — the WDSP IQ-feed hot path — which calls
/// <see cref="Write"/> exactly once per ~21 ms audio tick. The CONSUMER is the
/// Voyeur drain thread (below-normal priority), which calls <see cref="Read"/>.
///
/// This is the load-bearing "cannot break anything" primitive: the producer
/// path does only a bounded <c>Span.CopyTo</c> + two volatile index ops — no
/// lock, no allocation, no IO, and crucially NO back-pressure. If the consumer
/// has fallen behind and the ring is full, <see cref="Write"/> DROPS the block
/// and bumps <see cref="DroppedSamples"/>, then returns immediately. A stalled
/// or crashed Voyeur consumer is therefore invisible to RX — the worst case is
/// a gap in the captured audio, never a stall in the radio.
///
/// Concurrency model: the producer owns <c>_write</c>, the consumer owns
/// <c>_read</c>; each reads the other's position via <see cref="Volatile"/>.
/// Capacity is rounded up to a power of two so index→slot is a mask. Positions
/// are monotonic <c>long</c> (no wrap in any realistic runtime — 2^63 samples
/// at 48 kHz is ~6 million years).
/// </summary>
public sealed class VoyeurAudioRing
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;

    // Producer-owned write position; consumer-owned read position. Both
    // monotonically increasing. Read across threads only via Volatile.
    private long _write;
    private long _read;
    private long _dropped;

    public VoyeurAudioRing(int capacitySamples)
    {
        if (capacitySamples < 2) capacitySamples = 2;
        _capacity = NextPow2(capacitySamples);
        _buffer = new float[_capacity];
        _mask = _capacity - 1;
    }

    /// <summary>Ring capacity in samples (power of two).</summary>
    public int Capacity => _capacity;

    /// <summary>Total samples dropped because the ring was full when the
    /// producer wrote (consumer fell behind). Monotonic; telemetry only.</summary>
    public long DroppedSamples => Volatile.Read(ref _dropped);

    /// <summary>Samples currently available to read. Telemetry / consumer use.</summary>
    public int Available
    {
        get
        {
            long w = Volatile.Read(ref _write);
            long r = Volatile.Read(ref _read);
            return (int)(w - r);
        }
    }

    /// <summary>
    /// PRODUCER (DSP thread). Copy <paramref name="src"/> into the ring. If the
    /// whole block doesn't fit, drop it entirely (block-atomic — a half-written
    /// over is worse than a clean gap) and add its length to
    /// <see cref="DroppedSamples"/>. O(n) memcpy, no alloc, no lock, never blocks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<float> src)
    {
        if (src.IsEmpty) return;
        long w = _write;                       // producer owns _write — plain read
        long r = Volatile.Read(ref _read);     // consumer's position
        int free = _capacity - (int)(w - r);
        if (src.Length > free)
        {
            // Drop the whole block; never partially fill (keeps segment audio
            // clean) and never overwrite unread data.
            Interlocked.Add(ref _dropped, src.Length);
            return;
        }
        int start = (int)(w & _mask);
        int first = Math.Min(src.Length, _capacity - start);
        src.Slice(0, first).CopyTo(_buffer.AsSpan(start));
        if (first < src.Length)
            src.Slice(first).CopyTo(_buffer.AsSpan(0)); // wrap
        Volatile.Write(ref _write, w + src.Length);     // publish to consumer
    }

    /// <summary>
    /// CONSUMER (drain thread). Copy up to <paramref name="dst"/>.Length samples
    /// out of the ring. Returns the count actually read (0 when empty).
    /// </summary>
    public int Read(Span<float> dst)
    {
        if (dst.IsEmpty) return 0;
        long r = _read;                        // consumer owns _read — plain read
        long w = Volatile.Read(ref _write);    // producer's position
        int avail = (int)(w - r);
        if (avail <= 0) return 0;
        int n = Math.Min(avail, dst.Length);
        int start = (int)(r & _mask);
        int first = Math.Min(n, _capacity - start);
        _buffer.AsSpan(start, first).CopyTo(dst);
        if (first < n)
            _buffer.AsSpan(0, n - first).CopyTo(dst.Slice(first)); // wrap
        Volatile.Write(ref _read, r + n);      // publish free space to producer
        return n;
    }

    private static int NextPow2(int v)
    {
        v--;
        v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16;
        return v + 1;
    }
}
