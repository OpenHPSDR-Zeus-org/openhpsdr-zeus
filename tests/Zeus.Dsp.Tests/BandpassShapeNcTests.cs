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

using Zeus.Contracts;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

// Issue #871 — the SSB "rectangularity" control maps a BandpassWindow preset to
// a WDSP FIR tap count (nc). These tests lock the mapping and the WDSP legality
// invariants (nc >= size, nc an integer multiple of size) without needing a live
// WDSP channel, so they run on every CI platform.
public sealed class BandpassShapeNcTests
{
    // Zeus opens its RXA/TXA bandpass at this block size today.
    private const int ZeusSize = 1024;

    [Fact]
    public void Mapping_AtZeusSize_IsSoft1024_Normal2048_Sharp4096()
    {
        Assert.Equal(1024, WdspDspEngine.ResolveBandpassNc(BandpassWindow.Soft, ZeusSize));
        Assert.Equal(2048, WdspDspEngine.ResolveBandpassNc(BandpassWindow.Normal, ZeusSize));
        Assert.Equal(4096, WdspDspEngine.ResolveBandpassNc(BandpassWindow.Sharp, ZeusSize));
    }

    [Fact]
    public void Normal_EqualsWdspOpenValue_SoNoFirstConnectDrift()
    {
        // The WDSP create_bandpass open value is max(2048, size). Normal MUST
        // resolve to exactly that so a default/legacy session is byte-identical
        // to pre-#871 RF.
        foreach (int size in new[] { 512, 1024, 2048 })
        {
            int openNc = Math.Max(2048, size);
            Assert.Equal(openNc, WdspDspEngine.ResolveBandpassNc(BandpassWindow.Normal, size));
        }
    }

    [Fact]
    public void Soft_IsRounder_And_Sharp_IsHarder_Than_Normal()
    {
        int soft = WdspDspEngine.ResolveBandpassNc(BandpassWindow.Soft, ZeusSize);
        int normal = WdspDspEngine.ResolveBandpassNc(BandpassWindow.Normal, ZeusSize);
        int sharp = WdspDspEngine.ResolveBandpassNc(BandpassWindow.Sharp, ZeusSize);
        Assert.True(soft < normal, "Soft must use fewer taps than Normal (wider/rounder skirt)");
        Assert.True(sharp > normal, "Sharp must use more taps than Normal (narrower/harder skirt)");
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    public void AllPresets_SatisfyWdspLegality(int size)
    {
        foreach (BandpassWindow shape in Enum.GetValues<BandpassWindow>())
        {
            int nc = WdspDspEngine.ResolveBandpassNc(shape, size);
            Assert.True(nc >= size, $"{shape}: nc {nc} must be >= size {size}");
            Assert.True(nc % size == 0, $"{shape}: nc {nc} must be an integer multiple of size {size}");
        }
    }

    [Fact]
    public void UnknownPreset_FallsBackToOpenValue()
    {
        // Defensive: an out-of-range byte (e.g. from a corrupted DB row) must
        // resolve to the safe open value, never below the legal floor.
        var bogus = (BandpassWindow)200;
        Assert.Equal(Math.Max(2048, ZeusSize), WdspDspEngine.ResolveBandpassNc(bogus, ZeusSize));
    }
}
