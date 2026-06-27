// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Coverage for the RX-audio ring that carries demodulated RX audio into the
// Protocol-1 EP2 L/R slots (radio-codec speaker output). The RX-half mirror of
// TxIqRing: rate-matched 48 kHz producer/consumer, drop-oldest on overflow,
// silence (zero count) on underrun so the wire reverts to "no RX audio".

using Zeus.Protocol1;

namespace Zeus.Protocol1.Tests;

public class RxAudioRingTests
{
    [Fact]
    public void Read_EmptyRing_ReturnsZeroAndLeavesDestUntouched()
    {
        var ring = new RxAudioRing(64);
        Span<short> dest = stackalloc short[8];
        dest.Fill(0x55);

        int n = ring.Read(dest);

        Assert.Equal(0, n);
        // Untouched: caller relies on this to keep the cleared (zero) L/R slots.
        foreach (var s in dest) Assert.Equal((short)0x55, s);
    }

    [Fact]
    public void WriteThenRead_RoundTripsSamplesInOrder()
    {
        var ring = new RxAudioRing(64);
        // 0.5f -> +16384 with round(0.5 * 32767) = 16384 (16383.5 rounds to even? no:
        // MidpointRounding default is ToEven for Math.Round(double) -> 16384).
        ring.Write(new float[] { 0.0f, 0.5f, -0.5f, 1.0f });

        Span<short> dest = stackalloc short[4];
        int n = ring.Read(dest);

        Assert.Equal(4, n);
        Assert.Equal(0, dest[0]);
        Assert.Equal((short)Math.Round(0.5f * short.MaxValue), dest[1]);
        Assert.Equal((short)Math.Round(-0.5f * short.MaxValue), dest[2]);
        Assert.Equal(short.MaxValue, dest[3]);
    }

    [Fact]
    public void Write_SaturatesOutOfRangeAndNonFinite()
    {
        var ring = new RxAudioRing(64);
        ring.Write(new float[] { 2.0f, -2.0f, float.NaN, float.PositiveInfinity });

        Span<short> dest = stackalloc short[4];
        ring.Read(dest);

        // Out-of-range floats clamp; non-finite (NaN/±inf) map to 0 — matches
        // TxIqRing. Symmetric scale by short.MaxValue: ±1.0 -> ±32767.
        Assert.Equal(short.MaxValue, dest[0]);
        Assert.Equal((short)-short.MaxValue, dest[1]);
        Assert.Equal((short)0, dest[2]);   // NaN -> 0
        Assert.Equal((short)0, dest[3]);   // +inf non-finite -> 0
    }

    [Fact]
    public void Read_Underrun_ReturnsPartialCount()
    {
        var ring = new RxAudioRing(64);
        ring.Write(new float[] { 0.1f, 0.2f });

        Span<short> dest = stackalloc short[8];
        int n = ring.Read(dest);

        Assert.Equal(2, n);   // only 2 available; rest of dest left to the caller
    }

    [Fact]
    public void Write_Overflow_DropsOldest()
    {
        var ring = new RxAudioRing(4);
        // Write 6 into a depth-4 ring: oldest two (1,2) are overwritten.
        ring.Write(new float[] { 1f / 32767, 2f / 32767, 3f / 32767, 4f / 32767, 5f / 32767, 6f / 32767 });

        Assert.Equal(4, ring.Count);
        Assert.Equal(2, ring.Dropped);

        Span<short> dest = stackalloc short[4];
        int n = ring.Read(dest);

        Assert.Equal(4, n);
        Assert.Equal((short)3, dest[0]);   // 1 and 2 dropped
        Assert.Equal((short)4, dest[1]);
        Assert.Equal((short)5, dest[2]);
        Assert.Equal((short)6, dest[3]);
    }

    [Fact]
    public void Clear_DropsBufferedSamples()
    {
        var ring = new RxAudioRing(64);
        ring.Write(new float[] { 0.5f, 0.5f, 0.5f });

        ring.Clear();

        Assert.Equal(0, ring.Count);
        Span<short> dest = stackalloc short[4];
        Assert.Equal(0, ring.Read(dest));
    }

    [Fact]
    public void WrapAround_PreservesOrder()
    {
        // Exercise the modular index path: fill, drain, refill past the end.
        var ring = new RxAudioRing(4);
        ring.Write(new float[] { 1f / 32767, 2f / 32767, 3f / 32767 });
        Span<short> d1 = stackalloc short[3];
        ring.Read(d1);   // head/tail now mid-buffer
        ring.Write(new float[] { 4f / 32767, 5f / 32767, 6f / 32767 });

        Span<short> d2 = stackalloc short[3];
        int n = ring.Read(d2);

        Assert.Equal(3, n);
        Assert.Equal((short)4, d2[0]);
        Assert.Equal((short)5, d2[1]);
        Assert.Equal((short)6, d2[2]);
    }
}
