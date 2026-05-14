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

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

public class N2adrBandsTests
{
    // Pin masks are raw OCrx values (bits 0..6 = pins 1..7), not yet shifted
    // into C2. See docs/prd/09-n2adr-bands.md §2 and Thetis setup.cs:14655-14699.
    [Theory]
    [InlineData(1_800_000,  0x01)]   // 160m lower edge
    [InlineData(1_999_999,  0x01)]   // 160m upper edge
    [InlineData(3_500_000,  0x42)]   // 80m lower edge
    [InlineData(3_750_000,  0x42)]   // 80m centre
    [InlineData(5_300_000,  0x44)]   // 60m lower edge
    [InlineData(7_200_000,  0x44)]   // 40m common park
    [InlineData(10_100_000, 0x48)]   // 30m lower edge
    [InlineData(14_200_000, 0x48)]   // 20m common SSB
    [InlineData(18_068_000, 0x50)]   // 17m lower edge
    [InlineData(21_200_000, 0x50)]   // 15m SSB
    [InlineData(24_890_000, 0x60)]   // 12m lower edge
    [InlineData(28_500_000, 0x60)]   // 10m SSB
    [InlineData(29_700_000, 0x60)]   // 10m upper edge
    public void N2adrBands_RxOcMask_ReturnsExpectedPinMask(long vfoHz, byte expected)
    {
        Assert.Equal(expected, N2adrBands.RxOcMask(vfoHz));
    }

    [Theory]
    [InlineData(0)]                   // DC
    [InlineData(500_000)]             // MW
    [InlineData(1_799_999)]           // just below 160m
    [InlineData(29_700_001)]          // just above 10m
    [InlineData(50_000_000)]          // 6m — no N2ADR LPF
    [InlineData(144_000_000)]         // 2m
    public void N2adrBands_RxOcMask_ZeroOutsideHf(long vfoHz)
    {
        Assert.Equal(0, N2adrBands.RxOcMask(vfoHz));
    }

    [Fact]
    public void ControlFrame_ConfigC2_EmitsShiftedN2adrMask_WhenEnabled()
    {
        // Wire encoding: output_buffer[C2] |= rxband->OCrx << 1.
        // Verify the final wire byte for a 20m park.
        Span<byte> cc = stackalloc byte[5];
        var state = new ControlFrame.CcState(
            VfoAHz: 14_200_000,
            Rate: HpsdrSampleRate.Rate192k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: false,
            EnableHl2BandVolts: false,
            Board: HpsdrBoardKind.HermesLite2,
            HasN2adr: true);
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, state);

        // 20m raw mask 0x48 → C2 = 0x48 << 1 = 0x90.
        Assert.Equal(0x90, cc[2]);
    }

    [Fact]
    public void ControlFrame_ConfigC2_IsZero_WhenN2adrDisabled()
    {
        Span<byte> cc = stackalloc byte[5];
        var state = new ControlFrame.CcState(
            VfoAHz: 14_200_000,
            Rate: HpsdrSampleRate.Rate192k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: false,
            EnableHl2BandVolts: false,
            Board: HpsdrBoardKind.HermesLite2,
            HasN2adr: false);
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, state);
        Assert.Equal(0, cc[2]);
    }

    // Parallel band-name lookup used by PaSettingsStore to surface the
    // firmware auto-mask in the PA Settings panel. Must agree pin-for-pin
    // with the freq-keyed RxOcMask above — operator-visible UI mustn't
    // diverge from what the wire actually drives.
    [Theory]
    [InlineData("160m", 0x01)]
    [InlineData("80m",  0x42)]
    [InlineData("60m",  0x44)]
    [InlineData("40m",  0x44)]
    [InlineData("30m",  0x48)]
    [InlineData("20m",  0x48)]
    [InlineData("17m",  0x50)]
    [InlineData("15m",  0x50)]
    [InlineData("12m",  0x60)]
    [InlineData("10m",  0x60)]
    [InlineData("6m",   0x00)]
    [InlineData("",     0x00)]
    [InlineData("garbage", 0x00)]
    public void N2adrBands_RxOcMaskForBand_MatchesFreqKeyedTable(string band, byte expected)
    {
        Assert.Equal(expected, N2adrBands.RxOcMaskForBand(band));
    }

    [Fact]
    public void ControlFrame_ConfigC2_IsZero_WhenBoardIsNotHl2()
    {
        // N2ADR is an HL2-only filter board. Setting HasN2adr on a bare
        // Hermes must not emit any OC pin bits.
        Span<byte> cc = stackalloc byte[5];
        var state = new ControlFrame.CcState(
            VfoAHz: 14_200_000,
            Rate: HpsdrSampleRate.Rate192k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: false,
            EnableHl2BandVolts: false,
            Board: HpsdrBoardKind.Hermes,
            HasN2adr: true);
        ControlFrame.WriteCcBytes(cc, ControlFrame.CcRegister.Config, state);
        Assert.Equal(0, cc[2]);
    }
}
