// SPDX-License-Identifier: GPL-2.0-or-later
//
// Issue #426 — RX audio routed to the radio's on-board codec via the
// EP2 outbound L/R bytes. The ring is a tiny SPSC-ish buffer between
// WDSP's audio sink and the EP2 packer; this test pins its core
// semantics so a future refactor doesn't silently break Hermes audio.

using Xunit;
using Zeus.Protocol1;

namespace Zeus.Protocol1.Tests;

public class RxCodecAudioRingTests
{
    [Fact]
    public void Next_EmptyRing_ReturnsZeroPair()
    {
        var ring = new RxCodecAudioRing(capacitySamples: 16);
        var (l, r) = ring.Next();
        Assert.Equal(0, l);
        Assert.Equal(0, r);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Write_MonoFloat_ReadsBackAsDuplicatedLrInt16()
    {
        var ring = new RxCodecAudioRing(capacitySamples: 32);
        // 0.5f → ~16383.5 → int16 16383 after truncation toward zero.
        ring.Write(new float[] { 0.5f, -0.5f, 1.0f, -1.0f });

        Assert.Equal(4, ring.Count);

        var (l0, r0) = ring.Next();
        Assert.Equal(16383, l0);
        Assert.Equal(l0, r0); // mono in → L == R out

        var (l1, _) = ring.Next();
        Assert.Equal(-16383, l1);

        var (l2, _) = ring.Next();
        Assert.Equal(short.MaxValue, l2);

        var (l3, _) = ring.Next();
        // -1.0 * 32767 = -32767, clamped to int16 range.
        Assert.Equal(-short.MaxValue, l3);

        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Write_SaturatesPastUnity()
    {
        var ring = new RxCodecAudioRing(capacitySamples: 8);
        ring.Write(new float[] { 2.5f, -2.5f, 1.0001f });

        var (l0, _) = ring.Next();
        Assert.Equal(short.MaxValue, l0);
        var (l1, _) = ring.Next();
        Assert.Equal(-short.MaxValue, l1);
        var (l2, _) = ring.Next();
        Assert.Equal(short.MaxValue, l2);
    }

    [Fact]
    public void Write_PastCapacity_DropsOldestAndCountsDrops()
    {
        var ring = new RxCodecAudioRing(capacitySamples: 4);
        ring.Write(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        Assert.Equal(4, ring.Count);
        Assert.Equal(0, ring.DroppedSamples);

        // Overflow by 2 — oldest two get evicted.
        ring.Write(new float[] { 0.5f, 0.6f });
        Assert.Equal(4, ring.Count);
        Assert.Equal(2, ring.DroppedSamples);

        // FIFO from the surviving samples.
        var (l0, _) = ring.Next();
        var (l1, _) = ring.Next();
        var (l2, _) = ring.Next();
        var (l3, _) = ring.Next();
        // 0.3, 0.4, 0.5, 0.6 — strictly increasing.
        Assert.True(l0 < l1 && l1 < l2 && l2 < l3);
    }

    [Fact]
    public void Clear_DrainsRingWithoutResettingDropCounter()
    {
        var ring = new RxCodecAudioRing(capacitySamples: 2);
        ring.Write(new float[] { 0.1f, 0.2f, 0.3f }); // drops 1
        Assert.Equal(2, ring.Count);
        Assert.Equal(1, ring.DroppedSamples);

        ring.Clear();
        Assert.Equal(0, ring.Count);
        // DroppedSamples is a session counter and reflects history, not
        // current depth — Clear() doesn't reset it.
        Assert.Equal(1, ring.DroppedSamples);

        var (l, r) = ring.Next();
        Assert.Equal(0, l);
        Assert.Equal(0, r);
    }

    [Fact]
    public void IRxCodecAudioSource_Polymorphism()
    {
        // Trivial — but ensures the contract surface the EP2 packer talks
        // to is the same one the ring implements, so a future "swap the
        // ring for a different source" refactor can't drift the interface.
        IRxCodecAudioSource src = new RxCodecAudioRing(capacitySamples: 4);
        var (l, r) = src.Next();
        Assert.Equal(0, l);
        Assert.Equal(0, r);
    }
}
