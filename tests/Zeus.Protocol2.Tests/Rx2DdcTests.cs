// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// True second-receiver (RX2) DDC wiring on Protocol 2. RX2 streams its own
/// independent DDC (RxBaseDdc + 1) sourced from the RX2 ADC on dual-ADC boards
/// so it can sit on a different band/input than RX1. These pin the CmdRx
/// enable-mask, per-DDC config-block offsets, and ADC source so the wire shape
/// cannot regress to RX1 mirroring without a test failure.
/// </summary>
public class Rx2DdcTests
{
    [Fact]
    public void Rx2Ddc_IsOneAboveBaseRxDdc()
    {
        // Orion-family RX1 = DDC2 → RX2 = DDC3; Hermes-class RX1 = DDC0 → RX2 = DDC1.
        Assert.Equal(3, Protocol2Client.Rx2Ddc(HpsdrBoardKind.OrionMkII));
        Assert.Equal(1, Protocol2Client.Rx2Ddc(HpsdrBoardKind.Hermes));
        Assert.Equal(1, Protocol2Client.Rx2Ddc(HpsdrBoardKind.HermesII));
    }

    [Fact]
    public void ReceiverIndexForRxStream_Rx2Disabled_LeavesAllStreamsOnRx1()
    {
        Assert.Equal(0, Protocol2Client.ReceiverIndexForRxStream(1, HpsdrBoardKind.OrionMkII, rx2Enabled: false));
        Assert.Equal(0, Protocol2Client.ReceiverIndexForRxStream(3, HpsdrBoardKind.OrionMkII, rx2Enabled: false));
    }

    [Fact]
    public void ReceiverIndexForRxStream_Orion_AcceptsLogicalSecondStreamAndDdcSlot()
    {
        Assert.Equal(0, Protocol2Client.ReceiverIndexForRxStream(0, HpsdrBoardKind.OrionMkII, rx2Enabled: true));
        Assert.Equal(0, Protocol2Client.ReceiverIndexForRxStream(2, HpsdrBoardKind.OrionMkII, rx2Enabled: true));
        Assert.Equal(1, Protocol2Client.ReceiverIndexForRxStream(1, HpsdrBoardKind.OrionMkII, rx2Enabled: true));
        Assert.Equal(1, Protocol2Client.ReceiverIndexForRxStream(3, HpsdrBoardKind.OrionMkII, rx2Enabled: true));
    }

    [Fact]
    public void ReceiverIndexForRxStream_Hermes_Rx2IsSecondStream()
    {
        Assert.Equal(0, Protocol2Client.ReceiverIndexForRxStream(0, HpsdrBoardKind.Hermes, rx2Enabled: true));
        Assert.Equal(1, Protocol2Client.ReceiverIndexForRxStream(1, HpsdrBoardKind.Hermes, rx2Enabled: true));
    }

    [Fact]
    public void Rx2AdcSource_AllBoards_ShareRx1Adc0()
    {
        // RX2 shares RX1's ADC0 (main antenna). Sourcing ADC1 on dual-ADC
        // boards fed the empty RX2/EXT jack — a silent DDC and a flat
        // panadapter on a normal single-antenna station (verified on a live G2).
        Assert.Equal((byte)0x00, Protocol2Client.Rx2AdcSource(2, HpsdrBoardKind.OrionMkII));
        Assert.Equal((byte)0x00, Protocol2Client.Rx2AdcSource(2, HpsdrBoardKind.Angelia));
        Assert.Equal((byte)0x00, Protocol2Client.Rx2AdcSource(2, HpsdrBoardKind.Unknown));
    }

    [Fact]
    public void Rx2AdcSource_SingleAdcBoards_StayOnAdc0()
    {
        Assert.Equal((byte)0x00, Protocol2Client.Rx2AdcSource(1, HpsdrBoardKind.OrionMkII));
        Assert.Equal((byte)0x00, Protocol2Client.Rx2AdcSource(2, HpsdrBoardKind.Hermes));
        Assert.Equal((byte)0x00, Protocol2Client.Rx2AdcSource(2, HpsdrBoardKind.HermesII));
        Assert.Equal((byte)0x00, Protocol2Client.Rx2AdcSource(2, HpsdrBoardKind.HermesC10));
    }

