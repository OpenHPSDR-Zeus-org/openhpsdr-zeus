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

using Zeus.Dsp;
using Xunit;

namespace Zeus.Dsp.Tests;

public class SyntheticDspEngineTests
{
    [Fact]
    public void OpenChannel_ReturnsIds_AndPixoutsHaveExpectedShape()
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        Assert.True(id > 0);

        var pan = new float[256];
        Assert.True(eng.TryGetDisplayPixels(id, DisplayPixout.Panadapter, pan));
        Assert.True(pan.Length == 256);
        Assert.Contains(pan, v => v > -60f);
    }

    [Fact]
    public void Panadapter_PeakColumnAdvancesOverTime()
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);

        var pan = new float[256];
        eng.TryGetDisplayPixels(id, DisplayPixout.Panadapter, pan);
        int first = ArgMax(pan);

        Thread.Sleep(120);

        eng.TryGetDisplayPixels(id, DisplayPixout.Panadapter, pan);
        int second = ArgMax(pan);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryGetDisplayPixels_ReturnsFalseForUnknownChannel()
    {
        using var eng = new SyntheticDspEngine();
        Assert.False(eng.TryGetDisplayPixels(42, DisplayPixout.Panadapter, new float[256]));
    }

    [Fact]
    public void OpenTxChannel_ReturnsNegativeOne_AndSetMoxIsNoOp()
    {
        using var eng = new SyntheticDspEngine();
        Assert.Equal(-1, eng.OpenTxChannel());
        eng.SetMox(true);
        eng.SetMox(false);
    }

    private static int ArgMax(ReadOnlySpan<float> s)
    {
        int best = 0;
        float bestVal = float.NegativeInfinity;
        for (int i = 0; i < s.Length; i++) if (s[i] > bestVal) { bestVal = s[i]; best = i; }
        return best;
    }
}
