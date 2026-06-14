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
}
