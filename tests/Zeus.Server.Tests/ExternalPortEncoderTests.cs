// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Coverage + byte-identity tests for the external-port encoder seam
/// (external-ports plan, Phase 1).
///
/// 1. Every <see cref="HpsdrBoardKind"/> resolves to a non-null encoder.
/// 2. The encoders produce the SAME antenna bits as today's wire path for the
///    default (ANT1) state — these complement the golden-byte tests in the
///    protocol test projects, asserting the firewall is byte-identical.
/// </summary>
public class ExternalPortEncoderTests
{
    [Theory]
    [InlineData(HpsdrBoardKind.Metis)]
    [InlineData(HpsdrBoardKind.Hermes)]
    [InlineData(HpsdrBoardKind.HermesII)]
    [InlineData(HpsdrBoardKind.Angelia)]
    [InlineData(HpsdrBoardKind.Orion)]
    [InlineData(HpsdrBoardKind.HermesLite2)]
    [InlineData(HpsdrBoardKind.OrionMkII)]
    [InlineData(HpsdrBoardKind.HermesC10)]
    [InlineData(HpsdrBoardKind.Unknown)]
    public void For_ReturnsNonNullEncoder_ForEveryBoardKind(HpsdrBoardKind board)
    {
        var encoder = ExternalPortEncoders.For(board);
        Assert.NotNull(encoder);
        Assert.False(string.IsNullOrWhiteSpace(encoder.Label));
    }

    [Fact]
    public void For_ReturnsNonNullEncoder_ForEveryEnumeratedBoardKind()
    {
        foreach (HpsdrBoardKind board in Enum.GetValues<HpsdrBoardKind>())
        {
            Assert.NotNull(ExternalPortEncoders.For(board));
        }
    }

