// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zeus.Server;

/// <summary>
/// Lock-free single-producer / single-consumer float sample ring.
///
/// <para>
/// Used by <see cref="NativeAudioSink"/>: the DSP tick thread (producer)
/// writes batches of mono float32 samples; the miniaudio playback worker
/// (consumer) reads variable-length batches sized to whatever miniaudio
/// hands it per period. Capacity is a power of two so the cursor wrap is
/// a bitmask, not a modulo.
/// </para>
///
/// <para>
/// Memory ordering: cursors use <see cref="Volatile.Read"/> /
/// <see cref="Volatile.Write"/> for release/acquire semantics across ARM64.
/// The producer publishes its slice writes before bumping <c>_tail</c>; the
/// consumer reads <c>_tail</c> before it reads the slice. Same shape as
/// <see cref="SpscRing{T}"/>, specialised to a span-friendly float buffer.
/// </para>
///
/// <para>
/// On overflow, <see cref="Write"/> writes as much as fits and returns the
/// short count — the caller (NativeAudioSink.Publish) tallies the dropped
/// remainder as an overrun. On underflow, <see cref="Read"/> returns the
/// short count and the audio callback fills the rest with silence.
/// </para>
///
/// <para>
/// Strictly single-producer + single-consumer. Concurrent use from more
/// than one of either is undefined.
/// </para>
/// </summary>
internal sealed class FloatSpscRing
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;
    private FloatSpscPaddedCursors _cursors;

    /// <param name="capacityPowerOfTwo">Sample capacity. Must be a positive
    /// power of two so the cursor wrap is a bitmask.</param>
    public FloatSpscRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 ||
            (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
        {
            throw new ArgumentException(
                "Capacity must be a positive power of two.",
                nameof(capacityPowerOfTwo));
        }
        _buffer = new float[capacityPowerOfTwo];
        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;
    }

    public int Capacity => _capacity;

    /// <summary>Approximate item count. Snapshot across both cursors;
    /// clamped to <c>[0, Capacity]</c>. For telemetry / heuristics only —
    /// races with both producer and consumer.</summary>
    public int Count
    {
        get
        {
            long tail = Volatile.Read(ref _cursors.Tail);
            long head = Volatile.Read(ref _cursors.Head);
            long diff = tail - head;
            if (diff < 0) return 0;
            if (diff > _capacity) return _capacity;
            return (int)diff;
        }
    }

    /// <summary>Producer side. Copies up to <c>src.Length</c> floats into the
    /// ring. Returns the number actually written (may be less than
    /// <c>src.Length</c> if the ring is full). Never blocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Write(ReadOnlySpan<float> src)
    {
        long tail = _cursors.Tail;
        long head = Volatile.Read(ref _cursors.Head);
        long space = _capacity - (tail - head);
        if (space <= 0) return 0;

        int n = src.Length;
        if (n > (int)space) n = (int)space;

        int idx = (int)(tail & _mask);
        int first = Math.Min(n, _capacity - idx);
        src.Slice(0, first).CopyTo(new Span<float>(_buffer, idx, first));
        if (first < n)
        {
            // Wrap remainder to the start of the buffer.
            src.Slice(first, n - first).CopyTo(new Span<float>(_buffer, 0, n - first));
        }
        // Release-store: slice writes visible before the new tail.
        Volatile.Write(ref _cursors.Tail, tail + n);
        return n;
    }

    /// <summary>Consumer side. Reads up to <c>dst.Length</c> floats from the
    /// ring into <c>dst</c>. Returns the number actually read (may be less
    /// than <c>dst.Length</c> if the ring is empty). Never blocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Read(Span<float> dst)
    {
        long head = _cursors.Head;
        long tail = Volatile.Read(ref _cursors.Tail);
        long avail = tail - head;
        if (avail <= 0) return 0;

        int n = dst.Length;
        if (n > (int)avail) n = (int)avail;

        int idx = (int)(head & _mask);
        int first = Math.Min(n, _capacity - idx);
        new ReadOnlySpan<float>(_buffer, idx, first).CopyTo(dst.Slice(0, first));
        if (first < n)
        {
            new ReadOnlySpan<float>(_buffer, 0, n - first).CopyTo(dst.Slice(first, n - first));
        }
        Volatile.Write(ref _cursors.Head, head + n);
        return n;
    }

    /// <summary>Consumer-side reset. Discards every queued sample. Safe to
    /// call only from the consumer thread or while both producer and
    /// consumer are quiesced.</summary>
    public void Clear()
    {
        long tail = Volatile.Read(ref _cursors.Tail);
        Volatile.Write(ref _cursors.Head, tail);
    }
}

/// <summary>Two cursors on independent cache lines; mirror of
/// <see cref="SpscPaddedCursors"/>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 384)]
internal struct FloatSpscPaddedCursors
{
    [FieldOffset(128)] public long Head;
    [FieldOffset(256)] public long Tail;
}
