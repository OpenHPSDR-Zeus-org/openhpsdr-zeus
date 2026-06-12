// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Tests for the Voyeur Mode lock-free SPSC audio ring (zeus-la5). This is the
// "cannot break anything" primitive: the producer (DSP/RX thread) must never
// block or overwrite unread data, and a slow consumer must only ever cause a
// counted drop — never corruption and never a stall.

using System.Threading;
using Xunit;
using Zeus.Server.Voyeur;

namespace Zeus.Server.Tests;

public class VoyeurAudioRingTests
{
    [Fact]
    public void RoundsCapacityUpToPowerOfTwo()
    {
        Assert.Equal(1024, new VoyeurAudioRing(1000).Capacity);
        Assert.Equal(1024, new VoyeurAudioRing(1024).Capacity);
        Assert.Equal(2, new VoyeurAudioRing(1).Capacity);
    }

    [Fact]
    public void WriteThenReadRoundTripsExactSamples()
    {
        var ring = new VoyeurAudioRing(16);
        var src = new float[] { 1, 2, 3, 4, 5 };
        ring.Write(src);
        Assert.Equal(5, ring.Available);
        var dst = new float[5];
        Assert.Equal(5, ring.Read(dst));
        Assert.Equal(src, dst);
        Assert.Equal(0, ring.Available);
        Assert.Equal(0, ring.DroppedSamples);
    }

    [Fact]
    public void WrapsAroundCorrectly()
    {
        var ring = new VoyeurAudioRing(8); // capacity 8
        // Fill, drain most, then write across the wrap boundary.
        ring.Write(new float[] { 1, 2, 3, 4, 5, 6 });
        var tmp = new float[4];
        Assert.Equal(4, ring.Read(tmp)); // read 1,2,3,4 — read idx now 4
        ring.Write(new float[] { 7, 8, 9, 10 }); // writes 7,8 then wraps 9,10
        var dst = new float[6];
        Assert.Equal(6, ring.Read(dst)); // 5,6,7,8,9,10
        Assert.Equal(new float[] { 5, 6, 7, 8, 9, 10 }, dst);
    }

    [Fact]
    public void DropsWholeBlockWhenFull_NeverOverwritesUnread()
    {
        var ring = new VoyeurAudioRing(8); // capacity 8
        ring.Write(new float[] { 1, 2, 3, 4, 5, 6 }); // 6 used, 2 free
        ring.Write(new float[] { 7, 8, 9 });          // needs 3, only 2 free -> DROP whole block
        Assert.Equal(3, ring.DroppedSamples);
        Assert.Equal(6, ring.Available);
        // The unread data is intact — the dropped block did not corrupt it.
        var dst = new float[6];
        Assert.Equal(6, ring.Read(dst));
        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, dst);
    }

    [Fact]
    public void ReadFromEmptyReturnsZero()
    {
        var ring = new VoyeurAudioRing(8);
        Assert.Equal(0, ring.Read(new float[4]));
    }

    [Fact]
    public void PartialReadLeavesRemainder()
    {
        var ring = new VoyeurAudioRing(16);
        ring.Write(new float[] { 1, 2, 3, 4, 5 });
        var dst = new float[3];
        Assert.Equal(3, ring.Read(dst));
        Assert.Equal(new float[] { 1, 2, 3 }, dst);
        Assert.Equal(2, ring.Available);
    }

    [Fact]
    public void ConcurrentProducerConsumer_NoLossNoCorruption_WhenConsumerKeepsUp()
    {
        // A real SPSC stress: producer writes a known ramp in blocks, consumer
        // drains continuously. With a generously sized ring and a keeping-up
        // consumer, every sample arrives in order with zero drops.
        const int total = 2_048_000; // multiple of the 256-sample block
        var ring = new VoyeurAudioRing(1 << 16);
        long nextExpected = 0;
        bool corrupt = false;

        var consumer = new Thread(() =>
        {
            var buf = new float[1024];
            long seen = 0;
            while (seen < total)
            {
                int n = ring.Read(buf);
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] != (float)(nextExpected)) { corrupt = true; }
                    nextExpected++;
                }
                seen += n;
                if (n == 0) Thread.SpinWait(50);
            }
        });
        consumer.Start();

        var block = new float[256];
        long produced = 0;
        while (produced < total)
        {
            for (int i = 0; i < block.Length; i++) block[i] = (float)(produced + i);
            // Retry on drop so this "keeps up" test asserts ordering, not policy
            // (the drop policy itself is covered by DropsWholeBlockWhenFull).
            long before = ring.DroppedSamples;
            ring.Write(block);
            if (ring.DroppedSamples == before) produced += block.Length;
            else Thread.SpinWait(50); // ring momentarily full; retry same block
        }
        consumer.Join();

        Assert.False(corrupt, "consumer observed an out-of-order / corrupt sample");
        Assert.Equal(total, nextExpected);
    }
}
