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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

// Issue #1028 — WDSP's bandpass stages silently pass nothing for a zero-width
// passband, taking the receiver dead with no error path. WdspDspEngine floors
// the FINAL signed passband width right before the native calls, so no
// degenerate width reaches WDSP no matter the mode/centre/caller (including a
// centre-zero filter that an upstream symmetric clamp left at low == high after
// abs-folding). These lock that boundary guard without needing a live WDSP
// channel, so they run on every CI platform.
public sealed class PassbandWidthFloorTests
{
    private static double Floor = WdspDspEngine.MinPassbandWidthHz;

    [Fact]
    public void NormalSsbWidth_PassesThroughUnchanged()
    {
        var (lo, hi) = WdspDspEngine.FloorPassbandWidth(100, 2800);
        Assert.Equal(100, lo);
        Assert.Equal(2800, hi);
    }

    [Fact]
    public void NarrowCwWidth_PassesThroughUnchanged()
    {
        // CW F10 = 25 Hz (587..613) — narrowest shipped preset, well above the
        // floor, so it must be byte-identical.
        var (lo, hi) = WdspDspEngine.FloorPassbandWidth(587, 613);
        Assert.Equal(587, lo);
        Assert.Equal(613, hi);
    }

    [Fact]
    public void SymmetricAmWidth_PassesThroughUnchanged()
    {
        var (lo, hi) = WdspDspEngine.FloorPassbandWidth(-2500, 2500);
        Assert.Equal(-2500, lo);
        Assert.Equal(2500, hi);
    }

    [Fact]
    public void WidthExactlyAtFloor_PassesThroughUnchanged()
    {
        var (lo, hi) = WdspDspEngine.FloorPassbandWidth(0, Floor);
        Assert.Equal(0, lo);
        Assert.Equal(Floor, hi);
    }

    [Fact]
    public void ZeroWidthUsb_ExpandsSymmetricallyToFloor()
    {
        // Centre-zero USB collapse: ApplyBandpassForMode produced low == high == 5.
        var (lo, hi) = WdspDspEngine.FloorPassbandWidth(5, 5);
        Assert.Equal(Floor, hi - lo);
        Assert.Equal(5, (lo + hi) / 2.0);
    }

    [Fact]
    public void ZeroWidthLsb_ExpandsSymmetricallyAndStaysNegative()
    {
        // LSB collapse: low == high == -5. Must stay on the negative side.
        var (lo, hi) = WdspDspEngine.FloorPassbandWidth(-5, -5);
        Assert.Equal(Floor, hi - lo);
        Assert.Equal(-5, (lo + hi) / 2.0);
    }

    [Fact]
    public void ZeroWidthAtDc_ExpandsAcrossZero()
    {
        var (lo, hi) = WdspDspEngine.FloorPassbandWidth(0, 0);
        Assert.Equal(Floor, hi - lo);
        Assert.Equal(0, (lo + hi) / 2.0);
    }

    [Fact]
    public void SubFloorWidth_ExpandsAboutExistingCentre()
    {
        // 3 Hz wide about a 1500 Hz centre -> floored width, same centre.
        var (lo, hi) = WdspDspEngine.FloorPassbandWidth(1499, 1502);
        Assert.Equal(Floor, hi - lo);
        Assert.Equal(1500.5, (lo + hi) / 2.0);
    }
}
