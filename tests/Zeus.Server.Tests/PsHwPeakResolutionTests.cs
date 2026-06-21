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

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// Per-protocol / per-board PureSignal HW-peak resolution. Sourced from
/// Thetis clsHardwareSpecific.cs:295-318 + pihpsdr transmitter.c:1166-1179.
/// These values land via SetPSHWPeak to the WDSP calcc stage; getting them
/// wrong by even a few percent makes the correction curve fight the radio.
/// </summary>
public class PsHwPeakResolutionTests
{
    [Fact]
    public void Protocol1_Hermes_Defaults_To_0_4072()
    {
        Assert.Equal(0.4072, RadioService.ResolvePsHwPeak(false, HpsdrBoardKind.Hermes));
        Assert.Equal(0.4072, RadioService.ResolvePsHwPeak(false, HpsdrBoardKind.Angelia));
        Assert.Equal(0.4072, RadioService.ResolvePsHwPeak(false, HpsdrBoardKind.Orion));
    }

    [Fact]
    public void Protocol2_OrionMkII_Defaults_To_0_6121()
    {
        Assert.Equal(0.6121, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.OrionMkII));
    }

    [Fact]
    public void Protocol2_Default_Is_0_2899()
    {
        Assert.Equal(0.2899, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.Hermes));
        Assert.Equal(0.2899, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.Angelia));
        Assert.Equal(0.2899, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.Unknown));
    }

    [Fact]
    public void HermesLite2_Both_Protocols_Use_0_233()
    {
        // mi0bot clsHardwareSpecific.cs:312 PSDefaultPeak = 0.233 — the
        // canonical HL2 value. Same value either protocol; the HL2
        // hardware peak is determined by the mod, not the protocol.
        Assert.Equal(0.233, RadioService.ResolvePsHwPeak(false, HpsdrBoardKind.HermesLite2));
        Assert.Equal(0.233, RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.HermesLite2));
    }

    [Theory]
    [InlineData(OrionMkIIVariant.G2,            0.6121)]
    [InlineData(OrionMkIIVariant.G2_1K,         0.6121)]
    [InlineData(OrionMkIIVariant.Anan7000DLE,   0.2899)]
    [InlineData(OrionMkIIVariant.Anan8000DLE,   0.2899)]
    [InlineData(OrionMkIIVariant.OrionMkII,     0.2899)]
    [InlineData(OrionMkIIVariant.AnvelinaPro3,  0.2899)]
    [InlineData(OrionMkIIVariant.RedPitaya,     0.2899)]
    public void Protocol2_OrionMkII_Variant_Disambiguates_Saturn_From_OrionClass(
        OrionMkIIVariant variant, double expectedPeak)
    {
        // Phase 6 of issue #218: only the Saturn-FPGA variants (G2 / G2-1K)
        // get the 0.6121 peak per Thetis clsHardwareSpecific.cs:313. Every
        // other 0x0A variant inherits the OrionMkII-class default 0.2899.
        Assert.Equal(expectedPeak,
            RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.OrionMkII, variant));
    }

    [Fact]
    public void Default_Variant_Overload_Matches_Variant_G2()
    {
        // Backward-compat: the no-variant overload defaults to G2, so
        // pre-#218 callers continue to see the Saturn peak for OrionMkII.
        Assert.Equal(
            RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2),
            RadioService.ResolvePsHwPeak(true, HpsdrBoardKind.OrionMkII));
    }

    [Theory]
    [MemberData(nameof(EveryNonOrionMkIIBoard))]
    public void Variant_Ignored_For_NonOrionMkII_Boards(HpsdrBoardKind board)
    {
        foreach (var variant in Enum.GetValues<OrionMkIIVariant>())
        {
            Assert.Equal(
                RadioService.ResolvePsHwPeak(true, board, OrionMkIIVariant.G2),
                RadioService.ResolvePsHwPeak(true, board, variant));
            Assert.Equal(
                RadioService.ResolvePsHwPeak(false, board, OrionMkIIVariant.G2),
                RadioService.ResolvePsHwPeak(false, board, variant));
        }
    }

    public static IEnumerable<object[]> EveryNonOrionMkIIBoard() =>
        Enum.GetValues<HpsdrBoardKind>()
            .Where(b => b != HpsdrBoardKind.OrionMkII)
            .Select(b => new object[] { b });
}
