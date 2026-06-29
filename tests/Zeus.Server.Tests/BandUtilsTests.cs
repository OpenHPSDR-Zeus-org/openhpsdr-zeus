// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Issue #917 — pin the FreqToBand boundary table. Rotator auto-routing keys
// off this mapping, and a careless edit to a band edge would silently point
// the wrong antenna. No hardware required — it's a pure function.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class BandUtilsTests
{
    [Theory]
    // Below 160m → null.
    [InlineData(1_799_999, null)]
    // Lower edge of each band resolves to that band; edge-1 Hz resolves to the
    // band below (strict '<' semantics).
    [InlineData(1_800_000, "160m")]
    [InlineData(3_499_999, "160m")]
    [InlineData(3_500_000, "80m")]
    [InlineData(5_299_999, "80m")]
    [InlineData(5_300_000, "60m")]
    [InlineData(6_999_999, "60m")]
    [InlineData(7_000_000, "40m")]
    [InlineData(10_099_999, "40m")]
    [InlineData(10_100_000, "30m")]
    [InlineData(13_999_999, "30m")]
    [InlineData(14_000_000, "20m")]
    [InlineData(18_067_999, "20m")]
    [InlineData(18_068_000, "17m")]
    [InlineData(20_999_999, "17m")]
    [InlineData(21_000_000, "15m")]
    [InlineData(24_889_999, "15m")]
    [InlineData(24_890_000, "12m")]
    [InlineData(27_999_999, "12m")]
    [InlineData(28_000_000, "10m")]
    [InlineData(49_999_999, "10m")]
    [InlineData(50_000_000, "6m")]
    [InlineData(53_999_999, "6m")]
    // Above 6m → null.
    [InlineData(54_000_000, null)]
    public void FreqToBand_ResolvesEdgesExactly(long hz, string? expected)
    {
        Assert.Equal(expected, BandUtils.FreqToBand(hz));
    }

    [Fact]
    public void FreqToBand_EveryHfBandName_IsProducibleFromSomeFrequency()
    {
        // Guards against a band name in HfBands that FreqToBand can never emit
        // (which would make that band un-auto-routable).
        var producible = new[]
        {
            1_900_000L, 3_700_000, 5_350_000, 7_100_000, 10_120_000, 14_100_000,
            18_100_000, 21_100_000, 24_920_000, 28_300_000, 50_100_000,
        }.Select(BandUtils.FreqToBand).ToHashSet();
        foreach (var band in BandUtils.HfBands)
            Assert.Contains(band, producible);
    }
}
