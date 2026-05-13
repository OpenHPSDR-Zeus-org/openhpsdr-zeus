// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Behaviour and concurrent-correctness tests for <see cref="SpscRing{T}"/>.
/// Stress test runs one producer + one consumer (SPSC contract) and verifies
/// no torn reads, no drops, and strict FIFO order across ~1M items.
/// </summary>
public class SpscRingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(1000)]
    public void Constructor_Rejects_NonPowerOfTwo(int capacity)
    {
        Assert.Throws<ArgumentException>(() => new SpscRing<int>(capacity));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(1024)]
    public void Constructor_Accepts_PowerOfTwo(int capacity)
    {
        var ring = new SpscRing<int>(capacity);
        Assert.Equal(capacity, ring.Capacity);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Empty_Dequeue_Returns_False()
    {
        var ring = new SpscRing<int>(8);
        Assert.False(ring.TryDequeue(out int item));
        Assert.Equal(0, item);
    }

    [Fact]
    public void Single_Enqueue_Then_Dequeue_Roundtrips_Value()
    {
        var ring = new SpscRing<int>(8);
        Assert.True(ring.TryEnqueue(42));
        Assert.Equal(1, ring.Count);
        Assert.True(ring.TryDequeue(out int item));
        Assert.Equal(42, item);
        Assert.Equal(0, ring.Count);
        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void Fill_To_Capacity_Then_Reject_Then_Drain_FIFO()
    {
        var ring = new SpscRing<int>(4);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(ring.TryEnqueue(i), $"Enqueue {i} unexpectedly rejected");
        }
        Assert.Equal(4, ring.Count);
        // Full → next enqueue is rejected, no blocking.
        Assert.False(ring.TryEnqueue(999));
        // Drain in FIFO order.
        for (int i = 0; i < 4; i++)
        {
            Assert.True(ring.TryDequeue(out int item));
            Assert.Equal(i, item);
        }
        Assert.Equal(0, ring.Count);
        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void Wraps_Around_Across_Buffer_Boundary()
    {
        // Capacity 4 → mask 3. Enqueue 3, dequeue 2, enqueue 3 more → the
        // last 3 writes wrap past the buffer end. Verify items still come
        // out in strict FIFO order.
        var ring = new SpscRing<int>(4);
        Assert.True(ring.TryEnqueue(1));
        Assert.True(ring.TryEnqueue(2));
        Assert.True(ring.TryEnqueue(3));

        Assert.True(ring.TryDequeue(out int a)); Assert.Equal(1, a);
        Assert.True(ring.TryDequeue(out int b)); Assert.Equal(2, b);

        Assert.True(ring.TryEnqueue(4));
        Assert.True(ring.TryEnqueue(5));
        Assert.True(ring.TryEnqueue(6));
        // 3, 4, 5, 6 now in the ring (capacity 4, head advanced past slots 0..1).
        Assert.False(ring.TryEnqueue(7));

        Assert.True(ring.TryDequeue(out int c)); Assert.Equal(3, c);
        Assert.True(ring.TryDequeue(out int d)); Assert.Equal(4, d);
        Assert.True(ring.TryDequeue(out int e)); Assert.Equal(5, e);
        Assert.True(ring.TryDequeue(out int f)); Assert.Equal(6, f);
        Assert.False(ring.TryDequeue(out _));
    }

    [Fact]
    public void Many_Wraps_Preserve_FIFO()
    {
        // Capacity 8, single-threaded interleaved produce/consume across many
        // wrap-arounds. Catches off-by-one in masking arithmetic.
        var ring = new SpscRing<int>(8);
        int produced = 0, consumed = 0;
        for (int round = 0; round < 1000; round++)
        {
            // Produce a small burst (≤ capacity), consume it all.
            int burst = (round % 7) + 1;
            for (int i = 0; i < burst; i++)
            {
                Assert.True(ring.TryEnqueue(produced++));
            }
            for (int i = 0; i < burst; i++)
            {
                Assert.True(ring.TryDequeue(out int got));
                Assert.Equal(consumed++, got);
            }
        }
        Assert.Equal(produced, consumed);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Reference_Type_Slot_Is_Cleared_On_Dequeue()
    {
        // For reference T, the implementation nulls the backing slot on
        // dequeue so the GC isn't blocked by a stale payload until wrap.
        // We verify directly by peeking the private backing array — a
        // GC/WeakReference test would race against xUnit-rooted locals.
        var ring = new SpscRing<object>(2);
        var sentinel = new object();
        Assert.True(ring.TryEnqueue(sentinel));

        var bufferField = typeof(SpscRing<object>).GetField(
            "_buffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(bufferField);
        var buffer = (object?[])bufferField!.GetValue(ring)!;
        Assert.Same(sentinel, buffer[0]);

        Assert.True(ring.TryDequeue(out object? got));
        Assert.Same(sentinel, got);

        // The dequeued slot must no longer hold the sentinel — otherwise
        // the ring would pin payloads until the next wrap, which is the
        // bug the s_clearOnDequeue branch exists to prevent.
        Assert.Null(buffer[0]);
    }

    [Fact]
    public void Value_Type_Slot_Is_Not_Cleared_On_Dequeue()
    {
        // For non-reference T the slot-clear is a pure cost with no benefit,
        // so we skip it. Verify by inspecting the backing array.
        var ring = new SpscRing<int>(2);
        Assert.True(ring.TryEnqueue(42));
        Assert.True(ring.TryDequeue(out int got));
        Assert.Equal(42, got);

        var bufferField = typeof(SpscRing<int>).GetField(
            "_buffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var buffer = (int[])bufferField!.GetValue(ring)!;
        // Slot still contains the old value — that's fine for value types.
        Assert.Equal(42, buffer[0]);
    }

    [Fact]
    public async Task Concurrent_Single_Producer_Single_Consumer_Stress()
    {
        // SPSC contract: one producer task, one consumer task, ~1M items.
        // Producer monotonic ints → consumer must see them in strict order
        // with no drops and no duplicates. Uses a small ring so the producer
        // blocks on full repeatedly and the consumer drains repeatedly.
        const int Capacity = 1024;
        const int N = 1_000_000;

        var ring = new SpscRing<int>(Capacity);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < N; i++)
            {
                // Spin until there is room. SPSC + non-blocking ring →
                // back-off is the caller's responsibility.
                SpinWait spin = default;
                while (!ring.TryEnqueue(i))
                {
                    if (cts.IsCancellationRequested) return;
                    spin.SpinOnce();
                }
            }
        }, cts.Token);

        long lastSeen = -1;
        long total = 0;
        var consumer = Task.Run(() =>
        {
            SpinWait spin = default;
            while (total < N)
            {
                if (ring.TryDequeue(out int item))
                {
                    if (item != lastSeen + 1)
                    {
                        throw new Xunit.Sdk.XunitException(
                            $"Out-of-order or dropped: expected {lastSeen + 1}, got {item}");
                    }
                    lastSeen = item;
                    total++;
                    spin = default;
                }
                else
                {
                    if (cts.IsCancellationRequested) return;
                    spin.SpinOnce();
                }
            }
        }, cts.Token);

        var bothDone = Task.WhenAll(producer, consumer);
        var winner = await Task.WhenAny(bothDone, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(bothDone, winner);
        Assert.False(cts.IsCancellationRequested, "Stress test timed out");
        await bothDone; // surface any thrown XunitException
        Assert.Equal(N, total);
        Assert.Equal(N - 1, lastSeen);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Count_Approximate_Within_Bounds()
    {
        var ring = new SpscRing<int>(16);
        Assert.Equal(0, ring.Count);
        for (int i = 0; i < 10; i++) ring.TryEnqueue(i);
        Assert.Equal(10, ring.Count);
        ring.TryDequeue(out _);
        ring.TryDequeue(out _);
        Assert.Equal(8, ring.Count);

        // Fill to capacity → Count clamps at Capacity.
        while (ring.TryEnqueue(0)) { }
        Assert.Equal(ring.Capacity, ring.Count);
    }

    private struct FrameLike
    {
        public long Seq;
        public double Value;
    }

    [Fact]
    public void Struct_Payload_Roundtrips_Without_Boxing()
    {
        // Sanity check that a moderately wide struct (mimicking AudioFrame /
        // DisplayFrame metadata) survives a producer/consumer roundtrip
        // without truncation. If this regresses, the array-element type is
        // wrong.
        var ring = new SpscRing<FrameLike>(8);
        for (int i = 0; i < 8; i++)
        {
            Assert.True(ring.TryEnqueue(new FrameLike { Seq = i, Value = i * 1.5 }));
        }
        for (int i = 0; i < 8; i++)
        {
            Assert.True(ring.TryDequeue(out FrameLike f));
            Assert.Equal(i, f.Seq);
            Assert.Equal(i * 1.5, f.Value);
        }
    }
}