    [Fact]
    public void CmdRx_Orion_Rx2Disabled_LeavesDdc3Clear()
    {
        // Regression guard: default (RX2 off) must keep shipping exactly the
        // DDC2-only wire shape.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.OrionMkII);

        Assert.Equal((byte)0x04, p[7]);   // only DDC2 (RX1)
        Assert.Equal((byte)0x00, p[35]);  // DDC3 config block untouched
        Assert.Equal((byte)0x00, p[40]);
    }

    [Fact]
    public void CmdRx_Orion_Rx2Enabled_EnablesDdc3_AndConfig()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.OrionMkII,
            adcDitherEnabled: false, adcRandomEnabled: false, rx2Enabled: true);

        // DDC2 (RX1) + DDC3 (RX2) both enabled.
        Assert.Equal((byte)(0x04 | 0x08), p[7]);
        // RX2 DDC3 config block at 17 + 3*6 = 35: ADC0 (shares RX1's main
        // antenna), sample-rate BE, 24-bit.
        Assert.Equal((byte)0x00, p[35]);  // ADC0 / shared with RX1
        Assert.Equal((byte)0x00, p[36]);  // 48 kHz BE high
        Assert.Equal((byte)48,   p[37]);  // 48 kHz BE low
        Assert.Equal((byte)24,   p[40]);  // 24-bit
    }

    [Fact]
    public void CmdRx_Hermes_Rx2Enabled_EnablesDdc1_AndConfig()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.Hermes,
            adcDitherEnabled: false, adcRandomEnabled: false, rx2Enabled: true);

        // DDC0 (RX1) + DDC1 (RX2).
        Assert.Equal((byte)(0x01 | 0x02), p[7]);
        // RX2 DDC1 config block at 17 + 1*6 = 23.
        Assert.Equal((byte)0x00, p[23]);  // ADC0
        Assert.Equal((byte)0x00, p[24]);  // 48 kHz BE high
        Assert.Equal((byte)48,   p[25]);  // 48 kHz BE low
        Assert.Equal((byte)24,   p[28]);  // 24-bit
    }

    [Fact]
    public void Alex1FilterWord_Rx2Enabled_FollowsVfoB()
    {
        uint alex1 = Protocol2Client.ComposeAlex1Word(
            rxFreqHz: 14_200_000,
            rx2FreqHz: 7_200_000,
            txLpfFreqHz: 14_200_000,
            rx2Enabled: true,
            moxOn: false,
            psEnabled: false,
            board: HpsdrBoardKind.OrionMkII);

        Assert.Equal(
            Protocol2Client.ComputeAlexWord(7_200_000, 7_200_000, txAnt: 1, board: HpsdrBoardKind.OrionMkII),
            alex1);
    }

    [Fact]
    public void Alex1FilterWord_Rx2Disabled_FollowsVfoA()
    {
        uint alex1 = Protocol2Client.ComposeAlex1Word(
            rxFreqHz: 14_200_000,
            rx2FreqHz: 7_200_000,
            txLpfFreqHz: 14_200_000,
            rx2Enabled: false,
            moxOn: false,
            psEnabled: false,
            board: HpsdrBoardKind.OrionMkII);

        Assert.Equal(
            Protocol2Client.ComputeAlexWord(14_200_000, 14_200_000, txAnt: 1, board: HpsdrBoardKind.OrionMkII),
            alex1);
    }

    [Fact]
    public void Alex1FilterWord_Rx2DifferentBand_DuringTx_LpfFollowsTxVfo_NotRx2()
    {
        // Dual-RX TUNE/MOX no-carrier regression (live-confirmed on G2): with RX2
        // parked on a different band than TX, the alex1 word's TX LOW-PASS filter
        // (LPF) must follow the TX VFO, not RX2. The LPF is a TX-path filter
        // carried on both alex words during TX (pihpsdr new_protocol.c); if it
        // selects RX2's band the alex1 filter board rejects the TX carrier and no
        // RF is emitted. The RX2 receive preselector (BPF) still follows RX2.
        const uint txHz = 7_200_000;    // TX on 40 m
        const uint rx2Hz = 14_200_000;  // RX2 on 20 m (different band)
        const uint txRelay = 0x08000000u;            // ALEX_TX_RELAY
        const uint gndOnTx = 0x00000100u;            // ALEX1_ANAN7000_RX_GNDonTX

        uint alex1 = Protocol2Client.ComposeAlex1Word(
            rxFreqHz: rx2Hz,    // RX1 receive band (irrelevant here; RX2 is on)
            rx2FreqHz: rx2Hz,   // RX2 receive preselector → BPF
            txLpfFreqHz: txHz,  // TX freq (independent DUC) → TX low-pass
            rx2Enabled: true,
            moxOn: true,        // transmitting (MOX or TUNE)
            psEnabled: false,
            board: HpsdrBoardKind.OrionMkII);

        // BPF follows RX2 (20 m), LPF follows TX (40 m), plus the TX relay and
        // the keyed RX-ground bit.
        uint expected = Protocol2Client.ComputeAlexWord(rx2Hz, txHz, txAnt: 1, board: HpsdrBoardKind.OrionMkII)
            | txRelay | gndOnTx;
        Assert.Equal(expected, alex1);

        // Regression guard: must NOT be the old buggy word where the LPF also
        // followed RX2's band — that word rejected the TX carrier.
        uint buggy = Protocol2Client.ComputeAlexWord(rx2Hz, rx2Hz, txAnt: 1, board: HpsdrBoardKind.OrionMkII)
            | txRelay | gndOnTx;
        Assert.NotEqual(buggy, alex1);

        // And the LPF component specifically must be the TX-band LPF.
        Assert.Equal(Protocol2Client.LpfBits(txHz), alex1 & Protocol2Client.LpfBits(txHz));
        Assert.NotEqual(Protocol2Client.LpfBits(rx2Hz), Protocol2Client.LpfBits(txHz));
    }

    // ---- split TX: independent TX DUC (dual-RX two-carrier fix) ----

    [Fact]
    public void TxDuc_DefaultFollowsRx0_NonSplit_ByteIdentical()
    {
        // Non-split: the TX DUC tracks RX0 (VFO A) exactly, so the wire is
        // byte-identical to the historic single-frequency model.
        using var p2 = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
        p2.SetVfoAHz(14_200_000);
        Assert.False(p2.TxDucIndependentForTesting);
        Assert.Equal(p2.CorrectedRxFreqHzForTesting, p2.TxDucFreqHzForTesting);
    }

    [Fact]
    public void TxDuc_Independent_SplitTx_OnVfoB_WhileRx0StaysVfoA()
    {
        // Split TX: the TX DUC is driven to VFO B independently, so the carrier
        // lands on B, while RX0 (the shared LO that feeds RX1's DDC) stays on
        // VFO A — RX1 is no longer dragged to B (the two-carrier bug).
        using var p2 = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
        p2.SetVfoAHz(14_200_000);          // RX0 / RX1 on 20 m
        p2.SetTxDucFrequency(7_200_000);   // TX DUC on 40 m (VFO B)

        Assert.True(p2.TxDucIndependentForTesting);
        Assert.Equal(7_200_000u, p2.TxDucFreqHzForTesting);
        Assert.Equal(14_200_000u, p2.CorrectedRxFreqHzForTesting);   // RX0 not dragged

        // While the override is latched, an RX0 retune must NOT clobber the TX DUC.
        p2.SetVfoAHz(14_250_000);
        Assert.Equal(7_200_000u, p2.TxDucFreqHzForTesting);
        Assert.Equal(14_250_000u, p2.CorrectedRxFreqHzForTesting);

        // Clearing (split ended) returns the DUC to following RX0.
        p2.SetTxDucFrequency(0);
        Assert.False(p2.TxDucIndependentForTesting);
        Assert.Equal(p2.CorrectedRxFreqHzForTesting, p2.TxDucFreqHzForTesting);
    }
}
