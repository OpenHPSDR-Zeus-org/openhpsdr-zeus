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

    [Fact]
    public void Hl2Encoder_RxAntennaC3Bits_StillRawInPhase1()
    {
        // Phase 1 is byte-identical: HL2 still emits the raw C3[7:5] value
        // (the Phase-2 clamp to ANT1 is inert). This pins that the clamp has
        // NOT activated early — if it had, Ant2 would encode as 0x00.
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.HermesLite2);
        byte bits = encoder.EncodeP1RxAntennaC3Bits(new ExternalPortState(RxAnt: HpsdrAntenna.Ant2));
        Assert.Equal(0x20, bits);
    }

    [Fact]
    public void P2Encoder_TxAntennaBits_AreAnt1InPhase1()
    {
        // Phase 1 keeps the hardcoded ANT1 (0x01000000) regardless of the
        // desired TxAnt — the real per-band thread-through is Phase 2.
        var encoder = ExternalPortEncoders.For(HpsdrBoardKind.OrionMkII);
        uint bits = encoder.EncodeP2TxAntennaBits(new ExternalPortState(TxAnt: HpsdrAntenna.Ant3));
        Assert.Equal(0x01000000u, bits);
    }
}
