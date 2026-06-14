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

    // DIFFERENTIAL: alex0 ANT1/2/3 [26:24] is STATE-MULTIPLEXED (pihpsdr
    // new_protocol.c:1331-1357, bench-confirmed on G2). DURING XMIT alex0 carries
    // the TX antenna (+ TX_RELAY); nothing else moves.
    [Theory]
    [InlineData(1, TxAnt1)]
    [InlineData(2, TxAnt2)]
    [InlineData(3, TxAnt3)]
    public void Alex0_Xmit_CarriesTxAntenna_OnRelayBoard(int txAntWire, uint expectedAntBits)
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            txAntWire: txAntWire, hasTxAntennaRelays: true);
        Assert.Equal(AlexNoAnt | expectedAntBits | TxRelay, alex0);
    }

    // AT IDLE (RX) alex0 carries the RX antenna, NOT the TX antenna.
    [Theory]
    [InlineData(1, TxAnt1)]
    [InlineData(2, TxAnt2)]
    [InlineData(3, TxAnt3)]
    public void Alex0_Idle_CarriesRxAntenna_OnRelayBoard(int rxAntWire, uint expectedAntBits)
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            txAntWire: 1, hasTxAntennaRelays: true, rxAntWire: rxAntWire);
        Assert.Equal(AlexNoAnt | expectedAntBits, alex0);
    }

    // THE BENCH BUG: at idle, changing the TX antenna must NOT move the RX path.
    // TX=ANT2 with RX=ANT1 → alex0 stays on ANT1 (RX), so RX doesn't go away.
    [Fact]
    public void Alex0_Idle_TxAntennaChange_DoesNotMoveRx()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            txAntWire: 2, hasTxAntennaRelays: true, rxAntWire: 1);
        Assert.Equal(AlexNoAnt | TxAnt1, alex0);
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

    // ============================================================
    //  Phase 5 — RX-aux inputs (EXT1/EXT2/XVTR/BYPASS) + RX_SELECT
    // ============================================================
    // Verified against pihpsdr src/alex.h:43-47, 122.
    private const uint AuxXvtr   = 0x00000100u; // bit 8
    private const uint AuxExt1   = 0x00000200u; // bit 9
    private const uint AuxExt2   = 0x00000400u; // bit 10
    private const uint AuxBypass = 0x00000800u; // bit 11  (== Bypass / K36)
    private const uint RxSelect  = 0x00004000u; // bit 14  Saturn master RX-select

    // selector ints used by ComposeRxAuxBits / ComposeAlex0ForTest:
    // 0=None, 1=EXT1, 2=EXT2, 3=XVTR, 4=BYPASS.

    [Fact]
    public void RxAux_None_IsByteIdentical()
    {
        // The default-unsent invariant: aux=None leaves alex0 exactly as the
        // pre-Phase-5 word.
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            rxAuxInput: 0, mkiiBpfRxSelect: true);
        Assert.Equal(AlexCommon, alex0);
    }

    [Theory]
    [InlineData(1, AuxExt1)]
    [InlineData(2, AuxExt2)]
    [InlineData(3, AuxXvtr)]
    [InlineData(4, AuxBypass)]
    public void RxAux_ClassicAlex_SetsJustTheJacketBit(int aux, uint expected)
    {
        // Classic-Alex board (no MkII master RX-select): the aux jacket bit
        // alone, RX_SELECT clear.
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            rxAuxInput: aux, mkiiBpfRxSelect: false);
        Assert.Equal(AlexCommon | expected, alex0);
        Assert.Equal(0u, alex0 & RxSelect);
    }

    [Theory]
    [InlineData(1, AuxExt1)]
    [InlineData(2, AuxExt2)]
    [InlineData(3, AuxXvtr)]
    [InlineData(4, AuxBypass)]
    public void RxAux_SaturnBpf_AlsoSetsRxSelectBit14(int aux, uint expected)
    {
        // MkII / Saturn BPF board: the aux jacket bit AND the master RX-select
        // (bit 14) so the jack is actually gated onto the RX (alex.h:122).
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: false, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            rxAuxInput: aux, mkiiBpfRxSelect: true);
        Assert.Equal(AlexCommon | expected | RxSelect, alex0);
    }

    // ============================================================
    //  Phase 5 — PS-K36 ARBITRATION (the load-bearing firewall, §3.4(2))
    // ============================================================

    // THE GOLDEN TEST. Operator selects aux=BYPASS AND PureSignal is armed
    // (external feedback) during xmit: the wire MUST still carry the PS coupler
    // routing. Because both the operator BYPASS and PS-external use bit 11, and
    // PS routing is OR'd AFTER the operator aux, the PS coupler bit is present
    // regardless — the operator pick can NEVER strip it. Encoded order:
    // operator-aux first, PS last.
    [Fact]
    public void PsK36_AuxBypassPlusPsExternal_StillEmitsPsCoupler()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: true, psExternal: true,
            board: HpsdrBoardKind.OrionMkII,
            rxAuxInput: 4 /* BYPASS */, mkiiBpfRxSelect: true);

        // PS coupler bit (K36 / bit 11) present, PS bit present, TX-relay present.
        Assert.Equal(AuxBypass, alex0 & AuxBypass); // K36 set (== PS coupler)
        Assert.Equal(PsBit, alex0 & PsBit);          // PS feedback-coupler enable
        Assert.Equal(TxRelay, alex0 & TxRelay);
        // The exact word: base + RX_SELECT (operator aux on Saturn) + K36 + PS + TX-relay.
        Assert.Equal(AlexCommon | RxSelect | AuxBypass | PsBit | TxRelay, alex0);
    }

    // Even with a CONFLICTING aux (operator picks EXT1, not BYPASS) while PS is
    // armed-external during xmit, the PS coupler (BYPASS/K36) is still asserted —
    // PS owns the relay. The operator's EXT1 bit also rides (it is a different
    // bit, bit 9) but it cannot suppress the PS coupler.
    [Fact]
    public void PsK36_AuxExt1PlusPsExternal_PsCouplerNotStolen()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: true, psExternal: true,
            board: HpsdrBoardKind.OrionMkII,
            rxAuxInput: 1 /* EXT1 */, mkiiBpfRxSelect: true);
        Assert.Equal(AuxBypass, alex0 & AuxBypass); // PS coupler still set
        Assert.Equal(PsBit, alex0 & PsBit);
    }

    // PS armed but INTERNAL coupler + operator aux=BYPASS during xmit: the K36
    // bit comes purely from the operator's aux here (PS internal doesn't set it),
    // which is the operator's prerogative when PS isn't using the external path.
    // The PS bit is still present (feedback-coupler enable). This documents that
    // the arbitration is "PS external OWNS K36"; PS internal leaves the operator
    // aux in control of bit 11.
    [Fact]
    public void PsK36_AuxBypassPlusPsInternal_OperatorBypassRides()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: true, psEnabled: true, psExternal: false,
            board: HpsdrBoardKind.OrionMkII,
            rxAuxInput: 4 /* BYPASS */, mkiiBpfRxSelect: true);
        Assert.Equal(AuxBypass, alex0 & AuxBypass);
        Assert.Equal(PsBit, alex0 & PsBit);
        Assert.Equal(AlexCommon | RxSelect | AuxBypass | PsBit | TxRelay, alex0);
    }

    // Idle (not keyed), PS armed external, operator aux=BYPASS: alex0 carries the
    // operator BYPASS (RX-time aux routing) but NOT the PS coupler/PS bit (those
    // ride alex0 only during xmit). Confirms the aux path works at RX and the PS
    // xmit-only gating is intact.
    [Fact]
    public void PsK36_Idle_AuxBypass_PsExternal_NoPsBitButAuxRides()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: Rx, moxOn: false, psEnabled: true, psExternal: true,
            board: HpsdrBoardKind.OrionMkII,
            rxAuxInput: 4 /* BYPASS */, mkiiBpfRxSelect: true);
        Assert.Equal(AlexCommon | RxSelect | AuxBypass, alex0);
        Assert.Equal(0u, alex0 & PsBit);
    }
}
