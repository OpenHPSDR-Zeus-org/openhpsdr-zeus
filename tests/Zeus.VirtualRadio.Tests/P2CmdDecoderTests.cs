// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;
using Zeus.Contracts;
using Zeus.Protocol2;          // internal ComposeCmdRxBuffer / ComposeCmdTxBuffer, via InternalsVisibleTo
using Zeus.VirtualRadio.P2;

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Anti-drift tests for <see cref="P2CmdDecoder"/>: the decoder is fed bytes
/// produced by the REAL client composers
/// (<see cref="Protocol2Client.ComposeCmdRxBuffer"/> /
/// <see cref="Protocol2Client.ComposeCmdTxBuffer"/>) so a wire offset that
/// moves in the client breaks the round-trip. Socketless.
/// </summary>
public class P2CmdDecoderTests
{
    private static P2CmdDecoder Decoder() => new(HpsdrBoardKind.HermesII);

    [Fact]
    public void DecodeCmdRx_Burst_DetectsByte1363Mux_AndNumAdc()
    {
        // The exact bytes the client emits for the HermesII PS time-mux burst.
        byte[] p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesII,
            adcDitherEnabled: false, adcRandomEnabled: false,
            rx2Enabled: false, g2eFeedbackBurst: true);

        // Ground-truth: the client really does set byte 1363 = 0x02.
        Assert.Equal((byte)0x02, p[1363]);

        var state = new HostCommandState();
        var events = Decoder().Decode(P2Wire.CmdRxPort, p, state);

        Assert.NotEmpty(events);
        Assert.Equal(ProtocolVersion.P2, events[0].Protocol);
        Assert.True(state.PsArmedBurst);
        Assert.Equal((byte)1, state.NumAdc);
        Assert.Equal(192, state.SampleRateKhz);
    }

    [Fact]
    public void DecodeCmdRx_G2eBurst_HermesC10Decoder_DetectsByte1363Mux()
    {
        // The G2E (HermesC10) burst is byte-for-byte the 10E burst (RxBaseDdc is
        // DDC0 for both single-ADC Hermes-class boards), so a decoder constructed
        // for HermesC10 must detect the same byte-1363 Mux arm from the real
        // HermesC10 composer. This pins the emulator's G2E board parameterization
        // against the client's actual G2E wire.
        byte[] p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 192, psEnabled: true,
            boardKind: HpsdrBoardKind.HermesC10,
            adcDitherEnabled: false, adcRandomEnabled: false,
            rx2Enabled: false, g2eFeedbackBurst: true);

        Assert.Equal((byte)0x02, p[1363]);

        var state = new HostCommandState();
        var events = new P2CmdDecoder(HpsdrBoardKind.HermesC10)
            .Decode(P2Wire.CmdRxPort, p, state);

        Assert.NotEmpty(events);
        Assert.True(state.PsArmedBurst);
        Assert.Equal((byte)1, state.NumAdc);
        Assert.Equal(192, state.SampleRateKhz);
    }

    [Fact]
    public void DecodeCmdRx_Rest_NoMux_ReadsNegotiatedRate()
    {
        byte[] p = Protocol2Client.ComposeCmdRxBuffer(
            seq: 2, numAdc: 1, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.HermesII);

        Assert.Equal((byte)0x00, p[1363]);

        var state = new HostCommandState();
        Decoder().Decode(P2Wire.CmdRxPort, p, state);

        Assert.False(state.PsArmedBurst);
        Assert.Equal(48, state.SampleRateKhz);
    }

    [Fact]
    public void DecodeCmdTx_ReadsByte59_TxStepAttenuator()
    {
        // HermesII is a single-ADC board, so the client takes the non-PS
        // ComposeCmdTxBuffer branch (psEnabled=false) and writes
        // p[57]=p[58]=p[59]=txStepAttnDb. Byte 59 is the TX-time ADC attenuator.
        const byte seed = 31;
        byte[] p = Protocol2Client.ComposeCmdTxBuffer(
            seq: 3, sampleRateKhz: 192, txStepAttnDb: seed, paEnabled: true, psEnabled: false);

        Assert.Equal(seed, p[59]);

        var state = new HostCommandState();
        Decoder().Decode(P2Wire.CmdTxPort, p, state);

        Assert.Equal(seed, state.TxStepAttnDb);
    }

    [Fact]
    public void DecodeCmdHighPriority_ReadsMoxAndDrive()
    {
        var p = new byte[P2Wire.BufLen];
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0, 4), 9);
        p[P2Wire.HpRunMoxByte] = 0x03;       // run | PTT/MOX
        p[P2Wire.HpDriveByte] = 200;         // drive level
        // DDC0 RX NCO phase for 14.074 MHz.
        uint rxPhase = (uint)(14_074_000L * P2Wire.HzToPhase);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(P2Wire.HpDdc0PhaseOffset, 4), rxPhase);

        var state = new HostCommandState();
        Decoder().Decode(P2Wire.CmdHighPriorityPort, p, state);

        Assert.True(state.Running);
        Assert.True(state.Mox);
        Assert.Equal((byte)200, state.DriveByte);
        Assert.InRange(state.RxFreqHz, 14_073_990L, 14_074_010L); // phase quantisation
    }

    [Fact]
    public void DecodeCmdHighPriority_NotKeyed_MoxFalse()
    {
        var p = new byte[P2Wire.BufLen];
        p[P2Wire.HpRunMoxByte] = 0x01; // run only, no MOX
        p[P2Wire.HpDriveByte] = 0;

        var state = new HostCommandState();
        Decoder().Decode(P2Wire.CmdHighPriorityPort, p, state);

        Assert.True(state.Running);
        Assert.False(state.Mox);
    }

    [Fact]
    public void DecodeCmdGeneral_MarksRunning()
    {
        var p = new byte[P2Wire.CmdSmallLength];
        p[P2Wire.GeneralCmdByte] = P2Wire.GeneralConnect; // 0x00

        var state = new HostCommandState();
        var events = Decoder().Decode(P2Wire.CmdGeneralPort, p, state);

        Assert.NotEmpty(events);
        Assert.True(state.Running);
    }

    [Fact]
    public void Decode_NoAutoArm_FreshStateLeavesPsBurstOff()
    {
        // A plain connect + rest CmdRx must NEVER leave PS armed.
        var state = new HostCommandState();
        var dec = Decoder();

        var general = new byte[P2Wire.CmdSmallLength];
        dec.Decode(P2Wire.CmdGeneralPort, general, state);

        byte[] rest = Protocol2Client.ComposeCmdRxBuffer(
            seq: 1, numAdc: 1, sampleRateKhz: 48, psEnabled: false,
            boardKind: HpsdrBoardKind.HermesII);
        dec.Decode(P2Wire.CmdRxPort, rest, state);

        Assert.False(state.PsArmedBurst);
        Assert.False(state.Mox);
    }
}
