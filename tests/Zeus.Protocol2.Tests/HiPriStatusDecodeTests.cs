// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Xunit;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Wire-format tests for the Protocol-2 hi-priority status packet
/// (radio → host on UDP 1025). Field offsets sourced from Thetis
/// <c>ChannelMaster/network.c:683-756</c>:
///
/// <list type="bullet">
///   <item>byte 0 — bit 0 PTT, bit 1 Dot, bit 2 Dash, bit 4 PLL locked</item>
///   <item>byte 1 — ADC0..7 overload bits</item>
///   <item>bytes 2..3 — exciter power ADC (BE u16)</item>
///   <item>bytes 10..11 — PA forward power ADC (BE u16)</item>
///   <item>bytes 18..19 — PA reverse power ADC (BE u16)</item>
///   <item>bytes 35..38 — ADC0/ADC1 max magnitude (G2 v27+)</item>
///   <item>bytes 45..55 — supply volts, user ADCs, user digital input</item>
/// </list>
///
/// Issue #174 — without these offsets being honoured, the operator-facing
/// TX power meter on a P2-connected G2 / Orion / ANAN sat at zero.
/// </summary>
public class HiPriStatusDecodeTests
{
    /// <summary>Build a fresh 60-byte hi-pri status payload zeroed.</summary>
    private static byte[] EmptyPacket() => new byte[60];

    /// <summary>Write a u16 big-endian into the buffer.</summary>
    private static void WriteBeU16(byte[] buf, int offset, ushort value)
    {
        buf[offset]     = (byte)(value >> 8);
        buf[offset + 1] = (byte)(value & 0xFF);
    }

    [Fact]
    public void Decode_Zeros_AllFieldsZero()
    {
        var buf = EmptyPacket();
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.Equal(0, r.FwdAdc);
        Assert.Equal(0, r.RevAdc);
        Assert.Equal(0, r.ExciterAdc);
        Assert.False(r.PttIn);
        Assert.False(r.PllLocked);
        Assert.False(r.DotIn);
        Assert.False(r.DashIn);
        Assert.Equal(0, r.AdcOverloadBits);
        Assert.Equal(0, r.SupplyVoltsAdc);
    }

    [Fact]
    public void Decode_FwdPower_AtBytes10And11_BigEndian()
    {
        var buf = EmptyPacket();
        WriteBeU16(buf, 10, 0x1234);
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.Equal(0x1234, r.FwdAdc);
        // No bleed into the other axes.
        Assert.Equal(0, r.RevAdc);
        Assert.Equal(0, r.ExciterAdc);
    }

    [Fact]
    public void Decode_RevPower_AtBytes18And19_BigEndian()
    {
        var buf = EmptyPacket();
        WriteBeU16(buf, 18, 0xBEEF);
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.Equal(0xBEEF, r.RevAdc);
        Assert.Equal(0, r.FwdAdc);
        Assert.Equal(0, r.ExciterAdc);
    }

    [Fact]
    public void Decode_ExciterPower_AtBytes2And3_BigEndian()
    {
        var buf = EmptyPacket();
        WriteBeU16(buf, 2, 0xCAFE);
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.Equal(0xCAFE, r.ExciterAdc);
        Assert.Equal(0, r.FwdAdc);
        Assert.Equal(0, r.RevAdc);
    }

    [Fact]
    public void Decode_PttIn_FromByte0Bit0()
    {
        var buf = EmptyPacket();
        buf[0] = 0x01;
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.True(r.PttIn);
        Assert.False(r.PllLocked);
    }

    [Fact]
    public void Decode_PllLocked_FromByte0Bit4()
    {
        var buf = EmptyPacket();
        buf[0] = 0x10;
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.False(r.PttIn);
        Assert.True(r.PllLocked);
    }

    [Fact]
    public void Decode_FullPacket_AllFieldsIndependent()
    {
        // Realistic dual-set: PTT in, PLL locked, FWD reading mid-scale,
        // REV near-zero (matched antenna), exciter at 12-bit max.
        var buf = EmptyPacket();
        buf[0] = 0x11; // PTT + PLL
        WriteBeU16(buf, 2, 0x0FFF);   // exciter: 12-bit full scale
        WriteBeU16(buf, 10, 0x0800);  // FWD: 2048 / 4095
        WriteBeU16(buf, 18, 0x0040);  // REV: small
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.True(r.PttIn);
        Assert.True(r.PllLocked);
        Assert.Equal(0x0FFF, r.ExciterAdc);
        Assert.Equal(0x0800, r.FwdAdc);
        Assert.Equal(0x0040, r.RevAdc);
    }

    [Fact]
    public void Decode_DotAndDashBits_DoNotLeakIntoPllOrPtt()
    {
        // Byte 0 with Dot (bit 1) + Dash (bit 2) only — neither PTT nor PLL.
        var buf = EmptyPacket();
        buf[0] = 0x06;
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.False(r.PttIn);
        Assert.False(r.PllLocked);
        Assert.True(r.DotIn);
        Assert.True(r.DashIn);
    }

    [Fact]
    public void Decode_AdcOverloadBits_FromByte1()
    {
        var buf = EmptyPacket();
        buf[1] = 0b1010_0011;
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.Equal(0b1010_0011, r.AdcOverloadBits);
    }

    [Fact]
    public void Decode_G2DiagnosticFields_FromThetisOffsets()
    {
        var buf = EmptyPacket();
        buf[0] = 0x80; // sidetone
        WriteBeU16(buf, 26, 0x0102); // hardware LEDs
        WriteBeU16(buf, 35, 0x2222); // ADC0 max magnitude
        WriteBeU16(buf, 37, 0x3333); // ADC1 max magnitude
        WriteBeU16(buf, 45, 0x4444); // supply volts ADC
        WriteBeU16(buf, 47, 0xAAAA); // user ADC3
        WriteBeU16(buf, 49, 0xBBBB); // user ADC2
        WriteBeU16(buf, 51, 0xCCCC); // user ADC1
        WriteBeU16(buf, 53, 0xDDDD); // user ADC0
        buf[55] = 0x1B;

        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.True(r.SidetoneActive);
        Assert.Equal(0x0102, r.HardwareLeds);
        Assert.Equal(0x2222, r.Adc0MaxMagnitude);
        Assert.Equal(0x3333, r.Adc1MaxMagnitude);
        Assert.Equal(0x4444, r.SupplyVoltsAdc);
        Assert.Equal(0xDDDD, r.UserAdc0);
        Assert.Equal(0xCCCC, r.UserAdc1);
        Assert.Equal(0xBBBB, r.UserAdc2);
        Assert.Equal(0xAAAA, r.UserAdc3);
        Assert.Equal(0x1B, r.UserDigitalIn);
    }

    [Fact]
    public void Decode_AcceptsLongerPayload()
    {
        // Some firmwares pad the packet to a longer length. The decoder
        // must read only the first 20 bytes; the tail is irrelevant.
        var buf = new byte[256];
        WriteBeU16(buf, 10, 0xABCD);
        WriteBeU16(buf, 18, 0x1357);
        // Trailing garbage shouldn't influence the readout.
        for (int i = 20; i < buf.Length; i++) buf[i] = 0xFF;
        var r = Protocol2Client.DecodeHiPriStatus(buf);
        Assert.Equal(0xABCD, r.FwdAdc);
        Assert.Equal(0x1357, r.RevAdc);
    }
}
