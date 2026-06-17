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
            rx2Enabled: false,
            moxOn: false,
            psEnabled: false,
            board: HpsdrBoardKind.OrionMkII);

        Assert.Equal(
            Protocol2Client.ComputeAlexWord(14_200_000, 14_200_000, txAnt: 1, board: HpsdrBoardKind.OrionMkII),
            alex1);
    }
}
