// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Xunit;
using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Wire-format tests for the generalized N-receiver Receive-Specific composer
/// (<see cref="Protocol2Client.ComposeCmdRxBufferForReceivers"/>), the
/// foundation for full multi-RX (up to <see cref="Protocol2Client.MaxRxDdc"/>
/// concurrent DDCs across the dual phase-synchronous ADCs).
///
/// The first group pins byte-FOR-byte parity with the legacy
/// <see cref="Protocol2Client.ComposeCmdRxBuffer"/> for the RX1 / RX1+RX2 / PS
/// cases — so the generalization cannot drift from the wire shape every
/// shipped G2 / Saturn / Hermes operator is validated against. The second
/// group exercises the new capability: independent per-DDC ADC assignment and
/// per-DDC sample rate across all 8 DDCs.
/// </summary>
public class ComposeCmdRxBufferForReceiversTests
{
    private static Protocol2Client.DdcReceiverSpec Rx(byte adc, ushort rateKhz) => new(adc, rateKhz);

    // ---- Parity with the legacy composer -----------------------------------

    [Fact]
    public void SingleReceiver_OrionMkII_MatchesLegacy_Rx1Only()
    {
        var legacy = Protocol2Client.ComposeCmdRxBuffer(
            seq: 42, numAdc: 2, sampleRateKhz: 192, psEnabled: false);
        var generalized = Protocol2Client.ComposeCmdRxBufferForReceivers(
            seq: 42, numAdc: 2, receivers: [Rx(0, 192)]);

        Assert.Equal(legacy, generalized);
        Assert.Equal((byte)0x04, generalized[7]); // DDC2 enable
    }

    [Fact]
    public void TwoReceivers_OrionMkII_MatchesLegacy_Rx2OnSameAdc()
    {
        var legacy = Protocol2Client.ComposeCmdRxBuffer(
            seq: 7, numAdc: 2, sampleRateKhz: 96, psEnabled: false, rx2Enabled: true);
        // Legacy RX2 sources ADC0 (Rx2AdcSource) at the same rate as RX1.
        var generalized = Protocol2Client.ComposeCmdRxBufferForReceivers(
            seq: 7, numAdc: 2, receivers: [Rx(0, 96), Rx(0, 96)]);

        Assert.Equal(legacy, generalized);
        Assert.Equal((byte)0x0C, generalized[7]); // DDC2 | DDC3
    }

    [Fact]
    public void PsArmed_SingleReceiver_OrionMkII_MatchesLegacyPs()
    {
        var legacy = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 2, sampleRateKhz: 192, psEnabled: true);
        var generalized = Protocol2Client.ComposeCmdRxBufferForReceivers(
            seq: 9, numAdc: 2, receivers: [Rx(0, 192)], psEnabled: true);

