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
using Zeus.Contracts;

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

    // ---- G2E (HermesC10) single-ADC external-feedback bypass routing ----
    // The single-ADC G2E can only see an external sampler tap through the
    // RX-aux BYPASS relay. psWire is false for HermesC10, so the historical
    // gate never set the bit and calcc could never receive feedback. The fix
    // routes it via RoutesExternalPsFeedbackBypass, scoped to HermesC10.

    [Fact]
    public void Alex0_G2e_BypassBit_SetWhenExternal_PsArmed_AndMox()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: true,
            psEnabled: true,
            psExternal: true,
            board: HpsdrBoardKind.HermesC10);

        Assert.True((alex0 & Protocol2Client.AlexRxAntennaBypass) != 0,
            "G2E external PS armed + MOX must route the tap via the BYPASS relay.");
        // Parity: the single-ADC G2E must NOT assert ALEX_PS (that is the
        // dual-ADC Orion wire only). A spurious PS bit here would diverge
        // from the C10 gateware.
        Assert.True((alex0 & Protocol2Client.AlexPsBit) == 0,
            "G2E must never set ALEX_PS — it is not on the dual-ADC PS wire.");
    }

    [Fact]
    public void Alex0_G2e_BypassBit_SetEvenWhenInternal_SingleAdcHasNoInternalCoupler()
    {
        // Root-cause regression (#960): the single-ADC G2E has NO internal feedback
        // coupler — its one ADC (raw LTC2208, temp_ADC = INA, Hermes.v:1117-1130) can
        // only see the external sampler tap through the bit-11 BYPASS relay. So
        // "Internal" is a physically-impossible selection on this board, and the tap
        // MUST route regardless of the operator's Internal/External pick. Before the
        // fix, a G2E left on the default Internal source left the relay clear -> ADC
        // on the antenna -> calcc never converged -> dead PS meter ("as if PS isn't
        // on"). This test would PASS on the old code only by asserting == 0; it now
        // asserts the tap is routed even when Internal is selected.
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: true,
            psEnabled: true,
            psExternal: false,
            board: HpsdrBoardKind.HermesC10);

        Assert.True((alex0 & Protocol2Client.AlexRxAntennaBypass) != 0,
            "G2E must route the external tap even on the Internal pick — it has no " +
            "internal coupler; Internal-default is the dead-meter bug this fixes.");
        // Still never the dual-ADC Orion PS bit.
        Assert.True((alex0 & Protocol2Client.AlexPsBit) == 0,
            "G2E must never set ALEX_PS — it is not on the dual-ADC PS wire.");
    }

    [Fact]
    public void Alex0_G2e_BypassBit_ClearWhenMoxOff()
    {
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: false,
            psEnabled: true,
            psExternal: true,
            board: HpsdrBoardKind.HermesC10);

        Assert.True((alex0 & Protocol2Client.AlexRxAntennaBypass) == 0,
            "G2E BYPASS relay must not flip outside xmit.");
    }

    [Fact]
    public void Alex0_10e_BypassBit_StaysClear_ProvingG2eScoping()
    {
        // Other-board safety (KB2UKA hard constraint): the 10E (HermesII) is
        // the same single-ADC class but is intentionally left byte-identical
        // until a 10E owner can bench-confirm the routing. The fix must NOT
        // change the 10E wire.
        uint alex0 = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: true,
            psEnabled: true,
            psExternal: true,
            board: HpsdrBoardKind.HermesII);

        Assert.True((alex0 & Protocol2Client.AlexRxAntennaBypass) == 0,
            "10E BYPASS relay must stay clear — the fix is scoped to the G2E.");
        Assert.True((alex0 & Protocol2Client.AlexPsBit) == 0,
            "10E must never set ALEX_PS either (single-ADC board).");
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

    // ---- Golden full-buffer regression guards (issue #960) ----
    // These pin the ENTIRE composed wire buffer (every non-zero byte, and by
    // exhaustion every zero byte) for the HermesC10 (ANAN-G2E) single-ADC PS
    // time-mux path, the shared 10E (HermesII) burst, and the dual-ADC family.
    // They prove the G2E documentation + read-only instrumentation change made
    // ZERO wire-byte change: if any compose byte drifts on any of these boards,
    // exactly one of these fails.

    // Collect the sparse (index -> value) map of all non-zero bytes. Comparing
    // this against an explicit expected map pins the whole buffer: anything not
    // listed MUST be zero, or the count/equality check fails.
    private static SortedDictionary<int, byte> NonZeroBytes(byte[] p)
    {
        var map = new SortedDictionary<int, byte>();
        for (int i = 0; i < p.Length; i++)
            if (p[i] != 0) map[i] = p[i];
        return map;
    }

    [Fact]
    public void CmdRx_G2E_FeedbackBurst_GoldenBytes()
    {
        // ANAN-G2E (HermesC10) single-ADC time-mux PS feedback descriptor:
        // DDC0 enable ONLY (no DDC1 — the TX-DAC reference is the hardwired
        // rx_I[NR] receiver synced into Rx0), ADC0, 192 kHz / 24-bit, and
        // byte 1363 = 0x02 arming the SyncRx[0][1] coupler/reference interleave.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 7, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesC10, g2eFeedbackBurst: true);

        var expected = new SortedDictionary<int, byte>
        {
            [3] = 7,       // seq (BE)
            [4] = 1,       // single ADC
            [7] = 0x01,    // DDC0 enable only, NO DDC1
            [19] = 0xC0,   // DDC0 192 kHz BE low (0x00C0)
            [22] = 24,     // DDC0 24-bit
            [1363] = 0x02, // SyncRx[0][1] coupler/reference interleave
        };
        Assert.Equal(expected, NonZeroBytes(p));
    }

    [Fact]
    public void CmdRx_10E_FeedbackBurst_ByteIdenticalToG2E()
    {
        // The 10E (HermesII) burst is composed by the SAME g2eFeedbackBurst
        // branch — it MUST be byte-for-byte identical to the G2E burst. This
        // guards the shared branch: a G2E-scoped change must not perturb it.
        var g2e = Protocol2Client.ComposeCmdRxBuffer(
            seq: 7, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesC10, g2eFeedbackBurst: true);
        var tenE = Protocol2Client.ComposeCmdRxBuffer(
            seq: 7, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesII, g2eFeedbackBurst: true);

        Assert.Equal(g2e, tenE);
    }

    [Fact]
    public void CmdRx_DualAdc_PsArmed_GoldenBytes_Unaffected()
    {
        // Dual-ADC OrionMkII (G2/Saturn family) PS-armed compose: the dedicated
        // DDC0+DDC1 feedback pair (0x05 enable = DDC0|DDC2) plus user RX on DDC2,
        // byte 1363 = 0x02. Pinned to prove the bench radio's wire is untouched.
        var p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 7, numAdc: 2, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.OrionMkII);

        var expected = new SortedDictionary<int, byte>
        {
            [3] = 7,       // seq (BE)
            [4] = 2,       // dual ADC
            [7] = 0x05,    // DDC0 (feedback) | DDC2 (user RX)
            [19] = 0xC0,   // DDC0 192 kHz
            [22] = 24,     // DDC0 24-bit
            [23] = 2,      // DDC1 ADC = numAdc
            [25] = 0xC0,   // DDC1 192 kHz
            [28] = 24,     // DDC1 24-bit
            [31] = 0xC0,   // DDC2 (user RX) 192 kHz
            [34] = 24,     // DDC2 24-bit
            [1363] = 0x02, // feedback pair sync
        };
        Assert.Equal(expected, NonZeroBytes(p));
    }

    [Fact]
    public void CmdTx_G2E_PsArmed_Byte57Through59_TakeProtectiveFloor()
    {
        // On the G2E, ComposesPsFeedbackWire(HermesC10) is false, so SendCmdTx
        // composes CmdTx with psEnabled=false while the byte-59 seed has driven
        // txStepAttnDb to the protective floor (31). Bytes 57/58/59 all carry 31
        // — the historical single-ADC shape with the protective attenuation.
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 7, sampleRateKhz: 48, txStepAttnDb: 31, paEnabled: true, psEnabled: false);

        var expected = new SortedDictionary<int, byte>
        {
            [3] = 7,    // seq (BE)
            [4] = 1,    // num_dac
            [15] = 48,  // DAC sample rate 48 kHz BE low
            [57] = 31,  // ADC step atten (historical shape)
            [58] = 31,
            [59] = 31,  // Angelia_atten_Tx0 protective floor
        };
        Assert.Equal(expected, NonZeroBytes(p));
    }

    // ---- HermesC10 PS wire-instrumentation gate (issue #960) ----
    // The read-only p2.ps.g2e ~1 Hz diagnostic is gated by this pure predicate.
    // Deterministic, socketless: proves the log fires ONLY on the ANAN-G2E while
    // PS is armed AND keyed, and NEVER for the 10E or any dual-ADC board.

    [Fact]
    public void PsWireDiag_Emitted_OnlyFor_G2E_Armed_AndKeyed()
    {
        Assert.True(Protocol2Client.ShouldLogG2ePsWireDiag(
            HpsdrBoardKind.HermesC10, psFeedbackEnabled: true, txKeyed: true));
    }

    [Theory]
    [InlineData(false, true)]  // disarmed
    [InlineData(true, false)]  // armed but not keyed (DDC0 is user RX at rest)
    [InlineData(false, false)]
    public void PsWireDiag_NotEmitted_ForG2E_WhenNotArmedAndKeyed(bool armed, bool keyed)
    {
        Assert.False(Protocol2Client.ShouldLogG2ePsWireDiag(
            HpsdrBoardKind.HermesC10, psFeedbackEnabled: armed, txKeyed: keyed));
    }

    [Theory]
    [InlineData(HpsdrBoardKind.HermesII)]   // sibling 10E — diagnostic is G2E-scoped
    [InlineData(HpsdrBoardKind.OrionMkII)]  // dual-ADC — also reaches the paired decoder
    [InlineData(HpsdrBoardKind.Orion)]
    [InlineData(HpsdrBoardKind.Hermes)]
    public void PsWireDiag_NeverEmitted_ForOtherBoards_EvenArmedAndKeyed(HpsdrBoardKind board)
    {
        Assert.False(Protocol2Client.ShouldLogG2ePsWireDiag(
            board, psFeedbackEnabled: true, txKeyed: true));
    }
}
