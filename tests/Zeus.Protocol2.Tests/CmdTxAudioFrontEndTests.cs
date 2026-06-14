// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Protocol2.Tests;

/// <summary>
/// GOLDEN-BYTE tests for the Protocol-2 audio front-end (external-ports plan,
/// Phase 4). The TxSpecific (CmdTx, port 1026) packet carries the mic/line-in
/// front-end at byte 50 (mic_control flags) + byte 51 (line_in_gain) — verified
/// against ramdor Thetis network.c:1234,1236 with the byte-50 bit layout from
/// network.c:1226-1233:
///   bit0 = line_in, bit1 = mic_boost, bit2 = mic_ptt_disabled,
///   bit3 = mic_ptt_tip, bit4 = mic_bias_enable, bit5 = balanced(XLR) input.
///
/// DEFAULT-UNSENT: with the audio front-end at defaults, bytes 50/51 stay 0,
/// so the CmdTx tail is byte-identical to the pre-feature all-zero memset.
/// </summary>
public class CmdTxAudioFrontEndTests
{
    [Fact]
    public void CmdTx_DefaultAudio_Bytes50And51AreZero()
    {
        // No mic_control / line_in_gain args → both bytes stay at the memset 0,
        // byte-identical to today's CmdTx.
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 48, txStepAttnDb: 0, paEnabled: true, psEnabled: false);
        Assert.Equal((byte)0, p[50]);
        Assert.Equal((byte)0, p[51]);
    }

    [Theory]
    [InlineData(false, false, false, false, 0x00)]
    [InlineData(true,  false, false, false, 0x01)] // line_in → bit0
    [InlineData(false, true,  false, false, 0x02)] // mic_boost → bit1
    [InlineData(false, false, true,  false, 0x10)] // mic_bias → bit4
    [InlineData(false, false, false, true,  0x20)] // balanced/XLR → bit5
    [InlineData(true,  true,  true,  true,  0x33)] // all four
    public void CmdTx_MicControl_FlagLayoutIsLocked(
        bool lineIn, bool micBoost, bool micBias, bool xlr, byte expected)
    {
        byte ctl = 0;
        if (lineIn)   ctl |= Protocol2Client.MicControlLineIn;
        if (micBoost) ctl |= Protocol2Client.MicControlMicBoost;
        if (micBias)  ctl |= Protocol2Client.MicControlMicBias;
        if (xlr)      ctl |= Protocol2Client.MicControlXlr;

        Assert.Equal(expected, ctl);

        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 48, txStepAttnDb: 0, paEnabled: true, psEnabled: false,
            micControl: ctl, lineInGain: 0);
        Assert.Equal(expected, p[50]);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)15)]
    [InlineData((byte)31)]
    public void CmdTx_LineInGain_LandsInByte51(byte gain)
    {
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 48, txStepAttnDb: 0, paEnabled: true, psEnabled: false,
            micControl: 0, lineInGain: gain);
        Assert.Equal(gain, p[51]);
    }

    [Fact]
    public void CmdTx_AudioSet_DoesNotDisturbStepAttenuatorBytes()
    {
        // The audio bytes (50/51) are disjoint from the PS / PA-protection
        // bytes (57/58/59). Setting audio must not move them.
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 48, txStepAttnDb: 17, paEnabled: true, psEnabled: true,
            micControl: 0x33, lineInGain: 31);
        // PS-on asymmetry preserved.
        Assert.Equal((byte)0, p[57]);
        Assert.Equal((byte)31, p[58]);
        Assert.Equal((byte)17, p[59]);
        // Audio landed.
        Assert.Equal((byte)0x33, p[50]);
        Assert.Equal((byte)31, p[51]);
    }
}
