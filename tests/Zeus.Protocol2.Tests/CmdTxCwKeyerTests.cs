// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

/// <summary>
/// GOLDEN-BYTE tests for the Protocol-2 internal (FPGA) CW keyer — issue
/// #1032. The TxSpecific (CmdTx, port 1026) packet carries the CW config that
/// arms the radio's own keyer so a physical key/paddle on the rear KEY jack
/// keys the transmitter. Byte map verified against pihpsdr new_protocol.c
/// new_protocol_tx_specific (transmit_specific_buffer[5..17]) and the OpenHPSDR
/// Ethernet Protocol v4.4 "Transmitter Specific" packet (pp.29-30):
///   byte 5    = CW mode_control bitfield
///   byte 6    = sidetone level (0-127)
///   bytes 7-8 = sidetone frequency (Hz, big-endian)
///   byte 9    = keyer speed (WPM)
///   byte 10   = keyer weight
///   bytes 11-12 = hang delay (ms, big-endian)
///   byte 13   = RF (key-down) delay (ms, clamped to 900/WPM)
///   byte 17   = CW envelope ramp (ms)
///
/// DEFAULT-UNSENT: with CW inactive, every CW byte stays 0 — so a non-CW
/// (SSB/AM/FM/DIG) transmit is byte-identical to the pre-feature emission.
/// </summary>
public class CmdTxCwKeyerTests
{
    private static byte[] Compose(CwKeyerWireConfig cw) =>
        Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 48, txStepAttnDb: 0, paEnabled: true, psEnabled: false,
            micControl: 0, lineInGain: 0, cw: cw);

    private static CwKeyerWireConfig ActiveCw(
        CwKeyerMode mode = CwKeyerMode.Straight, int wpm = 22, int sidetoneHz = 600, byte level = 0) =>
        new() { Active = true, Mode = mode, SpeedWpm = wpm, SidetoneHz = sidetoneHz, SidetoneLevel = level };

    // ---- mode_control byte-5 bit positions are locked to the wire spec ----

    [Fact]
    public void CwModeBitConstants_MatchWireSpec()
    {
        Assert.Equal((byte)0x02, Protocol2Client.CwModeCwSelected);
        Assert.Equal((byte)0x04, Protocol2Client.CwModeReverse);
        Assert.Equal((byte)0x08, Protocol2Client.CwModeIambic);
        Assert.Equal((byte)0x10, Protocol2Client.CwModeSidetone);
        Assert.Equal((byte)0x20, Protocol2Client.CwModeModeB);
        Assert.Equal((byte)0x40, Protocol2Client.CwModeStrictSpacing);
        Assert.Equal((byte)0x80, Protocol2Client.CwModeBreakIn);
    }

    [Theory]
    // Straight key, no sidetone: CW-selected (0x02) + break-in (0x80) = 0x82.
    [InlineData(CwKeyerMode.Straight, (byte)0,  (byte)0x82)]
    // Straight key, sidetone on: + sidetone bit (0x10) = 0x92.
    [InlineData(CwKeyerMode.Straight, (byte)40, (byte)0x92)]
    // Iambic A: + iambic (0x08) = 0x8A.
    [InlineData(CwKeyerMode.IambicA,  (byte)0,  (byte)0x8A)]
    // Iambic B: + iambic (0x08) + Mode B (0x20) = 0xAA.
    [InlineData(CwKeyerMode.IambicB,  (byte)0,  (byte)0xAA)]
    public void CmdTx_CwActive_Byte5_ModeControlIsLocked(CwKeyerMode mode, byte level, byte expected)
    {
        var p = Compose(ActiveCw(mode: mode, level: level));
        Assert.Equal(expected, p[5]);
    }

    [Fact]
    public void CmdTx_CwActive_SidetoneFreqIsBigEndian_AndLevelInByte6()
    {
        var p = Compose(ActiveCw(sidetoneHz: 700, level: 64));
        Assert.Equal((byte)64, p[6]);                 // level
        Assert.Equal((byte)(700 >> 8), p[7]);         // freq high (BE)
        Assert.Equal((byte)(700 & 0xFF), p[8]);       // freq low
        Assert.Equal(700, (p[7] << 8) | p[8]);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(22)]
    [InlineData(60)]
    public void CmdTx_CwActive_WpmInByte9_WeightHangRampPinned(int wpm)
    {
        var p = Compose(ActiveCw(wpm: wpm));
        Assert.Equal((byte)wpm, p[9]);                // keyer speed
        Assert.Equal((byte)50, p[10]);                // weight pinned 50%
        Assert.Equal(300, (p[11] << 8) | p[12]);      // hang delay BE, 300 ms
        Assert.Equal((byte)9, p[17]);                 // ramp pinned 9 ms (Thetis)
        // RF delay = min(8, 900/wpm); 900/wpm >= 15 for wpm<=60, so always 8.
        Assert.Equal((byte)Math.Min(8, 900 / wpm), p[13]);
    }

    [Fact]
    public void CmdTx_CwActive_WpmIsClampedTo_1_60()
    {
        Assert.Equal((byte)1, Compose(ActiveCw(wpm: 0))[9]);    // clamp low
        Assert.Equal((byte)60, Compose(ActiveCw(wpm: 200))[9]); // clamp high
    }

    [Fact]
    public void CmdTx_CwActive_DoesNotDisturbSampleRate_Audio_OrAttenuatorBytes()
    {
        // CW bytes (5-13, 17) are disjoint from sample-rate (14-15), audio
        // (50/51) and step-attenuator (57/58/59) bytes.
        var p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 1, sampleRateKhz: 192, txStepAttnDb: 17, paEnabled: true, psEnabled: true,
            micControl: 0x33, lineInGain: 31, cw: ActiveCw(level: 64));
        Assert.Equal(192, (p[14] << 8) | p[15]);  // sample rate intact
        Assert.Equal((byte)0x33, p[50]);          // audio intact
        Assert.Equal((byte)31, p[51]);
        Assert.Equal((byte)0, p[57]);             // PS-on atten asymmetry intact
        Assert.Equal((byte)31, p[58]);
        Assert.Equal((byte)17, p[59]);
    }

    // ---- DEFAULT-UNSENT: CW inactive is byte-identical to the pre-feature wire ----

    [Fact]
    public void CmdTx_CwInactive_AllCwBytesAreZero()
    {
        var p = Compose(CwKeyerWireConfig.Inactive);
        foreach (var i in new[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 17 })
            Assert.Equal((byte)0, p[i]);
    }

    [Fact]
    public void CmdTx_NotActive_SuppressesAllCwBytes_EvenWithConfigPopulated()
    {
        // The arm gate (RadioService: CW mode AND host-CW idle) lands here as
        // Active=false. When disarmed, a fully-populated keyer config must
        // still emit zero CW bytes — so a host-keyed CW send (MoxSource.Cwx)
        // or a non-CW mode can never leave the FPGA keyer armed on the wire.
        var p = Compose(new CwKeyerWireConfig
        {
            Active = false,
            Mode = CwKeyerMode.IambicB,
            SpeedWpm = 30,
            SidetoneHz = 700,
            SidetoneLevel = 64,
        });
        foreach (var i in new[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 17 })
            Assert.Equal((byte)0, p[i]);
    }

    [Theory]
    [InlineData(false, false)] // PS off
    [InlineData(true,  true)]  // PS on + PA
    public void CmdTx_CwInactive_Is_FullBuffer_ByteIdentical_To_PreFeatureEmission(
        bool psEnabled, bool paEnabled)
    {
        // The pre-feature emission omits the cw arg entirely (default). An
        // explicit Inactive config must reproduce the whole 60-byte buffer
        // byte-for-byte — the regression guard that a non-CW transmit is
        // unchanged on the wire.
        var baseline = Protocol2Client.ComposeCmdTxBuffer(
            seq: 7, sampleRateKhz: 48, txStepAttnDb: 19, paEnabled: paEnabled, psEnabled: psEnabled);
        var cwInactive = Protocol2Client.ComposeCmdTxBuffer(
            seq: 7, sampleRateKhz: 48, txStepAttnDb: 19, paEnabled: paEnabled, psEnabled: psEnabled,
            micControl: 0, lineInGain: 0, cw: CwKeyerWireConfig.Inactive);

        Assert.Equal(60, cwInactive.Length);
        Assert.True(baseline.AsSpan().SequenceEqual(cwInactive),
            "CW-inactive must reproduce the pre-feature CmdTx buffer byte-for-byte.");
    }
}
