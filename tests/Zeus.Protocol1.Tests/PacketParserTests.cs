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

public class PacketParserTests
{
    [Fact]
    public void TryParsePacket_NoAinEcho_TelemetryDefault()
    {
        // FramingTests.BuildValidPacket leaves the C&C echo zero → C0 byte = 0x00,
        // addr = 0. That's the Mercury/Penelope-version slot, not AIN-bearing, so
        // telemetry should stay at its default zero value.
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        bool ok = PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry);
        Assert.True(ok);
        Assert.Equal(default, telemetry);
    }

    [Theory]
    // Addr 1 (C0=0x08): Ain0 at C1..C2, Ain1 at C3..C4 — HL2 exciter/temp + FWD pwr.
    // Addr 2 (C0=0x10): REV pwr at C1..C2, ADC0 bias at C3..C4.
    // Addr 3 (C0=0x18): ADC1 bias at C1..C2.
    [InlineData((byte)0x08, (ushort)0x1234, (ushort)0x5678)]
    [InlineData((byte)0x10, (ushort)0x00AB, (ushort)0xFFEE)]
    [InlineData((byte)0x18, (ushort)0x8000, (ushort)0x0001)]
    public void TryParsePacket_AinEcho_PopulatesTelemetry(byte c0, ushort ain0, ushort ain1)
    {
        byte[] packet = FramingTests.BuildValidPacket(42, new (int, int)[PacketParser.ComplexSamplesPerPacket]);

        // Inject echo on the SECOND USB frame (the last AIN-bearing slot wins —
        // so this also covers the "last wins" ordering we documented).
        int usbStart = 8 + 512;
        packet[usbStart + 3] = c0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 4, 2), ain0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 6, 2), ain1);

        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        bool ok = PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry);

        Assert.True(ok);
        Assert.Equal(c0, telemetry.C0Address);
        Assert.Equal(ain0, telemetry.Ain0);
        Assert.Equal(ain1, telemetry.Ain1);
    }

    [Fact]
    public void TryParsePacket_BothFramesAinEchoes_LastWins()
    {
        byte[] packet = FramingTests.BuildValidPacket(7, new (int, int)[PacketParser.ComplexSamplesPerPacket]);

        // Frame 0 → addr 1, Frame 1 → addr 2. Parser should return frame-1 data.
        int f0 = 8;
        packet[f0 + 3] = 0x08;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f0 + 4, 2), 0x1111);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f0 + 6, 2), 0x2222);

        int f1 = 8 + 512;
        packet[f1 + 3] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f1 + 4, 2), 0xAAAA);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f1 + 6, 2), 0xBBBB);

        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry));

        Assert.Equal(0x10, telemetry.C0Address);
        Assert.Equal(0xAAAA, telemetry.Ain0);
        Assert.Equal(0xBBBB, telemetry.Ain1);
    }

    [Fact]
    public void TryParsePacket_NonAinAddress_LeavesTelemetryDefault()
    {
        // Addr 4 (C0 = 0x20) is Mercury-version / overload info, not AIN.
        byte[] packet = FramingTests.BuildValidPacket(3, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int usbStart = 8 + 512;
        packet[usbStart + 3] = 0x20;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 4, 2), 0xDEAD);

        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry));
        Assert.Equal(default, telemetry);
    }

    [Fact]
    public void TryParsePacket_BothFramesClear_OverloadBitsZero()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0, bits);
    }

    [Fact]
    public void TryParsePacket_FirstFrameSetsAdc0_BitZero()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f0 = 8;
        packet[f0 + 4] = 0x01; // C1[0] — ADC0 overload
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0x01, bits);
    }

    [Fact]
    public void TryParsePacket_SecondFrameSetsAdc1_BitOne()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f1 = 8 + 512;
        packet[f1 + 5] = 0x01; // C2[0] — ADC1 overload
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0x02, bits);
    }

    [Fact]
    public void TryParsePacket_BothFramesBothAdcs_AllBitsSet()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f0 = 8;
        int f1 = 8 + 512;
        packet[f0 + 4] = 0x01;
        packet[f0 + 5] = 0x01;
        packet[f1 + 4] = 0x01;
        packet[f1 + 5] = 0x01;
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0x03, bits);
    }

    [Fact]
    public void TryParsePacket_OverloadBitsOrAcrossFrames()
    {
        // ADC0 set on first frame only; ADC1 set on second frame only. Packet-level
        // result must report both bits.
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f0 = 8;
        int f1 = 8 + 512;
        packet[f0 + 4] = 0x01;
        packet[f1 + 5] = 0x01;
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0x03, bits);
    }

    [Fact]
    public void TryParsePacket_HighBitsOnC1AndC2_Ignored()
    {
        // Only bit 0 is the overload bit. Hermes IOx, TX-FIFO count, etc share
        // the byte — must not leak into our overload reading.
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int f0 = 8;
        packet[f0 + 4] = 0xFE; // everything except bit 0
        packet[f0 + 5] = 0xFE;
        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];

        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out _, out byte bits));
        Assert.Equal(0, bits);
    }

    [Fact]
    public void TryParsePacket_AddressMask_IgnoresStatusBits()
    {
        // Low 3 bits of the echoed C0 carry PTT/DOT/DASH/ADC0-overload — they
        // must not perturb address decoding. C0 = 0x08 | 0x01 (PTT set) should
        // still be recognised as addr-1.
        byte[] packet = FramingTests.BuildValidPacket(3, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        int usbStart = 8 + 512;
        packet[usbStart + 3] = 0x08 | 0x01; // addr 1 + PTT
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 4, 2), 0x0042);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 6, 2), 0x0043);

        var outBuf = new double[2 * PacketParser.ComplexSamplesPerPacket];
        Assert.True(PacketParser.TryParsePacket(packet, outBuf, out _, out _, out var telemetry));

        Assert.Equal(0x09, telemetry.C0Address);
        Assert.Equal(0x0042, telemetry.Ain0);
        Assert.Equal(0x0043, telemetry.Ain1);
    }

    // ---- HL2 PS-armed 4-DDC layout ----------------------------------------
    //
    // Regression coverage for the bug where TryParseHl2Ps4DdcPacket extracted
    // the four IQ streams but silently dropped the C&C echo bytes that carry
    // FWD/REF/PA-temp telemetry and ADC-overload status. With PS armed and
    // MOX/TUN active on HL2, every EP6 packet routes through this parser; if
    // it doesn't surface telemetry, the meter pipeline freezes at 0 W for
    // the entire transmission window. The C&C bytes live at usb[3..8] in
    // both the standard and 4-DDC layouts — only the payload below the
    // 8-byte USB header differs — so the extraction logic mirrors the
    // standard parser exactly.

    private static byte[] BuildValid4DdcPacket(uint seq)
    {
        var packet = new byte[PacketParser.PacketLength];
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x01;
        packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), seq);
        for (int f = 0; f < 2; f++)
        {
            int frameStart = 8 + f * 512;
            packet[frameStart + 0] = 0x7F;
            packet[frameStart + 1] = 0x7F;
            packet[frameStart + 2] = 0x7F;
            // C&C bytes 3..7 left zero; tests inject as needed.
            // Payload bytes left zero: parser reads 19 × 26 = 494 bytes per
            // USB frame and zero-byte slots decode to zero IQ samples, which
            // is fine — these tests assert telemetry/overload extraction,
            // not IQ values (those are covered by ddc-stream tests below).
        }
        return packet;
    }

    private static (double[] d0, double[] d1, double[] d2, double[] d3) Allocate4DdcBuffers()
    {
        int n = 2 * PacketParser.Hl2Ps4DdcSamplesPerPacket;
        return (new double[n], new double[n], new double[n], new double[n]);
    }

    [Fact]
    public void TryParseHl2Ps4DdcPacket_NoEcho_TelemetryDefault()
    {
        byte[] packet = BuildValid4DdcPacket(1);
        var (d0, d1, d2, d3) = Allocate4DdcBuffers();

        bool ok = PacketParser.TryParseHl2Ps4DdcPacket(
            packet, d0, d1, d2, d3,
            out _, out _, out var t0, out var t1, out byte bits);

        Assert.True(ok);
        Assert.Equal(default, t0);
        Assert.Equal(default, t1);
        Assert.Equal(0, bits);
    }

    [Theory]
    [InlineData((byte)0x08, (ushort)0x0F4A, (ushort)0x00DC)] // FWD slot, plausible HL2 ADCs
    [InlineData((byte)0x10, (ushort)0x00D6, (ushort)0x03A4)] // REF slot
    [InlineData((byte)0x18, (ushort)0x8000, (ushort)0x0001)] // ADC1 bias slot
    public void TryParseHl2Ps4DdcPacket_AinEcho_PopulatesTelemetry(byte c0, ushort ain0, ushort ain1)
    {
        byte[] packet = BuildValid4DdcPacket(42);
        int usbStart = 8 + 512;
        packet[usbStart + 3] = c0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 4, 2), ain0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 6, 2), ain1);

        var (d0, d1, d2, d3) = Allocate4DdcBuffers();
        bool ok = PacketParser.TryParseHl2Ps4DdcPacket(
            packet, d0, d1, d2, d3,
            out _, out _, out _, out var t1, out _);

        Assert.True(ok);
        Assert.Equal(c0, t1.C0Address);
        Assert.Equal(ain0, t1.Ain0);
        Assert.Equal(ain1, t1.Ain1);
    }

    [Fact]
    public void TryParseHl2Ps4DdcPacket_BothFramesAinEchoes_BothEmitted()
    {
        // Unlike the legacy 1-DDC overload (which collapses to "last wins"),
        // this parser must surface BOTH frames so the caller's per-frame
        // fan-out gets the FWD AND REF reading from a single packet — that
        // pairing is what makes SWR meaningful at the meter pipeline.
        byte[] packet = BuildValid4DdcPacket(7);
        int f0 = 8;
        packet[f0 + 3] = 0x08; // addr 1 — FWD slot
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f0 + 4, 2), 0x0F4A);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f0 + 6, 2), 0x00DC);

        int f1 = 8 + 512;
        packet[f1 + 3] = 0x10; // addr 2 — REF slot
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f1 + 4, 2), 0x00D6);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(f1 + 6, 2), 0x03A4);

        var (d0, d1, d2, d3) = Allocate4DdcBuffers();
        Assert.True(PacketParser.TryParseHl2Ps4DdcPacket(
            packet, d0, d1, d2, d3,
            out _, out _, out var t0, out var t1, out _));

        Assert.Equal(0x08, t0.C0Address);
        Assert.Equal(0x0F4A, t0.Ain0);
        Assert.Equal(0x00DC, t0.Ain1);
        Assert.Equal(0x10, t1.C0Address);
        Assert.Equal(0x00D6, t1.Ain0);
        Assert.Equal(0x03A4, t1.Ain1);
    }

    [Fact]
    public void TryParseHl2Ps4DdcPacket_AddressMask_IgnoresStatusBits()
    {
        // Same C0[0]=PTT echo handling as the standard parser: addr-1 with
        // PTT set arrives as 0x09 during MOX/TUN — must still be recognised
        // as the FWD/temp slot.
        byte[] packet = BuildValid4DdcPacket(11);
        int usbStart = 8 + 512;
        packet[usbStart + 3] = 0x08 | 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 4, 2), 0x0F00);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(usbStart + 6, 2), 0x0042);

        var (d0, d1, d2, d3) = Allocate4DdcBuffers();
        Assert.True(PacketParser.TryParseHl2Ps4DdcPacket(
            packet, d0, d1, d2, d3,
            out _, out _, out _, out var t1, out _));

        Assert.Equal(0x09, t1.C0Address);
        Assert.Equal(0x0F00, t1.Ain0);
        Assert.Equal(0x0042, t1.Ain1);
    }

    [Fact]
    public void TryParseHl2Ps4DdcPacket_OverloadBitsOrAcrossFrames()
    {
        byte[] packet = BuildValid4DdcPacket(1);
        int f0 = 8;
        int f1 = 8 + 512;
        packet[f0 + 4] = 0x01; // C1[0] — ADC0 overload, frame 0
        packet[f1 + 5] = 0x01; // C2[0] — ADC1 overload, frame 1
        var (d0, d1, d2, d3) = Allocate4DdcBuffers();

        Assert.True(PacketParser.TryParseHl2Ps4DdcPacket(
            packet, d0, d1, d2, d3,
            out _, out _, out _, out _, out byte bits));
        Assert.Equal(0x03, bits);
    }

    // ---- Standard dual-receiver 2-DDC layout (RX2) ------------------------
    //
    // NumReceiversMinusOne=1 interleaves DDC0 (RX1) and DDC1 (RX2) in 14-byte
    // sample slots (6 bytes I/Q each + 2 mic), 36 samples per USB frame, 72 per
    // packet per DDC. TryParse2DdcPacket must split the two streams into their
    // own buffers so RX2 demodulates its own NCO instead of a CTUN-shifted copy
    // of RX1 (the "duplicate waterfall" bug). Layout mirrors Thetis
    // networkproto1.c:358 (`spr = 504/(6*nddc+2)`, slot offset `iddc*6`).

    private static void WriteInt24(Span<byte> dst, int value)
    {
        dst[0] = (byte)((value >> 16) & 0xFF);
        dst[1] = (byte)((value >> 8) & 0xFF);
        dst[2] = (byte)(value & 0xFF);
    }

    private static byte[] BuildValid2DdcPacket(
        uint seq,
        Func<int, int> ddc0I, Func<int, int> ddc0Q,
        Func<int, int> ddc1I, Func<int, int> ddc1Q)
    {
        var packet = new byte[PacketParser.PacketLength];
        packet[0] = 0xEF;
        packet[1] = 0xFE;
        packet[2] = 0x01;
        packet[3] = 0x06;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), seq);
        int idx = 0;
        for (int f = 0; f < 2; f++)
        {
            int frameStart = 8 + f * 512;
            packet[frameStart + 0] = 0x7F;
            packet[frameStart + 1] = 0x7F;
            packet[frameStart + 2] = 0x7F;
            int payloadStart = frameStart + 8;
            for (int s = 0; s < PacketParser.TwoDdcSamplesPerUsbFrame; s++)
            {
                int off = payloadStart + s * PacketParser.TwoDdcBytesPerSlot;
                WriteInt24(packet.AsSpan(off, 3), ddc0I(idx));
                WriteInt24(packet.AsSpan(off + 3, 3), ddc0Q(idx));
                WriteInt24(packet.AsSpan(off + 6, 3), ddc1I(idx));
                WriteInt24(packet.AsSpan(off + 9, 3), ddc1Q(idx));
                // off+12, off+13 = mic (left zero).
                idx++;
            }
        }
        return packet;
    }

    [Fact]
    public void TryParse2DdcPacket_SplitsTheTwoReceivers()
    {
        // DDC0 = positive ramp; DDC1 = distinct negative ramp. A parser that
        // mixed up slot offsets (or duplicated DDC0 into DDC1 — the live bug)
        // would fail loudly.
        byte[] packet = BuildValid2DdcPacket(
            99,
            ddc0I: n => 0x010000 + n,
            ddc0Q: n => 0x020000 + n,
            ddc1I: n => -(0x030000 + n),
            ddc1Q: n => -(0x040000 + n));

        int cap = 2 * PacketParser.TwoDdcSamplesPerPacket;
        var d0 = new double[cap];
        var d1 = new double[cap];
        bool ok = PacketParser.TryParse2DdcPacket(
            packet, d0, d1, out uint seq, out int samples, out _, out _, out _);

        Assert.True(ok);
        Assert.Equal(99u, seq);
        Assert.Equal(PacketParser.TwoDdcSamplesPerPacket, samples);

        // First sample of each stream matches its own wire value...
        Assert.Equal(PacketParser.ScaleInt24(0x010000), d0[0], 12);
        Assert.Equal(PacketParser.ScaleInt24(0x020000), d0[1], 12);
        Assert.Equal(PacketParser.ScaleInt24(-0x030000), d1[0], 12);
        Assert.Equal(PacketParser.ScaleInt24(-0x040000), d1[1], 12);

        // ...and the last sample too (verifies the full 72-slot walk).
        int last = samples - 1;
        Assert.Equal(PacketParser.ScaleInt24(0x010000 + last), d0[2 * last], 12);
        Assert.Equal(PacketParser.ScaleInt24(-(0x040000 + last)), d1[2 * last + 1], 12);

        // The two receivers must be distinct — the whole point of the fix.
        Assert.NotEqual(d0[0], d1[0]);
    }

    [Fact]
    public void TryParse2DdcPacket_RejectsShortBuffers()
    {
        byte[] packet = BuildValid2DdcPacket(1, _ => 0, _ => 0, _ => 0, _ => 0);
        var tooSmall = new double[2 * PacketParser.TwoDdcSamplesPerPacket - 1];
        var ok = new double[2 * PacketParser.TwoDdcSamplesPerPacket];
        Assert.False(PacketParser.TryParse2DdcPacket(packet, tooSmall, ok, out _, out _, out _, out _, out _));
        Assert.False(PacketParser.TryParse2DdcPacket(packet, ok, tooSmall, out _, out _, out _, out _, out _));
    }

    // ---- ExtractHardwarePtt -------------------------------------------------
    // C0[0] is the PTT/MOX echo set by the HL2 gateware whenever the radio is
    // keying — host-driven MOX, rear KEY tip grounded, or external PTT line
    // asserted (HL2 protocol doc line 200). ExternalPttService consumes this
    // edge to follow hardware-initiated TX.

    [Fact]
    public void ExtractHardwarePtt_BothFramesClear_ReturnsFalse()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        Assert.False(PacketParser.ExtractHardwarePtt(packet));
    }

    [Fact]
    public void ExtractHardwarePtt_FirstFrameC0Bit0_ReturnsTrue()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        packet[8 + 3] |= 0x01;
        Assert.True(PacketParser.ExtractHardwarePtt(packet));
    }

    [Fact]
    public void ExtractHardwarePtt_SecondFrameC0Bit0_ReturnsTrue()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        packet[8 + 512 + 3] |= 0x01;
        Assert.True(PacketParser.ExtractHardwarePtt(packet));
    }

    [Fact]
    public void ExtractHardwarePtt_IgnoresOtherC0Bits()
    {
        // Bits 7:1 of C0 carry the address; only bit 0 is the PTT echo. Set
        // every other bit on both frames and expect false.
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        packet[8 + 3] = 0xFE;
        packet[8 + 512 + 3] = 0xFE;
        Assert.False(PacketParser.ExtractHardwarePtt(packet));
    }

    [Fact]
    public void ExtractHardwarePtt_ShortPacket_ReturnsFalse()
    {
        // Defensive guard for callers that don't pre-validate length.
        Assert.False(PacketParser.ExtractHardwarePtt(new byte[10]));
    }

    // ---- ExtractCwKeyDown (zeus-cl2) ----------------------------------------
    // C0[2] = cw_key_status, the gateware's shaped keyer OUTPUT (per dit/dah),
    // distinct from C0[0] = ptt_resp (held for the whole keyed period). The
    // sidetone follows C0[2]; MOX follows C0[0].

    [Fact]
    public void ExtractCwKeyDown_BothFramesClear_ReturnsFalse()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        Assert.False(PacketParser.ExtractCwKeyDown(packet));
    }

    [Fact]
    public void ExtractCwKeyDown_FirstFrameC0Bit2_ReturnsTrue()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        packet[8 + 3] |= 0x04;
        Assert.True(PacketParser.ExtractCwKeyDown(packet));
    }

    [Fact]
    public void ExtractCwKeyDown_SecondFrameC0Bit2_ReturnsTrue()
    {
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        packet[8 + 512 + 3] |= 0x04;
        Assert.True(PacketParser.ExtractCwKeyDown(packet));
    }

    [Fact]
    public void ExtractCwKeyDown_IsIndependentOfPttBit()
    {
        // The whole point of the fix: PTT (C0[0]) and cw_key_status (C0[2])
        // are separate. A held PTT with the keyer momentarily up (between
        // dits) must read key-DOWN false even though PTT is true.
        byte[] packet = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        packet[8 + 3] |= 0x01;          // ptt_resp set
        Assert.False(PacketParser.ExtractCwKeyDown(packet));   // but key is up
        Assert.True(PacketParser.ExtractHardwarePtt(packet));  // ptt still seen

        // And the reverse: keyer down but PTT bit clear reads key-down true.
        byte[] keyOnly = FramingTests.BuildValidPacket(1, new (int, int)[PacketParser.ComplexSamplesPerPacket]);
        keyOnly[8 + 3] |= 0x04;         // cw_key_status set, ptt clear
        Assert.True(PacketParser.ExtractCwKeyDown(keyOnly));
        Assert.False(PacketParser.ExtractHardwarePtt(keyOnly));
    }

    [Fact]
    public void ExtractCwKeyDown_ShortPacket_ReturnsFalse()
    {
        Assert.False(PacketParser.ExtractCwKeyDown(new byte[10]));
    }
}
