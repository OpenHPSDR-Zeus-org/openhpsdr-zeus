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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers.Binary;

namespace Zeus.Protocol1.Tests;

public class FramingTests
{
    [Fact]
    public void Int24BigEndian_PositiveMax_ScalesToNearOne()
    {
        // 0x7F_FF_FF = +8_388_607 → +8_388_607 / 8_388_608 ≈ +0.99999988.
        int v = PacketParser.ReadInt24BigEndian(stackalloc byte[] { 0x7F, 0xFF, 0xFF });
        Assert.Equal(8_388_607, v);
        Assert.InRange(PacketParser.ScaleInt24(v), 0.9999998, 1.0);
    }

    [Fact]
    public void Int24BigEndian_MidPositive_ScalesToHalf()
    {
        // 0x40_00_00 = +4_194_304 → +0.5 exactly.
        int v = PacketParser.ReadInt24BigEndian(stackalloc byte[] { 0x40, 0x00, 0x00 });
        Assert.Equal(4_194_304, v);
        Assert.Equal(0.5, PacketParser.ScaleInt24(v), 10);
    }

    [Fact]
    public void Int24BigEndian_NegativeValue_SignExtends()
    {
        // 0x80_00_00 = −8_388_608 → −1.0 exactly.
        int v = PacketParser.ReadInt24BigEndian(stackalloc byte[] { 0x80, 0x00, 0x00 });
        Assert.Equal(-8_388_608, v);
        Assert.Equal(-1.0, PacketParser.ScaleInt24(v), 10);
    }

    [Fact]
    public void Int24BigEndian_NegativeSmall_SignExtends()
    {
        // 0xFF_80_00 = −0x8000 = −32_768.
        int v = PacketParser.ReadInt24BigEndian(stackalloc byte[] { 0xFF, 0x80, 0x00 });
        Assert.Equal(-32_768, v);
    }

    [Fact]
    public void ParsePacket_ExtractsIqSamples()
    {
        // Two USB frames with known IQ values.
        var iqPairs = new (int i, int q)[PacketParser.ComplexSamplesPerPacket];
        for (int n = 0; n < iqPairs.Length; n++)
        {
            // Pick distinct values that span signs and magnitudes.
            iqPairs[n] = (i: (n - 63) * 1000, q: (63 - n) * 2000);
        }

        byte[] packet = BuildValidPacket(0xDEAD_BEEF, iqPairs);
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out uint seq, out int n2));
        Assert.Equal(0xDEAD_BEEFu, seq);
        Assert.Equal(PacketParser.ComplexSamplesPerPacket, n2);

        for (int k = 0; k < iqPairs.Length; k++)
        {
            Assert.Equal(PacketParser.ScaleInt24(iqPairs[k].i), outBuf[2 * k], 12);
            Assert.Equal(PacketParser.ScaleInt24(iqPairs[k].q), outBuf[2 * k + 1], 12);
        }
    }

    [Fact]
    public void ParsePacket_RejectsBadMetisMagic()
    {
        byte[] packet = BuildValidPacket(1, BuildZeroPairs());
        packet[0] = 0x00;
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.False(PacketParser.TryParsePacket(packet, outBuf, out _, out _));
    }

    [Fact]
    public void ParsePacket_RejectsBadSyncBytes()
    {
        byte[] packet = BuildValidPacket(1, BuildZeroPairs());
        packet[8] = 0x00; // first USB frame's sync[0]
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.False(PacketParser.TryParsePacket(packet, outBuf, out _, out _));
    }

    [Fact]
    public void ParsePacket_RejectsWrongLength()
    {
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.False(PacketParser.TryParsePacket(new byte[500], outBuf, out _, out _));
    }

    [Fact]
    public void SequenceTracker_DetectsGapOfThree()
    {
        // Feed packets with sequence {0, 1, 2, 5}. Parser/client reports 2 drops.
        var tracker = new PacketParser.SequenceTracker();
        foreach (uint s in new uint[] { 0, 1, 2, 5 }) tracker.Observe(s);
        Assert.Equal(2, tracker.DroppedFrames);
        Assert.Equal(4, tracker.TotalFrames);
    }

    [Fact]
    public void SequenceTracker_ResetOnRadioRestart()
    {
        var tracker = new PacketParser.SequenceTracker();
        foreach (uint s in new uint[] { 100, 101, 0, 1 }) tracker.Observe(s);
        Assert.Equal(0, tracker.DroppedFrames); // seq reset is not a drop
        Assert.Equal(4, tracker.TotalFrames);
    }

    [Fact]
    public void SampleCount_At192k_Is126()
    {
        // The brief calls out that the packet structure is identical across rates:
        // 126 complex samples per packet at any rate (doc 02 §5).
        Assert.Equal(126, PacketParser.ComplexSamplesPerPacket);

        byte[] packet = BuildValidPacket(42, BuildZeroPairs());
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out int samples));
        Assert.Equal(126, samples);
    }

    // --- fixture helpers -------------------------------------------------

    private static (int, int)[] BuildZeroPairs()
    {
        var arr = new (int, int)[PacketParser.ComplexSamplesPerPacket];
        return arr; // zero-initialized
    }

    /// <summary>Build a 1032-byte Metis EP6 data packet carrying the given IQ pairs.</summary>
    internal static byte[] BuildValidPacket(uint seq, (int i, int q)[] pairs)
    {
        if (pairs.Length != PacketParser.ComplexSamplesPerPacket)
            throw new ArgumentException("need 126 IQ pairs", nameof(pairs));

        var packet = new byte[PacketParser.PacketLength];
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x01;
        packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), seq);

        int pairIdx = 0;
        for (int f = 0; f < 2; f++)
        {
            int frameStart = 8 + f * 512;
            packet[frameStart + 0] = 0x7F;
            packet[frameStart + 1] = 0x7F;
            packet[frameStart + 2] = 0x7F;
            // C&C bytes 3..7 zero — parser ignores them on RX.

            int payloadStart = frameStart + 8;
            for (int s = 0; s < PacketParser.ComplexSamplesPerUsbFrame; s++)
            {
                int off = payloadStart + s * 8;
                WriteInt24BigEndian(packet.AsSpan(off, 3), pairs[pairIdx].i);
                WriteInt24BigEndian(packet.AsSpan(off + 3, 3), pairs[pairIdx].q);
                // bytes off+6, off+7 = mic sample (left zero).
                pairIdx++;
            }
        }
        return packet;
    }

    private static void WriteInt24BigEndian(Span<byte> dst, int value)
    {
        dst[0] = (byte)((value >> 16) & 0xFF);
        dst[1] = (byte)((value >> 8) & 0xFF);
        dst[2] = (byte)(value & 0xFF);
    }
}
