// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1;            // internal ControlFrame, via InternalsVisibleTo
using Zeus.VirtualRadio.P1;      // internal Ep2Decoder, via InternalsVisibleTo

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Anti-drift round-trip tests for <see cref="Ep2Decoder"/>: host packets are
/// built with the REAL <c>Zeus.Protocol1.ControlFrame</c> encoder — the encoder
/// the emulator inverts — then decoded, and the recovered
/// <see cref="HostCommandState"/> fields are asserted to match what was encoded.
/// Socketless: pure byte buffers. If the EP2 wire format changes in Zeus, these
/// break.
/// </summary>
public class Ep2DecoderRoundTripTests
{
    // The ANAN-10E vertical-slice board.
    private const HpsdrBoardKind Board = HpsdrBoardKind.HermesII;

    private static ControlFrame.CcState BaseState(
        long vfoHz = 14_074_000,
        HpsdrSampleRate rate = HpsdrSampleRate.Rate48k,
        bool preamp = false,
        int attenDb = 0,
        bool mox = false,
        byte drive = 0,
        byte ocTx = 0,
        byte ocRx = 0,
        byte numRxMinus1 = 0,
        bool micBoost = false,
        bool micLineIn = false) =>
        new ControlFrame.CcState(
            VfoAHz: vfoHz,
            Rate: rate,
            PreampOn: preamp,
            Atten: new HpsdrAtten(attenDb),
            RxAntenna: HpsdrAntenna.Ant1,
            Mox: mox,
            EnableHl2BandVolts: false,
            Board: Board,
            DriveLevel: drive,
            UserOcTxMask: ocTx,
            UserOcRxMask: ocRx,
            NumReceiversMinusOne: numRxMinus1,
            MicBoost: micBoost,
            MicLineIn: micLineIn);

    private static byte[] BuildPacket(ControlFrame.CcRegister even, ControlFrame.CcRegister odd, in ControlFrame.CcState state)
    {
        var packet = new byte[ControlFrame.PacketLength];
        ControlFrame.BuildDataPacket(packet, sendSequence: 1, even, odd, in state);
        return packet;
    }

    [Fact]
    public void Decode_ConfigFrame_RecoversRateOcPreampNumRx()
    {
        var state = BaseState(
            rate: HpsdrSampleRate.Rate96k,
            preamp: true,
            ocRx: 0x05,
            numRxMinus1: 1,
            mox: false);
        var packet = BuildPacket(ControlFrame.CcRegister.Config, ControlFrame.CcRegister.RxFreq, in state);

        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        var events = dec.Decode(packet, host);

        Assert.Equal(2, events.Count);
        Assert.Equal("Config", events[0].CommandKind);
        Assert.Equal(96, host.SampleRateKhz);
        Assert.True(host.PreampOn);
        Assert.Equal((byte)0x05, host.OcRxMask);   // !MOX → RX mask
        Assert.Equal((byte)1, host.NumReceiversMinusOne);
        Assert.False(host.Mox);
    }

    [Fact]
    public void Decode_Config_RoutesOcMaskByMox()
    {
        // MOX engaged → the OC mask Zeus sends is the TX mask, and the decoder
        // must store it as OcTxMask (Config C2 = ocPins << 1, ocPins selected by
        // MOX in ControlFrame.WriteConfigPayload).
        var state = BaseState(mox: true, ocTx: 0x12, ocRx: 0x7F);
        var packet = BuildPacket(ControlFrame.CcRegister.Config, ControlFrame.CcRegister.TxFreq, in state);

        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        dec.Decode(packet, host);

        Assert.True(host.Mox);
        Assert.Equal((byte)0x12, host.OcTxMask);
        Assert.Equal((byte)0x00, host.OcRxMask); // untouched while keyed
    }

    [Theory]
    [InlineData(HpsdrSampleRate.Rate48k, 48)]
    [InlineData(HpsdrSampleRate.Rate96k, 96)]
    [InlineData(HpsdrSampleRate.Rate192k, 192)]
    [InlineData(HpsdrSampleRate.Rate384k, 384)]
    public void Decode_Config_AllSampleRates(HpsdrSampleRate rate, int expectedKhz)
    {
        var state = BaseState(rate: rate);
        var packet = BuildPacket(ControlFrame.CcRegister.Config, ControlFrame.CcRegister.RxFreq, in state);

        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        dec.Decode(packet, host);

        Assert.Equal(expectedKhz, host.SampleRateKhz);
    }

