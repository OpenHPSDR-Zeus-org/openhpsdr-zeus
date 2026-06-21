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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Zeus.Server;

namespace Zeus.Server.Tests;

// Pin the drive-byte math down. A silent regression here emits the wrong RF
// power without any UI signal, so this is the most load-bearing formula in the
// whole TX path. Reference: Thetis console.cs:46801-46841, piHPSDR
// radio.c:2809-2828.
public class ComputeDriveByteTests
{
    [Theory]
    [InlineData(0,   0.0,   0, 0)]     // zero percent always zero
    [InlineData(100, 0.0,   0, 255)]   // legacy full scale (maxW=0 → pct×255/100)
    [InlineData(50,  40.5,  0, 127)]   // legacy ignores gain when maxW=0
    [InlineData(50,  0.0,   0, 127)]
    public void LegacyMode_Ignores_Gain_And_Scales_Linearly(int pct, double gainDb, int maxW, int expected)
    {
        Assert.Equal((byte)expected, RadioService.ComputeDriveByte(pct, gainDb, maxW));
    }

    [Fact]
    public void Zero_Percent_Zero_Byte_Even_With_Calibration()
    {
        Assert.Equal((byte)0, RadioService.ComputeDriveByte(0, 40.5, 5));
    }

    [Fact]
    public void Over_Range_Clamps_To_255()
    {
        // 5 W target with 0 dB PA gain: sqrt(5·50) ≈ 15.8 V ≫ 0.8 V scale. norm
        // clamps to 1.0 and byte pins at 255.
        Assert.Equal((byte)255, RadioService.ComputeDriveByte(100, 0.0, 5));
    }

    [Fact]
    public void Hl2_Calibrated_100pct_5W_Matches_PiHPSDR_Default()
    {
        // HL2 piHPSDR default: pa_calibration = 40.5 dB, pa_power = 5 W.
        // At 100% target = 5 W → sourceW = 5 / 10^4.05 ≈ 4.46e-4 W →
        // sourceV = sqrt(4.46e-4 × 50) ≈ 0.1494 V → norm ≈ 0.1867 →
        // byte ≈ 47. Pin at ±1 to allow for rounding drift across
        // platforms — the reference clients themselves emit ~47 here.
        byte result = RadioService.ComputeDriveByte(100, 40.5, 5);
        Assert.InRange(result, 46, 48);
    }

    [Fact]
    public void Equal_Pct_At_Same_Band_Gives_Equal_Byte()
    {
        // The whole point of the tune-slider feature: `20% drive ≈ 20% tune`
        // in on-air watts, because they share the same band-gain calibration.
        byte drive = RadioService.ComputeDriveByte(20, 40.5, 5);
        byte tune = RadioService.ComputeDriveByte(20, 40.5, 5);
        Assert.Equal(drive, tune);
    }

    [Fact]
    public void Negative_Percent_Clamps_To_Zero()
    {
        Assert.Equal((byte)0, RadioService.ComputeDriveByte(-10, 40.5, 5));
    }

    [Fact]
    public void Above_100_Percent_Clamps_To_100()
    {
        byte at100 = RadioService.ComputeDriveByte(100, 40.5, 5);
        byte at150 = RadioService.ComputeDriveByte(150, 40.5, 5);
        Assert.Equal(at100, at150);
    }
}
