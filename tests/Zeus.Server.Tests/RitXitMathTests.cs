// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Simone Fabris (IU3QEZ), and contributors.

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class RitXitMathTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2000, 2000)]
    [InlineData(-2000, -2000)]
    [InlineData(3000, 3000)]
    [InlineData(-3000, -3000)]
    [InlineData(3001, 3000)]
    [InlineData(-3001, -3000)]
    [InlineData(50000, 3000)]
    [InlineData(-50000, -3000)]
    public void ClampOffset_clamps_to_max(int input, int expected)
    {
        Assert.Equal(expected, RitXitMath.ClampOffset(input));
    }

    [Theory]
    [InlineData(2800, 10)]
    [InlineData(500, 10)]
    [InlineData(251, 10)]
    [InlineData(250, 5)]
    [InlineData(200, 5)]
    [InlineData(100, 5)]
    [InlineData(50, 5)]
    public void FilterAwareStepHz_returns_5_for_narrow_filters(int bwHz, int expected)
    {
        Assert.Equal(expected, RitXitMath.FilterAwareStepHz(bwHz));
    }

    [Fact]
    public void WireFreqs_Rit_shifts_rx_leaves_tx()
    {
        var (rx, tx) = RitXitMath.WireFreqs(
            RxMode.CWU, 14_050_000,
            IncrementalTuningMode.Rit, 250, 0);

        Assert.Equal(CwOffset.EffectiveLoHz(RxMode.CWU, 14_050_250), rx);
        Assert.Equal(CwOffset.EffectiveLoHz(RxMode.CWU, 14_050_000), tx);
    }

    [Fact]
    public void WireFreqs_Xit_shifts_tx_leaves_rx()
    {
        var (rx, tx) = RitXitMath.WireFreqs(
            RxMode.CWU, 14_050_000,
            IncrementalTuningMode.Xit, 0, -300);

        Assert.Equal(CwOffset.EffectiveLoHz(RxMode.CWU, 14_050_000), rx);
        Assert.Equal(CwOffset.EffectiveLoHz(RxMode.CWU, 14_049_700), tx);
    }

    [Fact]
    public void WireFreqs_Off_both_equal()
    {
        var (rx, tx) = RitXitMath.WireFreqs(
            RxMode.USB, 7_050_000,
            IncrementalTuningMode.Off, 500, -200);

        long expected = CwOffset.EffectiveLoHz(RxMode.USB, 7_050_000);
        Assert.Equal(expected, rx);
        Assert.Equal(expected, tx);
    }

    [Fact]
    public void WireFreqs_non_CW_mode_passes_through()
    {
        var (rx, tx) = RitXitMath.WireFreqs(
            RxMode.USB, 14_200_000,
            IncrementalTuningMode.Rit, 1000, 0);

        Assert.Equal(14_201_000, rx);
        Assert.Equal(14_200_000, tx);
    }
}
