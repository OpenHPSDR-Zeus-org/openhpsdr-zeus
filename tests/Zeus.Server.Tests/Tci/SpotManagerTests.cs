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

using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

public class SpotManagerTests
{
    [Fact]
    public void AddSpot_StoresSpot()
    {
        var manager = new SpotManager();
        manager.AddSpot("W1AW", "CW", 14074000, 0xFF00FF00, "CQ DX");

        var spots = manager.GetAll();
        Assert.Single(spots);
        Assert.Equal("W1AW", spots[0].Callsign);
        Assert.Equal("CW", spots[0].Mode);
        Assert.Equal(14074000, spots[0].FreqHz);
        Assert.Equal(0xFF00FF00u, spots[0].Argb);
        Assert.Equal("CQ DX", spots[0].Comment);
    }

    [Fact]
    public void AddSpot_DuplicateCallsign_Overwrites()
    {
        var manager = new SpotManager();
        manager.AddSpot("W1AW", "CW", 14074000, 0xFF00FF00);
        manager.AddSpot("W1AW", "SSB", 14250000, 0xFFFF0000, "Updated");

        var spots = manager.GetAll();
        Assert.Single(spots);
        Assert.Equal("SSB", spots[0].Mode);
        Assert.Equal(14250000, spots[0].FreqHz);
        Assert.Equal("Updated", spots[0].Comment);
    }

    [Fact]
    public void AddSpot_MultipleSpots_StoresAll()
    {
        var manager = new SpotManager();
        manager.AddSpot("W1AW", "CW", 14074000, 0xFF00FF00);
        manager.AddSpot("K3Y", "SSB", 14250000, 0xFFFF0000);
        manager.AddSpot("DL1ABC", "FT8", 14074000, 0xFF0000FF);

        var spots = manager.GetAll();
        Assert.Equal(3, spots.Length);
        Assert.Contains(spots, s => s.Callsign == "W1AW");
        Assert.Contains(spots, s => s.Callsign == "K3Y");
        Assert.Contains(spots, s => s.Callsign == "DL1ABC");
    }

    [Fact]
    public void RemoveSpot_ExistingCallsign_Removes()
    {
        var manager = new SpotManager();
        manager.AddSpot("W1AW", "CW", 14074000, 0xFF00FF00);
        manager.AddSpot("K3Y", "SSB", 14250000, 0xFFFF0000);

        manager.RemoveSpot("W1AW");

        var spots = manager.GetAll();
        Assert.Single(spots);
        Assert.Equal("K3Y", spots[0].Callsign);
    }

    [Fact]
    public void RemoveSpot_NonexistentCallsign_NoOp()
    {
        var manager = new SpotManager();
        manager.AddSpot("W1AW", "CW", 14074000, 0xFF00FF00);

        manager.RemoveSpot("NONEXISTENT");

        var spots = manager.GetAll();
        Assert.Single(spots);
    }

    [Fact]
    public void ClearAll_RemovesAllSpots()
    {
        var manager = new SpotManager();
        manager.AddSpot("W1AW", "CW", 14074000, 0xFF00FF00);
        manager.AddSpot("K3Y", "SSB", 14250000, 0xFFFF0000);
        manager.AddSpot("DL1ABC", "FT8", 14074000, 0xFF0000FF);

        manager.ClearAll();

        var spots = manager.GetAll();
        Assert.Empty(spots);
    }

    [Fact]
    public void GetAll_EmptyManager_ReturnsEmptyArray()
    {
        var manager = new SpotManager();
        var spots = manager.GetAll();
        Assert.Empty(spots);
    }

    [Fact]
    public void AddSpot_NullComment_Allowed()
    {
        var manager = new SpotManager();
        manager.AddSpot("W1AW", "CW", 14074000, 0xFF00FF00, null);

        var spots = manager.GetAll();
        Assert.Single(spots);
        Assert.Null(spots[0].Comment);
    }
}
