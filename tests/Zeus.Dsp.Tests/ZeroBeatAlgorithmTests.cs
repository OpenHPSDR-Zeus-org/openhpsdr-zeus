// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Xunit;

namespace Zeus.Dsp.Tests;

public class ZeroBeatAlgorithmTests
{
    [Fact]
    public void FindPeakInPassband_returns_index_of_max_in_range()
    {
        // bins[10..14] = sloping peak with max at index 12
        double[] bins = new double[20];
        bins[10] = -90; bins[11] = -70; bins[12] = -50; bins[13] = -65; bins[14] = -85;
        // outside passband — should be ignored even though louder
        bins[5] = -10; bins[18] = -10;

        var (idx, db) = ZeroBeatAlgorithm.FindPeakInPassband(bins, lowBin: 10, highBin: 14);

        Assert.Equal(12, idx);
        Assert.Equal(-50, db);
    }

    [Fact]
    public void FindPeakInPassband_handles_single_bin_range()
    {
        double[] bins = { -100, -50, -100 };
        var (idx, db) = ZeroBeatAlgorithm.FindPeakInPassband(bins, lowBin: 1, highBin: 1);
        Assert.Equal(1, idx);
        Assert.Equal(-50, db);
    }

    [Fact]
    public void ParabolicInterpolate_returns_zero_for_symmetric_peak()
    {
        // Symmetric (-70, -50, -70) → vertex is exactly at peak bin
        double offset = ZeroBeatAlgorithm.ParabolicInterpolate(-70, -50, -70);
        Assert.Equal(0.0, offset, precision: 9);
    }

    [Fact]
    public void ParabolicInterpolate_returns_positive_when_right_is_higher()
    {
        // (-70, -50, -60) → true peak is between center and right → positive offset
        double offset = ZeroBeatAlgorithm.ParabolicInterpolate(-70, -50, -60);
        Assert.InRange(offset, 0.0, 0.5);
    }

    [Fact]
    public void ParabolicInterpolate_returns_negative_when_left_is_higher()
    {
        double offset = ZeroBeatAlgorithm.ParabolicInterpolate(-60, -50, -70);
        Assert.InRange(offset, -0.5, 0.0);
    }

    [Fact]
    public void ParabolicInterpolate_clamps_to_half_bin_when_neighbours_equal_peak()
    {
        // Degenerate case: denominator → 0; clamped output prevents NaN/explosion
        double offset = ZeroBeatAlgorithm.ParabolicInterpolate(-50, -50, -50);
        Assert.InRange(offset, -0.5, 0.5);
    }

    [Fact]
    public void MedianDb_returns_middle_value_of_passband()
    {
        double[] bins = { 0, 0, 0, -90, -80, -70, -60, -50, 0, 0 };
        // sorted passband [3..7] = -90, -80, -70, -60, -50 → median = -70
        double m = ZeroBeatAlgorithm.MedianDb(bins, lowBin: 3, highBin: 7);
        Assert.Equal(-70, m);
    }

    [Fact]
    public void MedianDb_handles_even_count_with_lower_middle()
    {
        double[] bins = { -100, -90, -80, -70 };  // 4 values
        double m = ZeroBeatAlgorithm.MedianDb(bins, lowBin: 0, highBin: 3);
        // Lower-middle of two centre values → -90 (we don't need statistical
        // perfection; SNR gate is robust to a couple dB either way)
        Assert.Equal(-90, m);
    }
}
