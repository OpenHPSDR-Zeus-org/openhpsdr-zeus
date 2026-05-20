// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Hermes / HermesII Alex bit-selection divergence (issue #413).
///
/// The classic Alex filter board shipped on Hermes / ANAN-10 / ANAN-100 /
/// ANAN-100D / ANAN-200D / HermesII (ANAN-10E / ANAN-100B) uses RX HPFs
/// at low bits of the Alex0 word. The ANAN-7000 / Orion-II / Saturn BPF
/// board uses RX BPFs at the *same* low bits with *different* semantic
/// meaning per band. Picking the wrong table silently selects the wrong
/// filter (or none) — see pihpsdr alex.h comments and
/// <c>new_protocol.c:1154-1168</c>.
///
/// These tests pin the per-board branch in
/// <see cref="Protocol2Client.ComputeAlexWord"/>: Hermes/HermesII route
/// through <see cref="Protocol2Client.BpfBitsClassicAlex"/>, everything
/// else routes through <see cref="Protocol2Client.BpfBitsAnan7000"/>.
/// </summary>
public class HermesAlexBitsTests
{
    // 7 MHz (40m) — clearest divergence: classic Alex selects the
    // 6.5 MHz HPF (bit 5, 0x20), ANAN-7000 selects the 40/30m BPF
    // (bit 4, 0x10). Different bit values, so the test fails loudly
    // if the branch routes the wrong way.
    [Fact]
    public void Hermes_40m_Selects_ClassicAlex_6_5MHz_HPF()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 7_100_000, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.Hermes);

        Assert.Equal(0x00000020u, alex0 & 0xFFFFu);
    }

    [Fact]
    public void HermesII_40m_Selects_ClassicAlex_6_5MHz_HPF()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 7_100_000, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.HermesII);

        Assert.Equal(0x00000020u, alex0 & 0xFFFFu);
    }

    [Fact]
    public void OrionMkII_40m_Selects_Anan7000_40_30_BPF()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 7_100_000, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII);

        Assert.Equal(0x00000010u, alex0 & 0xFFFFu);
    }

    // 3.5 MHz (80m) — another divergence:
    // classic = ALEX_1_5MHZ_HPF (bit 6, 0x40); ANAN-7000 = ALEX_ANAN7000_RX_80_60_BPF (bit 5, 0x20).
    [Fact]
    public void Hermes_80m_Selects_ClassicAlex_1_5MHz_HPF()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 3_500_000, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.Hermes);

        Assert.Equal(0x00000040u, alex0 & 0xFFFFu);
    }

    [Fact]
    public void OrionMkII_80m_Selects_Anan7000_80_60_BPF()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 3_500_000, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII);

        Assert.Equal(0x00000020u, alex0 & 0xFFFFu);
    }

    // < 1.8 MHz on classic Alex selects ALEX_BYPASS_HPF (bit 12) — the
    // classic board has no 160m HPF (only a bypass at low freq). The
    // ANAN-7000 BPF board has a dedicated 160m BPF at bit 6 plus bypass
    // at bit 12 below 1.5 MHz. Test 1.0 MHz to assert both pick bit 12
    // (same bit value, same meaning — sanity check that not all bands
    // diverge).
    [Fact]
    public void Hermes_Below_1_8MHz_Selects_BypassHpf()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 1_000_000, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.Hermes);

        Assert.Equal(0x00001000u, alex0 & 0xFFFFu);
    }

    // 50 MHz (6m) — both layouts pick the bit-3 filter (classic =
    // ALEX_6M_PREAMP; ANAN-7000 = ALEX_ANAN7000_RX_6_PRE_BPF). Same
    // bit value, similar effective meaning. Sanity check.
    [Fact]
    public void Hermes_6m_Selects_6M_Preamp()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 50_100_000, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.Hermes);

        Assert.Equal(0x00000008u, alex0 & 0xFFFFu);
    }

    // LPF bits are shared across both filter boards. Same TX freq,
    // any board, same LPF. Asserted via the public ComputeAlexWord
    // entry on its own.
    [Fact]
    public void LpfBits_Are_Shared_Across_Boards()
    {
        uint a = Protocol2Client.ComputeAlexWord(
            rxFreqHz: 14_100_000, txFreqHz: 14_100_000, txAnt: 1,
            board: HpsdrBoardKind.Hermes);
        uint b = Protocol2Client.ComputeAlexWord(
            rxFreqHz: 14_100_000, txFreqHz: 14_100_000, txAnt: 1,
            board: HpsdrBoardKind.OrionMkII);

        // Both will pick the 30/20m LPF (bit 20 = 0x00100000). The RX
        // filter (low bits) is allowed to differ; mask it out and assert
        // the LPF + TX_ANT bits match.
        const uint highMask = 0xFFFF0000u;
        Assert.Equal(a & highMask, b & highMask);
    }
}
