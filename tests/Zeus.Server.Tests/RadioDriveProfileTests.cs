// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Per-board drive-byte encoding.
//
// HL2 uses a PERCENTAGE-based drive model from mi0bot openhpsdr-thetis
// (the HL2-specific Thetis fork) — the PaGainDb field is an output
// percentage 0..100, NOT dB. See HermesLite2DriveProfile's class comment
// for the full derivation from console.cs:49290-49299 and audio.cs:249-258
// in that fork. Full-byte (Hermes / ANAN / Orion) keeps the piHPSDR /
// Thetis dB model — that's dB forward PA gain, and the math is a
// targetW → sourceV → byte chain.
//
// Tests pin both conventions so a future refactor that reunifies them
// (or accidentally applies one board's model to another) fails loudly.
public class RadioDriveProfileTests
{
    [Fact]
    public void Dispatch_Hl2_Returns_HermesLite2Profile()
    {
        Assert.IsType<HermesLite2DriveProfile>(RadioDriveProfiles.For(HpsdrBoardKind.HermesLite2));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Orion)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.Unknown)]
    public void Dispatch_NonHl2_Returns_FullByteProfile(HpsdrBoardKind board)
    {
        Assert.IsType<FullByteDriveProfile>(RadioDriveProfiles.For(board));
    }

    // Full-byte (dB) model: unchanged. PaGainDb here is real decibels.
    [Theory]
    [InlineData(0,   0.0,  0, 0)]
    [InlineData(100, 0.0,  0, 255)]
    [InlineData(100, 40.5, 5, 48)]     // piHPSDR default on 5 W rated
    [InlineData(100, 26.0, 5, 253)]    // calibrated ANAN/Hermes at 5 W
    public void FullByteProfile_Uses_DbModel(int pct, double gainDb, int maxW, int expected)
    {
        Assert.Equal((byte)expected, FullByteDriveProfile.Instance.EncodeDriveByte(pct, gainDb, maxW));
    }

    // HL2 percentage model: slider × band-pct → raw byte → nibble-quantise.
    //   byte_raw = (drivePct/100) * (paPct/100) * 255
    //   byte     = round(byte_raw / 16) * 16   (saturate at 240)
    //
    // The mi0bot invariant: slider=100 with band-pct=100 must reach nibble
    // 0xF (rated output). slider=100 with band-pct=38.8 (6m) must cap at
    // nibble 0x6 (matches mi0bot's 6 m PA soft-cap — see
    // clsHardwareSpecific.cs:778 in that fork).
    [Theory]
    [InlineData(0,   100.0, 0)]     // slider off → no output
    [InlineData(100, 0.0,   0)]     // band-pct 0 → no output
    [InlineData(100, 100.0, 240)]   // full slider, no band-cap → nibble 0xF (rated)
    [InlineData(100, 38.8,   96)]   // full slider at 6m soft-cap → nibble 0x6
    [InlineData(50,  100.0, 128)]   // half slider → nibble 0x8 (~53 %)
    [InlineData(25,  100.0,  64)]   // quarter slider → nibble 0x4
    [InlineData(12,  100.0,  32)]   // ~12 % → nibble 0x2
    public void Hl2Profile_PercentModel_Maps_To_Correct_Nibble(int pct, double bandPct, int expected)
    {
        byte b = HermesLite2DriveProfile.Instance.EncodeDriveByte(pct, bandPct, maxWatts: 5);
        Assert.Equal((byte)expected, b);
    }

    [Fact]
    public void Hl2Profile_Ignores_MaxWatts()
    {
        // HL2 percentage model doesn't consult rated watts (there's no
        // target-watts formula). Varying maxWatts must not shift the byte.
        var p = HermesLite2DriveProfile.Instance;
        byte b5   = p.EncodeDriveByte(100, 100.0, 5);
        byte b100 = p.EncodeDriveByte(100, 100.0, 100);
        byte b0   = p.EncodeDriveByte(100, 100.0, 0);
        Assert.Equal(b5, b100);
        Assert.Equal(b5, b0);
    }

    [Fact]
    public void Hl2Profile_FullSlider_FullBand_Hits_Nibble_0xF()
    {
        // Mi0bot invariant: slider=100 + band-pct=100 lands the upper nibble
        // at 0xF so the radio produces rated output. This is the axis the
        // old Zeus dB-math got wrong (byte=48 → nibble 0x3 → 1 W).
        byte b = HermesLite2DriveProfile.Instance.EncodeDriveByte(100, 100.0, 5);
        Assert.Equal(15, b >> 4);
        Assert.Equal(240, b);
    }

    [Fact]
    public void Hl2Profile_Clamps_Slider_And_BandPct_To_Domain()
    {
        // Out-of-range inputs must not overflow or produce junk. Slider
        // clamp = 0..100; band-pct clamp = 0..100.
        var p = HermesLite2DriveProfile.Instance;
        Assert.Equal((byte)0,   p.EncodeDriveByte(-5, 100.0, 5));
        Assert.Equal((byte)240, p.EncodeDriveByte(200, 100.0, 5));
        Assert.Equal((byte)0,   p.EncodeDriveByte(100, -10.0, 5));
        Assert.Equal((byte)240, p.EncodeDriveByte(100, 250.0, 5));
    }

    [Fact]
    public void Zero_Drive_Percent_Is_Always_Zero_Byte_Everywhere()
    {
        Assert.Equal((byte)0, FullByteDriveProfile.Instance.EncodeDriveByte(0, 40.5, 5));
        Assert.Equal((byte)0, HermesLite2DriveProfile.Instance.EncodeDriveByte(0, 100.0, 5));
    }
}
