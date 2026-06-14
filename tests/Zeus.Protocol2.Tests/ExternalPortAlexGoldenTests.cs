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
/// GOLDEN-BYTE CHARACTERIZATION tests for the Protocol-2 Alex words on the
/// Saturn / 0x0A (OrionMkII) path (external-ports plan, Phase 1). These lock
/// the *current* 32-bit alex0 (HighPriority offset 1432) and alex1 (offset
/// 1428) values for the antenna + TX-relay + PureSignal bits across idle/xmit
/// and PS-armed/not.
///
/// This is the safety net for the IExternalPortEncoder refactor: moving the
/// TX/RX antenna bits behind the encoder must reproduce these words EXACTLY.
/// A single differing bit means the refactor changed behaviour — and the
/// PureSignal bits (AlexPsBit 0x00040000, AlexRxAntennaBypass 0x00000800) and
/// TX_RELAY (0x08000000) are pinned here precisely so a refactor cannot
/// disturb them.
///
/// Canonical fixture: 14.1 MHz on the OrionMkII (Saturn BPF) board.
///   BPF (20/15m bucket)      = 0x00000002
///   LPF (30/20m bucket)      = 0x00100000
///   TX antenna 1 (hardcoded) = 0x01000000
///   => alexCommon            = 0x01100002
/// </summary>
public class ExternalPortAlexGoldenTests
{
    private const uint Rx = 14_100_000u;

    // The composed base word (no TX-relay, no PS) for the canonical fixture.
    private const uint AlexCommon = 0x01100002u;

    // Live-path OR constants (must match Protocol2Client private consts).
    private const uint TxRelay = 0x08000000u;
    private const uint Gnd0nTx = 0x00000100u; // alex1-only RX-ground while keyed
    private const uint PsBit = 0x00040000u;
    private const uint Bypass = 0x00000800u;

    // ---- alex0 (offset 1432) ----

    [Fact]
    public void Alex0_Idle_NoPs_IsAlexCommon()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon, alex0);
    }

    [Fact]
    public void Alex0_Xmit_NoPs_AddsTxRelay()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon | TxRelay, alex0);
    }

    [Fact]
    public void Alex0_Xmit_PsInternal_AddsTxRelayAndPsBit()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: true, psExternal: false,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon | TxRelay | PsBit, alex0);
    }

    [Fact]
    public void Alex0_Xmit_PsExternal_AddsBypassBit()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: true, psExternal: true,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon | TxRelay | PsBit | Bypass, alex0);
    }

    [Fact]
    public void Alex0_Idle_PsArmed_DoesNotAddTxRelayOrPsOrBypass()
    {
        // PS armed but not keyed: alex0 stays at the base word (PS rides alex1
        // always, alex0 only during xmit).
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: true, psExternal: true,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon, alex0);
    }

    // ---- TX-antenna select alex0[26:24] (external-ports plan, Phase 2) ----

    // The fixture's BPF+LPF bits without ANY TX-antenna bit (AlexCommon minus
    // ALEX_TX_ANTENNA_1). Antenna bits are added on top per selection.
    private const uint AlexNoAnt = 0x00100002u;
    private const uint TxAnt1 = 0x01000000u;
    private const uint TxAnt2 = 0x02000000u;
    private const uint TxAnt3 = 0x04000000u;

    // DIFFERENTIAL: on a relay-capable board (G2 / OrionMkII) the selected TX
    // antenna flips alex0[26:24], and nothing else moves. PS / TX-relay / BPF /
    // LPF bits are untouched.
    [Theory]
    [InlineData(1, TxAnt1)]
    [InlineData(2, TxAnt2)]
    [InlineData(3, TxAnt3)]
    public void Alex0_Idle_TxAntenna_FlipsBits_OnRelayBoard(int txAntWire, uint expectedAntBits)
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            txAntWire: txAntWire, hasTxAntennaRelays: true);
        Assert.Equal(AlexNoAnt | expectedAntBits, alex0);
    }

    // During xmit the TX-relay rides alongside the selected antenna — confirm
    // the two are disjoint and both present.
    [Fact]
    public void Alex0_Xmit_TxAntenna2_AddsAntAndTxRelay()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            txAntWire: 2, hasTxAntennaRelays: true);
        Assert.Equal(AlexNoAnt | TxAnt2 | TxRelay, alex0);
    }

    // PS armed + ANT3 mid-xmit: the PS bit and the antenna bits coexist; PS is
    // never disturbed by the antenna selection.
    [Fact]
    public void Alex0_Xmit_TxAntenna3_PsInternal_KeepsPsBit()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: true, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            txAntWire: 3, hasTxAntennaRelays: true);
        Assert.Equal(AlexNoAnt | TxAnt3 | TxRelay | PsBit, alex0);
    }

    // GATE: a board/variant WITHOUT TX-antenna relays must NOT advertise ANT2/3
    // even when ANT2/3 is requested — it stays on ANT1. This is the single-relay
    // / non-relay safety, and it keeps the ANT1 default byte-identical.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Alex0_Idle_NonRelayBoard_StaysOnAnt1(int txAntWire)
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            txAntWire: txAntWire, hasTxAntennaRelays: false);
        Assert.Equal(AlexCommon, alex0); // AlexCommon already carries ANT1
    }

    // ---- alex1 (offset 1428) ----

    [Fact]
    public void Alex1_Idle_NoPs_IsAlexCommon()
    {
        uint alex1 = Protocol2Client.ComposeAlex1ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon, alex1);
    }

    [Fact]
    public void Alex1_Xmit_NoPs_AddsTxRelayAndGndOnTx()
    {
        uint alex1 = Protocol2Client.ComposeAlex1ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: false,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon | TxRelay | Gnd0nTx, alex1);
    }

    [Fact]
    public void Alex1_Idle_PsArmed_AddsPsBitAlways()
    {
        // PS rides alex1 whenever armed, regardless of MOX.
        uint alex1 = Protocol2Client.ComposeAlex1ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: true,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon | PsBit, alex1);
    }

    [Fact]
    public void Alex1_Xmit_PsArmed_AddsTxRelayGndAndPs()
    {
        uint alex1 = Protocol2Client.ComposeAlex1ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: true,
            board: HpsdrBoardKind.OrionMkII);
        Assert.Equal(AlexCommon | TxRelay | Gnd0nTx | PsBit, alex1);
    }
}