    [Theory]
    [InlineData(OrionMkIIVariant.G2)]
    [InlineData(OrionMkIIVariant.G2_1K)]
    [InlineData(OrionMkIIVariant.Anan7000DLE)]
    [InlineData(OrionMkIIVariant.Anan8000DLE)]
    [InlineData(OrionMkIIVariant.OrionMkII)]
    [InlineData(OrionMkIIVariant.AnvelinaPro3)]
    [InlineData(OrionMkIIVariant.RedPitaya)]
    public void For_0x0A_RoutesToProtocol2Encoder_ForEveryVariant(OrionMkIIVariant variant)
    {
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.OrionMkII, variant);
        Assert.IsType<Protocol2PortEncoder>(encoder);
    }

    [Fact]
    public void For_Hermes_RoutesToProtocol1Encoder()
        => Assert.IsType<Protocol1PortEncoder>(ExternalPortEncoders.For(HpsdrBoardKind.Hermes));

    [Fact]
    public void For_Hl2_RoutesToHl2Encoder()
        => Assert.IsType<HermesLite2PortEncoder>(ExternalPortEncoders.For(HpsdrBoardKind.HermesLite2));

    [Fact]
    public void DefaultProtocolFor_0x0A_IsProtocol2_OthersProtocol1()
    {
        Assert.Equal(RadioProtocol.Protocol2, ExternalPortEncoders.DefaultProtocolFor(HpsdrBoardKind.OrionMkII));
        Assert.Equal(RadioProtocol.Protocol1, ExternalPortEncoders.DefaultProtocolFor(HpsdrBoardKind.Hermes));
        Assert.Equal(RadioProtocol.Protocol1, ExternalPortEncoders.DefaultProtocolFor(HpsdrBoardKind.HermesLite2));
    }

    // ---- byte-identity: encoder output == wire path for the default state ----

    [Theory]
    [InlineData(HpsdrAntenna.Ant1, 0x00)]
    [InlineData(HpsdrAntenna.Ant2, 0x20)]
    [InlineData(HpsdrAntenna.Ant3, 0x40)]
    public void P1Encoder_RxAntennaC3Bits_MatchWirePath(HpsdrAntenna ant, byte expected)
    {
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.Hermes);
        byte bits = encoder.EncodeP1RxAntennaC3Bits(new ExternalPortState(RxAnt: ant));
        Assert.Equal(expected, bits);
    }

    [Theory]
    [InlineData(HpsdrAntenna.Ant1)]
    [InlineData(HpsdrAntenna.Ant2)]
    [InlineData(HpsdrAntenna.Ant3)]
    public void Hl2Encoder_RxAntennaC3Bits_ClampedToAnt1(HpsdrAntenna requested)
    {
        // Phase 2: HL2 has no RX-antenna relay — C3[7:5] is hard-clamped to
        // ANT1 (0x00) at the wire layer regardless of the requested antenna,
        // so a stale per-band ANT2/3 can never flip the N2ADR antenna pad.
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.HermesLite2);
        byte bits = encoder.EncodeP1RxAntennaC3Bits(new ExternalPortState(RxAnt: requested));
        Assert.Equal(0x00, bits);
    }

    [Theory]
    [InlineData(HpsdrAntenna.Ant1, 0x01000000u)]
    [InlineData(HpsdrAntenna.Ant2, 0x02000000u)]
    [InlineData(HpsdrAntenna.Ant3, 0x04000000u)]
    public void P2Encoder_TxAntennaBits_ThreadSelection_OnRelayBoard(HpsdrAntenna ant, uint expected)
    {
        // Phase 2: a relay-capable variant (G2 / OrionMkII) threads the real
        // per-band TX antenna into alex0[26:24].
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.OrionMkII);
        uint bits = encoder.EncodeP2TxAntennaBits(new ExternalPortState(TxAnt: ant));
        Assert.Equal(expected, bits);
    }

    // ---- TX-audio source: P2 byte-50/51 PURE FUNCTIONS (external-ports §2) ----

    [Theory]
    // Host: literal 0 regardless of any persisted params.
    [InlineData(TxAudioSource.Host, false, false, (byte)0x00)]
    [InlineData(TxAudioSource.Host, true, true, (byte)0x00)]
    // RadioMic: boost (bit1) + bias (bit4) from params, no line-in/XLR bits.
    [InlineData(TxAudioSource.RadioMic, false, false, (byte)0x00)]
    [InlineData(TxAudioSource.RadioMic, true, false, (byte)0x02)]
    [InlineData(TxAudioSource.RadioMic, false, true, (byte)0x10)]
    [InlineData(TxAudioSource.RadioMic, true, true, (byte)0x12)]
    // RadioLineIn: line-in select (bit0); mic params ignored.
    [InlineData(TxAudioSource.RadioLineIn, true, true, (byte)0x01)]
    // RadioBalancedXlr: XLR (bit5) | opt boost/bias.
    [InlineData(TxAudioSource.RadioBalancedXlr, false, false, (byte)0x20)]
    [InlineData(TxAudioSource.RadioBalancedXlr, true, true, (byte)0x32)]
    public void P2Encoder_MicControlByte_IsPureFunctionOfSource(
        TxAudioSource source, bool micBoost, bool micBias, byte expected)
    {
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.OrionMkII);
        var state = new ExternalPortState(Source: source, MicBoost: micBoost, MicBias: micBias);
        Assert.Equal(expected, encoder.EncodeP2MicControlByte(in state));
    }

    [Fact]
    public void P2Encoder_Host_ByteIsLiteralZero_DespitePersistedParams()
    {
        // PURITY: a stale non-zero MicBoost/MicBias/LineInGain must NOT leak onto
        // the wire after a revert to Host. Host returns literal 0 on every surface.
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.OrionMkII);
        var leaky = new ExternalPortState(
            Source: TxAudioSource.Host, MicBoost: true, MicBias: true, LineInGain: 31);
        Assert.Equal((byte)0x00, encoder.EncodeP2MicControlByte(in leaky));
        Assert.Equal((byte)0x00, encoder.EncodeP2LineInGainByte(in leaky));
    }

    [Theory]
    // Gain rides byte 51 ONLY for RadioLineIn; every other source returns 0.
    [InlineData(TxAudioSource.RadioLineIn, (byte)19, (byte)19)]
    [InlineData(TxAudioSource.RadioLineIn, (byte)63, (byte)31)] // 5-bit clamp
    [InlineData(TxAudioSource.RadioMic, (byte)19, (byte)0)]
    [InlineData(TxAudioSource.RadioBalancedXlr, (byte)19, (byte)0)]
    [InlineData(TxAudioSource.Host, (byte)19, (byte)0)]
    public void P2Encoder_LineInGainByte_OnlyForLineIn(
        TxAudioSource source, byte gain, byte expected)
    {
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.OrionMkII);
        var state = new ExternalPortState(Source: source, LineInGain: gain);
        Assert.Equal(expected, encoder.EncodeP2LineInGainByte(in state));
    }

    [Theory]
    // P1 codec 0x12 bits (mic_boost C2[0], mic_linein C2[1]). RadioMic → boost
    // from param; Host / everything else → all clear (no leak).
    [InlineData(TxAudioSource.Host, true, false, false)]
    [InlineData(TxAudioSource.RadioMic, false, false, false)]
    [InlineData(TxAudioSource.RadioMic, true, true, false)]
    public void P1Encoder_CodecAudioBits_IsPureFunctionOfSource(
        TxAudioSource source, bool micBoost, bool expBoost, bool expLineIn)
    {
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.Hermes);
        var state = new ExternalPortState(Source: source, MicBoost: micBoost);
        var (boost, lineIn) = encoder.EncodeP1CodecAudioBits(in state);
        Assert.Equal(expBoost, boost);
        Assert.Equal(expLineIn, lineIn);
    }

    [Fact]
    public void Hl2Encoder_AllAudioSurfaces_AreZero_HostOnly()
    {
        // HL2 is Host-only — no codec audio bits, no P2 bytes, ever.
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.HermesLite2);
        var hot = new ExternalPortState(
            Source: TxAudioSource.RadioMic, MicBoost: true, MicBias: true, LineInGain: 31);
        Assert.Equal((byte)0, encoder.EncodeP2MicControlByte(in hot));
        Assert.Equal((byte)0, encoder.EncodeP2LineInGainByte(in hot));
        var (boost, lineIn) = encoder.EncodeP1CodecAudioBits(in hot);
        Assert.False(boost);
        Assert.False(lineIn);
    }

    [Fact]
    public void P1Encoder_Anan10E_RadioLineIn_AssertsMicLineInSelect()
    {
        // ANAN-10E (HermesII, issue #667): RadioLineIn selects the TLV320 line-in
        // via mic_linein (0x12 frame C2[1]); the 0..31 gain rides the 0x14 frame.
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.HermesII);
        var state = new ExternalPortState(Source: TxAudioSource.RadioLineIn, LineInGain: 17);
        var (boost, lineIn) = encoder.EncodeP1CodecAudioBits(in state);
        Assert.False(boost);
        Assert.True(lineIn);
    }

    [Fact]
    public void P1Encoder_RadioLineIn_IsBoardGatedToAnan10E()
    {
        // The line-in select is 10E-ONLY: the SAME Protocol1PortEncoder on a pure
        // Hermes board must NOT assert mic_linein, so no other P1 codec board's
        // wire output changes.
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.Hermes);
        var state = new ExternalPortState(Source: TxAudioSource.RadioLineIn, LineInGain: 17);
        var (_, lineIn) = encoder.EncodeP1CodecAudioBits(in state);
        Assert.False(lineIn);
    }

    // ---- RadioService.ClampAudioSource against board capabilities (§5/§8) ----

    [Fact]
    public void Clamp_BalancedXlr_OnNonXlrBoard_FallsBackToHost()
    {
        // 7000DLE has line-in + bias but NO balanced XLR. A persisted XLR
        // selection (e.g. after a G2→7000DLE swap) must clamp to Host.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.Anan7000DLE);
        var got = RadioService.ClampAudioSource(
            new AudioSourceSelection(TxAudioSource.RadioBalancedXlr, true, true, 0), caps);
        Assert.Equal(TxAudioSource.Host, got.Source);
    }

    [Fact]
    public void Clamp_LineIn_OnBoardWithoutLineIn_FallsBackToHost()
    {
        // Hermes (pure P1 codec) has no RadioLineIn exposure in v1.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.Hermes);
        var got = RadioService.ClampAudioSource(
            new AudioSourceSelection(TxAudioSource.RadioLineIn, false, false, 12), caps);
        Assert.Equal(TxAudioSource.Host, got.Source);
    }

    [Fact]
    public void Clamp_LineIn_OnAnan10E_SurvivesWithGain()
    {
        // ANAN-10E (HermesII, issue #667) now exposes line-in: the persisted
        // selection survives the clamp and keeps its 0..31 gain; mic params drop.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.HermesII);
        var got = RadioService.ClampAudioSource(
            new AudioSourceSelection(TxAudioSource.RadioLineIn, MicBoost: true, MicBias: true, LineInGain: 21), caps);
        Assert.Equal(TxAudioSource.RadioLineIn, got.Source);
        Assert.Equal((byte)21, got.LineInGain);
        Assert.False(got.MicBoost);
        Assert.False(got.MicBias);
    }

    [Fact]
    public void Clamp_AnyRadioSource_OnHl2_FallsBackToHost()
    {
        // HL2 has the mic front-end (so not a 409) but no codec → Host-only.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.HermesLite2);
        foreach (var src in new[] { TxAudioSource.RadioMic, TxAudioSource.RadioLineIn, TxAudioSource.RadioBalancedXlr })
        {
            var got = RadioService.ClampAudioSource(
                new AudioSourceSelection(src, true, true, 9), caps);
            Assert.Equal(TxAudioSource.Host, got.Source);
        }
    }

    [Fact]
    public void Clamp_MicBias_DroppedOnNonBiasBoard()
    {
        // Red Pitaya has line-in but NOT mic bias. RadioMic survives; the bias
        // param is dropped so a stale bias bit can never reach the wire.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.RedPitaya);
        var got = RadioService.ClampAudioSource(
            new AudioSourceSelection(TxAudioSource.RadioMic, MicBoost: true, MicBias: true, LineInGain: 0), caps);
        Assert.Equal(TxAudioSource.RadioMic, got.Source);
        Assert.True(got.MicBoost);
        Assert.False(got.MicBias); // dropped — Red Pitaya has no mic bias
    }

    [Fact]
    public void Clamp_G2_KeepsAllSources()
    {
        // G2 (default OrionMkII variant) honours every radio source.
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII);
        Assert.Equal(TxAudioSource.RadioBalancedXlr,
            RadioService.ClampAudioSource(new AudioSourceSelection(TxAudioSource.RadioBalancedXlr, false, false, 0), caps).Source);
        Assert.Equal(TxAudioSource.RadioLineIn,
            RadioService.ClampAudioSource(new AudioSourceSelection(TxAudioSource.RadioLineIn, false, false, 7), caps).Source);
        var mic = RadioService.ClampAudioSource(new AudioSourceSelection(TxAudioSource.RadioMic, true, true, 0), caps);
        Assert.Equal(TxAudioSource.RadioMic, mic.Source);
        Assert.True(mic.MicBias); // G2 has mic bias
    }
}
