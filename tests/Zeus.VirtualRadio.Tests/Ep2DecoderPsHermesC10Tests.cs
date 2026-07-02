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
/// HermesC10 (ANAN-G2E) P1 PureSignal — socketless composer→decoder
/// round-trips, on the <see cref="Ep2DecoderRoundTripTests"/> pattern and
/// the #1249 de-vacuuming lesson: packets are built with the REAL
/// <c>Zeus.Protocol1.ControlFrame</c> encoder, decoded by the emulator's
/// <see cref="Ep2Decoder"/>, and the recovered <see cref="HostCommandState"/>
/// pins the three G2E PS wire behaviours end-to-end — the RX BYPASS relay
/// route (Config C3[6:5]), the receiver-mux enable (0x14 C2[6]) and the
/// TX-time ADC attenuation (0x1c C3[4:0]). Reverting any of the feature
/// branches in ControlFrame turns a positive assertion here red; the non-C10
/// cases lock the other boards' decode to today's values.
/// </summary>
public class Ep2DecoderPsHermesC10Tests
{
    private static ControlFrame.CcState PsState(
        HpsdrBoardKind board,
        bool psEnabled = true,
        bool mox = true,
        HpsdrAntenna rxAntenna = HpsdrAntenna.Ant3,
        int psTxAttnOnTxDb = int.MinValue) =>
        new ControlFrame.CcState(
            VfoAHz: 14_200_000,
            Rate: HpsdrSampleRate.Rate48k,
            PreampOn: false,
            Atten: HpsdrAtten.Zero,
            RxAntenna: rxAntenna,
            Mox: mox,
            EnableHl2BandVolts: false,
            Board: board,
            PsEnabled: psEnabled,
            PsTxAttnOnTxDb: psTxAttnOnTxDb);

    private static HostCommandState Decode(
        ControlFrame.CcRegister even, ControlFrame.CcRegister odd, in ControlFrame.CcState state)
    {
        var packet = new byte[ControlFrame.PacketLength];
        ControlFrame.BuildDataPacket(packet, sendSequence: 1, even, odd, in state);
        var host = new HostCommandState();
        new Ep2Decoder().Decode(packet, host);
        return host;
    }

    // ---- Config C3[6:5] — RX BYPASS routing --------------------------------

    [Fact]
    public void Config_C10_ArmedAndKeyed_RoutesRxBypassRelay()
    {
        // Armed + keyed → relay code 0b01 = RX BYPASS OUT (Mk2PA Alex SPI
        // bit 11), regardless of the operator's Ant3 selection.
        var host = Decode(ControlFrame.CcRegister.Config, ControlFrame.CcRegister.TxFreq,
            PsState(HpsdrBoardKind.HermesC10));
        Assert.True(host.Mox);
        Assert.Equal(0b01, host.P1RxRelayCode);
    }

    [Fact]
    public void Config_C10_ArmedAtRest_RestoresOperatorAntenna()
    {
        // The unkey Config re-send is what parks the bypass relay (the
        // gateware has no PTT term on it). Ant3 decodes as relay code 0b10 —
        // deliberately distinct from the bypass 0b01.
        var host = Decode(ControlFrame.CcRegister.Config, ControlFrame.CcRegister.TxFreq,
            PsState(HpsdrBoardKind.HermesC10, mox: false));
        Assert.False(host.Mox);
        Assert.Equal(0b10, host.P1RxRelayCode);
    }

    [Fact]
    public void Config_C10_KeyedPsOff_KeepsOperatorAntenna()
    {
        var host = Decode(ControlFrame.CcRegister.Config, ControlFrame.CcRegister.TxFreq,
            PsState(HpsdrBoardKind.HermesC10, psEnabled: false));
        Assert.Equal(0b10, host.P1RxRelayCode);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    public void Config_OtherBoards_ArmedAndKeyed_NeverRouteBypass(HpsdrBoardKind board)
    {
        // The override is HermesC10-only: every other board keeps the
        // operator antenna on the wire even with PS armed + keyed.
        var host = Decode(ControlFrame.CcRegister.Config, ControlFrame.CcRegister.TxFreq,
            PsState(board));
        Assert.Equal(0b10, host.P1RxRelayCode);
    }

    // ---- 0x14 C2[6] — PureSignal receiver-mux enable -----------------------

    [Theory]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.HermesLite2)]   // locks the existing HL2 leg too
    public void Attenuator_PsBoards_Armed_SetPsRun(HpsdrBoardKind board)
    {
        var host = Decode(ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.TxFreq,
            PsState(board, mox: false));
        Assert.True(host.P1PsRun);
    }

    [Fact]
    public void Attenuator_C10_Disarmed_ClearsPsRun()
    {
        var host = Decode(ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.TxFreq,
            PsState(HpsdrBoardKind.HermesC10, psEnabled: false));
        Assert.False(host.P1PsRun);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Orion)]
    public void Attenuator_OtherBoards_Armed_NeverSetPsRun(HpsdrBoardKind board)
    {
        var host = Decode(ControlFrame.CcRegister.Attenuator, ControlFrame.CcRegister.TxFreq,
            PsState(board));
        Assert.False(host.P1PsRun);
    }

    // ---- 0x1c C3[4:0] — atten_on_Tx ----------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(17, 17)]
    [InlineData(31, 31)]
    public void LnaTxGainStable_C10_CarriesOperatorAttenOnTx(int db, byte expected)
    {
        var host = Decode(ControlFrame.CcRegister.LnaTxGainStable, ControlFrame.CcRegister.TxFreq,
            PsState(HpsdrBoardKind.HermesC10, psTxAttnOnTxDb: db));
        Assert.Equal(expected, host.P1AttenOnTxDb);
    }

    [Fact]
    public void LnaTxGainStable_C10_SentinelUnset_Carries31()
    {
        // "Operator never set a value" → the wire carries 31, the silicon
        // reset default — the honest no-op that starves nothing and clips
        // nothing. A regression to 0 dB here is the ADC-clip extreme.
        var host = Decode(ControlFrame.CcRegister.LnaTxGainStable, ControlFrame.CcRegister.TxFreq,
            PsState(HpsdrBoardKind.HermesC10));
        Assert.Equal(31, host.P1AttenOnTxDb);
    }

    [Fact]
    public void LnaTxGainStable_Hl2_StaysAllZero()
    {
        // HL2's 0x1c is the AD9866 FAST_LNA block; Zeus keeps its payload
        // all-zero (en_tx_gain=0, the PGA-stability write). A stale
        // PsTxAttnOnTxDb must never leak into the HL2 frame.
        var host = Decode(ControlFrame.CcRegister.LnaTxGainStable, ControlFrame.CcRegister.TxFreq,
            PsState(HpsdrBoardKind.HermesLite2, psTxAttnOnTxDb: 17));
        Assert.Equal(0, host.P1AttenOnTxDb);
    }
}
