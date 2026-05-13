// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zeus.Server;

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer.
///
/// <para>
/// Built for the perf-pass-3 iter5 refactor: the DSP thread that owns WDSP
/// produces display / audio / meter frames and hands them to the SignalR hub
/// sender without acquiring any lock per frame. The producer (DSP thread)
/// MUST NOT block — <see cref="TryEnqueue"/> returns <c>false</c> on full so
/// the caller can drop the frame.
/// </para>
///
/// <para>
/// <b>Strictly single-producer + single-consumer.</b> Concurrent use from
/// more than one producer, or more than one consumer, is undefined behaviour:
/// you will get torn reads, lost items, or duplicated items. There are no
/// runtime checks for this — keep the invariant at the wiring layer.
/// </para>
///
/// <para>
/// Memory ordering: cursors are accessed via <see cref="Volatile.Read"/> /
/// <see cref="Volatile.Write"/> so the happens-before edges are correct on
/// ARM64 (Apple Silicon dev machine), where the hardware permits more
/// reordering than x64. Specifically: producer publishes the slot write
/// before the tail bump (release-store on tail), and the consumer observes
/// the slot only after acquire-load on tail. Symmetric on the head side.
/// The plain <c>volatile</c> keyword is intentionally not used — on .NET
/// it has weaker semantics than <see cref="Volatile.Read"/>/<see cref="Volatile.Write"/>
/// on ARM and does not support <c>long</c> at all.
/// </para>
///
/// <para>
/// The two cursors live on separate cache lines (see <see cref="PaddedCursors"/>)
/// so the producer and consumer do not ping-pong each other's L1.
/// </para>
///
/// <para>
/// <typeparamref name="T"/> is unconstrained: value-type frame structs
/// (<c>DisplayFrame</c>, <c>AudioFrame</c>, …) sit inline in the backing
/// array with no boxing, and reference-type payloads sit as plain references.
/// For reference-or-contains-reference T we null the slot on dequeue so the
/// GC can reclaim it before the next wrap.
/// </para>
/// </summary>
/// <typeparam name="T">Element type. Any type — struct or class.</typeparam>
public sealed class SpscRing<T>
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;
    private SpscPaddedCursors _cursors;

    // Hoisted at construction: T may hold managed references → clear slot
    // on dequeue so the consumer doesn't pin a stale payload until wrap-around.
    private static readonly bool s_clearOnDequeue =
        RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    /// <summary>
    /// Create a ring of exactly <paramref name="capacityPowerOfTwo"/> slots.
    /// Must be a positive power of two so the producer/consumer cursors can
    /// be wrapped with a bitmask (cheap) instead of modulo (expensive on the
    /// hot path).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if capacity is non-positive or not a power of two.
    /// </exception>
    public SpscRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 ||
            (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
        {
            throw new ArgumentException(
                "Capacity must be a positive power of two.",
                nameof(capacityPowerOfTwo));
        }
        _buffer = new T[capacityPowerOfTwo];
        _mask = capacityPowerOfTwo - 1;
        _capacity = capacityPowerOfTwo;
    }

    /// <summary>Configured capacity (power of two).</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Approximate item count. Producer- and consumer-thread snapshots may
    /// race; this is fine for telemetry / backpressure heuristics. The
    /// returned value is clamped to <c>[0, Capacity]</c>.
    /// </summary>
    public int Count
    {
        get
        {
            // Read tail first, then head: under-estimate is preferable to
            // over-estimate for backpressure callers.
            long tail = Volatile.Read(ref _cursors.Tail);
            long head = Volatile.Read(ref _cursors.Head);
            long diff = tail - head;
            if (diff < 0) return 0;
            if (diff > _capacity) return _capacity;
            return (int)diff;
        }
    }

    /// <summary>
    /// Producer-side: try to publish <paramref name="item"/>. Returns
    /// <c>false</c> immediately if the ring is full. Never blocks, never
    /// allocates. Single-producer only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in T item)
    {
        // Tail is owned by the producer; no need for a volatile read of our
        // own cursor. Head is owned by the consumer → acquire-load.
        long tail = _cursors.Tail;
        long head = Volatile.Read(ref _cursors.Head);
        if (tail - head >= _capacity)
        {
            return false; // full
        }
        _buffer[(int)(tail & _mask)] = item;
        // Release-store: the slot write above is visible before any reader
        // sees the new tail value.
        Volatile.Write(ref _cursors.Tail, tail + 1);
        return true;
    }

    /// <summary>
    /// Consumer-side: try to take the next item. Returns <c>false</c> if the
    /// ring is empty. Never blocks, never allocates. Single-consumer only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        // Head is owned by the consumer; no need for a volatile read of our
        // own cursor. Tail is owned by the producer → acquire-load.
        long head = _cursors.Head;
        long tail = Volatile.Read(ref _cursors.Tail);
        if (tail == head)
        {
            item = default!;
            return false; // empty
        }
        int idx = (int)(head & _mask);
        item = _buffer[idx];
        if (s_clearOnDequeue)
        {
            _buffer[idx] = default!;
        }
        // Release-store: clears (if any) visible before producer sees the
        // slot as reusable.
        Volatile.Write(ref _cursors.Head, head + 1);
        return true;
    }

}

/// <summary>
/// Two cursors on independent cache lines so producer / consumer updates
/// don't cause false-sharing ping-pong. 128-byte stride covers both x64
/// (64-byte cache lines) and Apple Silicon (128-byte cache lines).
///
/// Hoisted out of <see cref="SpscRing{T}"/> because explicit-layout structs
/// cannot be nested inside a generic type in .NET.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 384)]
internal struct SpscPaddedCursors
{
    /// <summary>Consumer-owned cursor (next index to read).</summary>
    [FieldOffset(128)] public long Head;
    /// <summary>Producer-owned cursor (next index to write).</summary>
    [FieldOffset(256)] public long Tail;
}