        Assert.Equal(legacy, generalized);
        Assert.Equal((byte)0x05, generalized[7]);  // DDC0 (PS) | DDC2 (RX1)
        Assert.Equal((byte)0x02, generalized[1363]); // DDC1→DDC0 sync
    }

    [Fact]
    public void SingleReceiver_Hermes_MatchesLegacy_Ddc0()
    {
        var legacy = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.Hermes);
        var generalized = Protocol2Client.ComposeCmdRxBufferForReceivers(
            seq: 1, numAdc: 1, receivers: [Rx(0, 48)],
            boardKind: HpsdrBoardKind.Hermes);

        Assert.Equal(legacy, generalized);
        Assert.Equal((byte)0x01, generalized[7]); // DDC0
    }

    [Fact]
    public void AdcOptions_MatchLegacy()
    {
        var legacy = Protocol2Client.ComposeCmdRxBuffer(
            seq: 3, numAdc: 2, sampleRateKhz: 192, psEnabled: false,
            adcDitherEnabled: true, adcRandomEnabled: true);
        var generalized = Protocol2Client.ComposeCmdRxBufferForReceivers(
            seq: 3, numAdc: 2, receivers: [Rx(0, 192)],
            adcDitherEnabled: true, adcRandomEnabled: true);

        Assert.Equal(legacy, generalized);
        Assert.Equal((byte)0x07, generalized[5]);
        Assert.Equal((byte)0x07, generalized[6]);
    }

    // ---- New capability: per-DDC ADC assignment + per-DDC rate --------------

    [Fact]
    public void FourReceivers_DualAdc_MixedRates_EnableBitsAndBlocks()
    {
        // RX0=ADC0@192, RX1=ADC1@384, RX2=ADC0@768, RX3=ADC1@1536 on OrionMkII
        // → DDC2,3,4,5.
        var p = Protocol2Client.ComposeCmdRxBufferForReceivers(
            seq: 1, numAdc: 2,
            receivers: [Rx(0, 192), Rx(1, 384), Rx(0, 768), Rx(1, 1536)]);

        // Enable bits: DDC2|DDC3|DDC4|DDC5 = 0x3C.
        Assert.Equal((byte)0x3C, p[7]);

        // DDC2 @ off 29: ADC0, 192 kHz (0x00C0), 24-bit.
        Assert.Equal((byte)0x00, p[29]);
        Assert.Equal((byte)0x00, p[30]);
        Assert.Equal((byte)0xC0, p[31]);
        Assert.Equal((byte)24, p[34]);

        // DDC3 @ off 35: ADC1, 384 kHz (0x0180), 24-bit.
        Assert.Equal((byte)0x01, p[35]);
        Assert.Equal((byte)0x01, p[36]);
        Assert.Equal((byte)0x80, p[37]);
        Assert.Equal((byte)24, p[40]);

        // DDC4 @ off 41: ADC0, 768 kHz (0x0300), 24-bit.
        Assert.Equal((byte)0x00, p[41]);
        Assert.Equal((byte)0x03, p[42]);
        Assert.Equal((byte)0x00, p[43]);
        Assert.Equal((byte)24, p[46]);

        // DDC5 @ off 47: ADC1, 1536 kHz (0x0600), 24-bit.
        Assert.Equal((byte)0x01, p[47]);
        Assert.Equal((byte)0x06, p[48]);
        Assert.Equal((byte)0x00, p[49]);
        Assert.Equal((byte)24, p[52]);
    }

    [Fact]
    public void EightReceivers_Hermes_FillsDdc0ThroughDdc7()
    {
        // Single-ADC Hermes-class: RX0..RX7 map to DDC0..DDC7, all enabled.
        var recv = new Protocol2Client.DdcReceiverSpec[8];
        for (int i = 0; i < 8; i++) recv[i] = Rx(0, 96);

        var p = Protocol2Client.ComposeCmdRxBufferForReceivers(
            seq: 1, numAdc: 1, receivers: recv, boardKind: HpsdrBoardKind.Hermes);

        Assert.Equal((byte)0xFF, p[7]); // all 8 DDC enable bits set
        // Spot-check the last DDC's block (DDC7 @ off 17 + 7*6 = 59).
        Assert.Equal((byte)0x00, p[59]);      // ADC0
        Assert.Equal((byte)96, p[61]);        // 96 kHz BE low
        Assert.Equal((byte)24, p[64]);        // 24-bit
    }

    [Fact]
    public void ExceedingProtocolCeiling_Throws()
    {
        // OrionMkII base DDC is 2, so the 7th user receiver would land on DDC8,
        // past the 8-DDC (DDC0..7) protocol ceiling.
        var recv = new Protocol2Client.DdcReceiverSpec[7];
        for (int i = 0; i < 7; i++) recv[i] = Rx(0, 192);

        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            Protocol2Client.ComposeCmdRxBufferForReceivers(
                seq: 1, numAdc: 2, receivers: recv));
    }

    [Fact]
    public void MaxRxDdc_IsEight()
    {
        // Pin the verified protocol ceiling so a future "bump to 10" is a
        // deliberate, reviewed change rather than an accident.
        Assert.Equal(8, Protocol2Client.MaxRxDdc);
    }
}