    [Fact]
    public void Decode_FrequencyFrames_RecoverTxAndRxHz()
    {
        const long freq = 7_074_000;
        var state = BaseState(vfoHz: freq);
        // TxFreq + RxFreq both carry VfoAHz in ControlFrame (no split VFO).
        var packet = BuildPacket(ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq, in state);

        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        var events = dec.Decode(packet, host);

        Assert.Equal("TxFreq", events[0].CommandKind);
        Assert.Equal("RxFreq", events[1].CommandKind);
        Assert.Equal(freq, host.TxFreqHz);
        Assert.Equal(freq, host.RxFreqHz);
    }

    [Fact]
    public void Decode_DriveFilter_RecoversDriveMoxAndMicBits()
    {
        var state = BaseState(mox: true, drive: 200, micBoost: true, micLineIn: true);
        var packet = BuildPacket(ControlFrame.CcRegister.DriveFilter, ControlFrame.CcRegister.RxFreq, in state);

        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        var events = dec.Decode(packet, host);

        Assert.Equal("DriveFilter", events[0].CommandKind);
        Assert.Equal((byte)200, host.DriveByte);
        Assert.True(host.Mox);
        Assert.True(host.MicBoost);
        Assert.True(host.MicLineIn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(31)]
    public void Decode_Attenuator_RecoversStepDb(int db)
    {
        // ANAN-10E (HermesII) uses the standard 0x20 | (db & 0x1F) encoding.
        var state = BaseState(attenDb: db);
        var packet = BuildPacket(ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.RxFreq, in state);

        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        dec.Decode(packet, host);

        Assert.Equal(db, host.AttenuatorDb);
    }

    [Fact]
    public void Decode_StartCommand_SetsRunning()
    {
        var packet = new byte[64];
        ControlFrame.BuildStartStop(packet, start: true);

        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        var events = dec.Decode(packet, host);

        Assert.Single(events);
        Assert.Equal("Start", events[0].CommandKind);
        Assert.True(host.Running);
    }

    [Fact]
    public void Decode_StopCommand_ClearsRunning()
    {
        var dec = new Ep2Decoder();
        var host = new HostCommandState { Running = true };

        var startPkt = new byte[64];
        ControlFrame.BuildStartStop(startPkt, start: true);
        dec.Decode(startPkt, host);
        Assert.True(host.Running);

        var stopPkt = new byte[64];
        ControlFrame.BuildStartStop(stopPkt, start: false);
        var events = dec.Decode(stopPkt, host);

        Assert.Equal("Stop", events[0].CommandKind);
        Assert.False(host.Running);
    }

    [Fact]
    public void Decode_StartWithWideband_StillStarts()
    {
        var packet = new byte[64];
        ControlFrame.BuildStartStop(packet, start: true, includeWideband: true);

        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        var events = dec.Decode(packet, host);

        Assert.Equal("Start", events[0].CommandKind);
        Assert.True(host.Running);
        Assert.Contains("wideband=1", events[0].Summary);
    }

    [Fact]
    public void Decode_MoxBit_TracksAcrossKeyUpAndDown()
    {
        var dec = new Ep2Decoder();
        var host = new HostCommandState();

        var keyed = BuildPacket(ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq,
            BaseState(mox: true));
        dec.Decode(keyed, host);
        Assert.True(host.Mox);

        var unkeyed = BuildPacket(ControlFrame.CcRegister.TxFreq, ControlFrame.CcRegister.RxFreq,
            BaseState(mox: false));
        dec.Decode(unkeyed, host);
        Assert.False(host.Mox);
    }

    [Fact]
    public void Decode_UnrecognisedPacket_ReturnsEmpty()
    {
        var dec = new Ep2Decoder();
        var host = new HostCommandState();

        // Metis discovery-ish / unrelated header (0xEF 0xFE 0x02 …) is not an
        // EP2 data frame nor a start/stop command.
        var junk = new byte[64];
        junk[0] = 0xEF; junk[1] = 0xFE; junk[2] = 0x02;

        Assert.Empty(dec.Decode(junk, host));
    }

    [Fact]
    public void Decode_DataFrame_EmitsOnePerUsbFrame()
    {
        var packet = BuildPacket(ControlFrame.CcRegister.Config, ControlFrame.CcRegister.DriveFilter,
            BaseState(drive: 50));
        var dec = new Ep2Decoder();
        var host = new HostCommandState();
        var events = dec.Decode(packet, host);

        Assert.Equal(2, events.Count);
        Assert.Equal("Config", events[0].CommandKind);
        Assert.Equal("DriveFilter", events[1].CommandKind);
        Assert.All(events, e => Assert.Equal(ProtocolVersion.P1, e.Protocol));
    }
}
