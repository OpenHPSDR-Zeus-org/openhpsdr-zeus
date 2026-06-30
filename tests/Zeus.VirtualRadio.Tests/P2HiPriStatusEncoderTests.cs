// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Protocol2;          // public DecodeHiPriStatus
using Zeus.VirtualRadio.P2;
using Zeus.VirtualRadio.Rf;

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Anti-drift round-trip tests for <see cref="P2HiPriStatusEncoder"/>: the
/// packet body (after the 4-byte sequence header the client strips) is fed
/// through the REAL <see cref="Protocol2Client.DecodeHiPriStatus"/> and the
/// FWD/REF/exciter/PTT/PLL/overload fields must survive. Socketless.
/// </summary>
public class P2HiPriStatusEncoderTests
{
    private static (P2TelemetryReading reading, byte[] packet) Encode(
        RfTelemetry tel, bool ptt, bool pll, byte overload)
    {
        var enc = new P2HiPriStatusEncoder();
        byte[] packet = enc.Build(sequence: 42, tel, ptt, pll, overload);
        // The client strips the 4-byte BE seq header before decoding.
        var body = packet.AsSpan(P2Wire.HiPriSeqHeaderBytes);
        return (Protocol2Client.DecodeHiPriStatus(body), packet);
    }

    [Fact]
    public void Build_KeyedTelemetry_RoundTripsFwdRevExciterPtt()
    {
        var tel = new RfTelemetry(FwdAdc: 2048, RefAdc: 96, FwdWatts: 9.5, RefWatts: 0.1, Swr: 1.1);
        var (reading, _) = Encode(tel, ptt: true, pll: true, overload: 0x00);

        Assert.Equal((ushort)2048, reading.FwdAdc);
        Assert.Equal((ushort)96, reading.RevAdc);
        Assert.Equal((ushort)2048, reading.ExciterAdc); // exciter mirrors FWD
        Assert.True(reading.PttIn);
        Assert.True(reading.PllLocked);
        Assert.Equal((byte)0x00, reading.AdcOverloadBits);
    }

    [Fact]
    public void Build_AtRest_FwdRevZero_NoPtt()
    {
        var tel = new RfTelemetry(0, 0, 0, 0, 1.0);
        var (reading, _) = Encode(tel, ptt: false, pll: true, overload: 0x00);

        Assert.Equal((ushort)0, reading.FwdAdc);
        Assert.Equal((ushort)0, reading.RevAdc);
        Assert.False(reading.PttIn);
        Assert.True(reading.PllLocked);
    }

    [Fact]
    public void Build_AdcOverloadBit_SurvivesDecode()
    {
        // The emulator asserts ADC overload when the host fails to seed byte 59
        // while the single-ADC PS time-mux is armed.
        var tel = new RfTelemetry(1000, 50, 1.0, 0.05, 1.05);
        var (reading, _) = Encode(tel, ptt: true, pll: true, overload: 0x01);
        Assert.Equal((byte)0x01, reading.AdcOverloadBits);
    }

    [Fact]
    public void Build_PacketLength_MeetsClientMinimum()
    {
        var (_, packet) = Encode(new RfTelemetry(1, 2, 0, 0, 1.0), true, true, 0);
        // 4-byte seq + at least 20-byte body = the client's HiPriStatusMinBytes.
        Assert.True(packet.Length >= P2Wire.HiPriSeqHeaderBytes + P2Wire.HiPriStatusMinBody);
    }
}
