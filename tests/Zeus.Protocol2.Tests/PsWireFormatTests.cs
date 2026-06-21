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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Xunit;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// Wire-format tests for PureSignal-armed CmdRx packets. Sourced from
/// pihpsdr new_protocol.c:1611-1630 + Thetis network.c. The bytes that
/// must change when PS is armed:
///   - p[7] |= 0x01           DDC0 enable (alongside existing DDC2)
///   - p[1363] = 0x02         sync DDC1→DDC0
///   - p[17]   = 0x00         DDC0 ADC = 0
///   - p[18..19] = 0x00 0xC0  DDC0 sample rate = 192 kHz BE
///   - p[22]   = 24           DDC0 bit depth
///   - p[23]   = numAdc       DDC1 ADC selection
///   - p[24..25] = 0x00 0xC0  DDC1 sample rate = 192 kHz BE
///   - p[28]   = 24           DDC1 bit depth
/// </summary>
public class PsWireFormatTests
{
    [Fact]
    public void CmdRx_NotArmed_LeavesDdc0AndSyncBitClear()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 7, numAdc: 2, sampleRateKhz: 192, psEnabled: false);

        Assert.Equal((byte)0x04, p[7]);          // only DDC2 enable bit
        Assert.Equal((byte)0x00, p[1363]);       // no DDC1→DDC0 sync
        // DDC0 cfg block stays zeroed.
        Assert.Equal((byte)0x00, p[17]);
        Assert.Equal((byte)0x00, p[18]);
        Assert.Equal((byte)0x00, p[19]);
        Assert.Equal((byte)0x00, p[22]);
    }

    [Fact]
    public void CmdRx_G2AdcOptions_Write_Dither_And_Random_Bytes()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 7,
            numAdc: 2,
            sampleRateKhz: 192,
            psEnabled: false,
            adcDitherEnabled: true,
            adcRandomEnabled: true);

        Assert.Equal((byte)0x07, p[5]); // Thetis SetADCDither writes ADC0..2
        Assert.Equal((byte)0x07, p[6]); // Thetis SetADCRandom writes ADC0..2
    }

    [Fact]
    public void CmdRx_G2AdcOptions_Can_Be_Disabled_Independently()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 7,
            numAdc: 2,
            sampleRateKhz: 192,
            psEnabled: false,
            adcDitherEnabled: true,
            adcRandomEnabled: false);

        Assert.Equal((byte)0x07, p[5]);
        Assert.Equal((byte)0x00, p[6]);
    }

    [Fact]
    public void CmdRx_PsArmed_EnablesDdc0AndSyncBit()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 9, numAdc: 2, sampleRateKhz: 192, psEnabled: true);

        Assert.Equal((byte)0x05, p[7]);          // DDC0 (0x01) | DDC2 (0x04)
        Assert.Equal((byte)0x02, p[1363]);       // DDC1→DDC0 sync
    }

    [Fact]
    public void CmdRx_PsArmed_ConfiguresDdc0_192kHz_24Bit_FromAdc0()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 192, psEnabled: true);

        // DDC0 cfg at offset 17.
        Assert.Equal((byte)0x00, p[17]);         // ADC0
        // 192 kHz big-endian = 0x00 0xC0.
        Assert.Equal((byte)0x00, p[18]);
        Assert.Equal((byte)0xC0, p[19]);
        Assert.Equal((byte)24, p[22]);           // 24-bit depth
    }

    [Fact]
    public void CmdRx_PsArmed_ConfiguresDdc1_192kHz_24Bit_FromNAdc()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 192, psEnabled: true);

        // DDC1 cfg at offset 23 = 17 + 6.
        Assert.Equal((byte)2, p[23]);            // ADC = numAdc
        Assert.Equal((byte)0x00, p[24]);
        Assert.Equal((byte)0xC0, p[25]);
        Assert.Equal((byte)24, p[28]);
    }

    [Fact]
    public void CmdRx_PreservesDdc2AndSequence()
    {
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 0xDEADBEEF, numAdc: 2, sampleRateKhz: 96, psEnabled: true);

        // Sequence at byte 0 BE.
        Assert.Equal((byte)0xDE, p[0]);
        Assert.Equal((byte)0xAD, p[1]);
        Assert.Equal((byte)0xBE, p[2]);
        Assert.Equal((byte)0xEF, p[3]);
        // DDC2 cfg at 17 + 12 = 29.
        Assert.Equal((byte)0x00, p[29]);
        Assert.Equal((byte)0x00, p[30]);
        Assert.Equal((byte)96, p[31]);
        Assert.Equal((byte)24, p[34]);
    }

    [Fact]
    public void AlexPsBit_Is_0x00040000()
    {
        // Defensive constant test — pihpsdr new_protocol.c:994-998 says
        // ALEX_PS_BIT = 0x00040000. If we change it Brian's G2 stops
        // engaging the feedback-coupler tap.
        Assert.Equal(0x00040000u, Protocol2Client.AlexPsBit);
    }

    [Fact]
    public void AlexRxAntennaBypass_Is_0x00000800()
    {
        // pihpsdr new_protocol.c:1284-1296. Wrong value silently breaks
        // the External feedback path.
        Assert.Equal(0x00000800u, Protocol2Client.AlexRxAntennaBypass);
    }

    // ---- DDC0/DDC1 destination assignment (round-1 swap regression) ----

    [Fact]
    public void DecodePsPair_Ddc0BytesLandInRx_Ddc1BytesLandInTx()
    {
        // Synthesize a sample-pair with distinct, identifiable patterns
        // for DDC0 vs DDC1 so a swap bug shows up immediately.
        // DDC0 = +1.0 in I, +0.5 in Q (max-positive pattern)
        // DDC1 = -1.0 in I, -0.5 in Q (max-negative pattern)
        // 24-bit signed: 0x7FFFFF =  8388607, 0x800000 = -8388608.
        // We use 0x400000 (≈+0.5) and 0xC00000 (≈-0.5) for the Q values.
        var pair = new byte[12]
        {
            // DDC0 I = 0x7FFFFF
            0x7F, 0xFF, 0xFF,
            // DDC0 Q = 0x400000
            0x40, 0x00, 0x00,
            // DDC1 I = 0x800000
            0x80, 0x00, 0x00,
            // DDC1 Q = 0xC00000
            0xC0, 0x00, 0x00,
        };

        var (rxI, rxQ, txI, txQ) = Protocol2Client.DecodePsPairForTest(pair);

        // DDC0 → rx side, DDC1 → tx side. If this assertion ever flips,
        // PS will arm but never correct (round-1 bug).
        Assert.True(rxI > 0.99f, $"rxI from DDC0 should be ~+1.0, got {rxI}");
        Assert.True(rxQ > 0.49f && rxQ < 0.51f, $"rxQ from DDC0 should be ~+0.5, got {rxQ}");
        Assert.True(txI < -0.99f, $"txI from DDC1 should be ~-1.0, got {txI}");
        Assert.True(txQ < -0.49f && txQ > -0.51f, $"txQ from DDC1 should be ~-0.5, got {txQ}");
    }

    // ---- ALEX bypass bit (External feedback antenna) ----

    [Fact]
    public void Alex0_BypassBit_SetWhenExternal_PsArmed_AndMox()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: true,
            psEnabled: true,
            psExternal: true);

        Assert.True((alex0 & Protocol2Client.AlexRxAntennaBypass) != 0,
            "Bypass bit must be set when PS armed && External && MOX.");
    }

    [Fact]
    public void Alex0_BypassBit_ClearWhenInternal()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: true,
            psEnabled: true,
            psExternal: false);

        Assert.True((alex0 & Protocol2Client.AlexRxAntennaBypass) == 0,
            "Bypass bit must stay clear in Internal-coupler mode.");
        // Sanity: PS bit is still set during MOX.
        Assert.True((alex0 & Protocol2Client.AlexPsBit) != 0,
            "PS bit should still be set on alex0 during xmit + PS armed.");
    }

    [Fact]
    public void Alex0_BypassBit_ClearWhenMoxOff()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: false,
            psEnabled: true,
            psExternal: true);

        Assert.True((alex0 & Protocol2Client.AlexRxAntennaBypass) == 0,
            "Bypass bit must not flip on alex0 outside xmit (matches pihpsdr).");
    }

    [Fact]
    public void Alex0_BypassBit_ClearWhenPsDisarmed()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: true,
            psEnabled: false,
            psExternal: true);

        Assert.True((alex0 & Protocol2Client.AlexRxAntennaBypass) == 0,
            "Bypass bit must not flip when PS isn't armed even if External is selected.");
    }

    // ---- RxSpecific buffer parity between Internal and External ----

    [Fact]
    public void CmdRx_BytesAreIdentical_BetweenInternalAndExternal()
    {
        // The RxSpecific buffer doesn't take a 'feedback source' input
        // (only psEnabled) — verify it stays that way so a future change
        // doesn't accidentally start emitting different bytes per source.
        var p1 = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 192, psEnabled: true);
        var p2 = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 2, sampleRateKhz: 192, psEnabled: true);

        Assert.Equal(p1, p2);
    }

    // ---- CmdTx (TxSpecific) — TX step attenuator wire support for PS
    // AutoAttenuate. pihpsdr new_protocol.c:1540-1547 enforces an asymmetric
    // PA-protection invariant: byte 58 (ADC1 / TX-DAC reference) MUST stay
    // at 31 dB whenever PA is on, while byte 59 (ADC0 / PA-feedback) is the
    // ONE byte PS overrides with the operator/auto-attenuator value. When
    // PS is off Zeus preserves the historical wire shape that voice TX has
    // been validated against.

    [Fact]
    public void CmdTx_PsOff_HistoricalShape_TxStepAttnLandsInAllThreeBytes()
    {
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 48, txStepAttnDb: 17, paEnabled: true, psEnabled: false);

        // PS off: historical Zeus shape — value lands in 57/58/59 verbatim
        // so normal voice TX wire form is unchanged from the prior release.
        Assert.Equal((byte)17, p[57]);
        Assert.Equal((byte)17, p[58]);
        Assert.Equal((byte)17, p[59]);
    }

    [Fact]
    public void CmdTx_PsOn_PaOn_PihpsdrAsymmetry_Byte58Stays31_Byte59TakesAttn()
    {
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 48, txStepAttnDb: 17, paEnabled: true, psEnabled: true);

        // pihpsdr new_protocol.c:1540-1547: byte 58 = 31 (TX-DAC ref
        // protection, never overridden by PS), byte 59 = operator step-att
        // (PA-feedback ADC, the only byte PS owns). Byte 57 reserved → 0.
        Assert.Equal((byte)0, p[57]);
        Assert.Equal((byte)31, p[58]);
        Assert.Equal((byte)17, p[59]);
    }

    [Fact]
    public void CmdTx_PsOn_PaOff_Byte58Zero_Byte59TakesAttn()
    {
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 48, txStepAttnDb: 17, paEnabled: false, psEnabled: true);

        // PA off: nothing to protect, so byte 58 stays at 0. Byte 59 still
        // carries the dynamic PS step-att.
        Assert.Equal((byte)0, p[57]);
        Assert.Equal((byte)0, p[58]);
        Assert.Equal((byte)17, p[59]);
    }

    [Fact]
    public void CmdTx_DefaultZeroAttn_PsOff_LeavesBytes57Through59Clear()
    {
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 0, sampleRateKhz: 48, txStepAttnDb: 0, paEnabled: true, psEnabled: false);

        Assert.Equal((byte)0, p[57]);
        Assert.Equal((byte)0, p[58]);
        Assert.Equal((byte)0, p[59]);
    }

    [Fact]
    public void CmdTx_PreservesSequenceAndNumDac()
    {
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 0xCAFEBABE, sampleRateKhz: 192, txStepAttnDb: 5, paEnabled: false, psEnabled: false);

        // Sequence at byte 0 BE
        Assert.Equal((byte)0xCA, p[0]);
        Assert.Equal((byte)0xFE, p[1]);
        Assert.Equal((byte)0xBA, p[2]);
        Assert.Equal((byte)0xBE, p[3]);
        // num_dac always 1 on G2
        Assert.Equal((byte)1, p[4]);
        // Sample rate at bytes 14..15 BE — 192 = 0x00C0
        Assert.Equal((byte)0x00, p[14]);
        Assert.Equal((byte)0xC0, p[15]);
    }
}
