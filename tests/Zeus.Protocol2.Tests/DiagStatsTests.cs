// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA), and contributors.

using Zeus.Protocol2;

namespace Zeus.Protocol2.Tests;

// Issue #1148 — the ~1 Hz RX-IQ / tick / speaker diag log lines all compute a
// p99 over a fixed ring via DiagStats. Lock down the nearest-rank math so the
// numbers an operator (or a bench run) reads off those logs are trustworthy.
public sealed class DiagStatsTests
{
    [Fact]
    public void Percentile_EmptyCount_ReturnsZero()
    {
        var values = new long[] { 5, 9, 100 };
        var scratch = new long[3];
        Assert.Equal(0, DiagStats.Percentile(values, scratch, 0, 0.99));
    }

    [Fact]
    public void Percentile_LeavesSourceUntouched_SortsOnlyScratch()
    {
        var values = new long[] { 30, 10, 20, 40 };
        var snapshot = (long[])values.Clone();
        var scratch = new long[4];

        DiagStats.Percentile(values, scratch, 4, 0.5);

        Assert.Equal(snapshot, values); // source ring must not be reordered
    }

    [Theory]
    // 100 ascending samples 1..100. Nearest-rank index = ceil(q*n)-1.
    [InlineData(0.99, 99)] // ceil(99)-1 = 98 -> value 99
    [InlineData(0.50, 50)] // ceil(50)-1 = 49 -> value 50
    [InlineData(1.00, 100)] // top
    [InlineData(0.00, 1)] // clamps to index 0 -> value 1
    public void Percentile_NearestRank_OnAscendingSamples(double q, long expected)
    {
        var values = new long[100];
        for (int i = 0; i < 100; i++) values[i] = i + 1;
        var scratch = new long[100];

        // Pre-scramble to prove the helper sorts internally.
        Array.Reverse(values);

        Assert.Equal(expected, DiagStats.Percentile(values, scratch, 100, q));
    }

    [Fact]
    public void Percentile_P99_IgnoresEntriesBeyondCount()
    {
        // Only the first 4 entries are valid; the trailing huge value must not
        // leak into the percentile (simulates a partly-filled ring).
        var values = new long[] { 1, 2, 3, 4, long.MaxValue };
        var scratch = new long[5];
        Assert.Equal(4, DiagStats.Percentile(values, scratch, 4, 0.99));
    }

    [Theory]
    [InlineData(1, 0.99, 0)]
    [InlineData(10, 0.99, 9)]
    [InlineData(50, 0.99, 49)]
    [InlineData(512, 0.99, 506)] // ceil(0.99*512)-1 = ceil(506.88)-1 = 506
    public void PercentileIndex_ClampedNearestRank(int count, double q, int expected)
    {
        Assert.Equal(expected, DiagStats.PercentileIndex(count, q));
    }

    [Fact]
    public void PercentileIndex_NonPositiveCount_ReturnsZero()
    {
        Assert.Equal(0, DiagStats.PercentileIndex(0, 0.99));
        Assert.Equal(0, DiagStats.PercentileIndex(-5, 0.99));
    }
}
