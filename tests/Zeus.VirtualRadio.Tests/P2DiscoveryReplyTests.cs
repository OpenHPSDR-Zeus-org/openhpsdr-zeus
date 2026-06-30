// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using System.Net.NetworkInformation;
using Zeus.Contracts;
using Zeus.Protocol2.Discovery;
using Zeus.VirtualRadio.Discovery;
using Zeus.VirtualRadio.P2;

namespace Zeus.VirtualRadio.Tests;

/// <summary>
/// Anti-drift round-trip tests for the Protocol-2 discovery reply: the
/// emulator's reply is fed straight through the REAL
/// <see cref="ReplyParser"/> and must round-trip back to the same board /
/// MAC / status. Socketless.
/// </summary>
public class P2DiscoveryReplyTests
{
    private static VirtualRadioProfile HermesII() =>
        VirtualRadioProfile.Create(HpsdrBoardKind.HermesII, ProtocolVersion.P2);

    [Fact]
    public void Build_RoundTripsThroughReplyParser_AsHermesII()
    {
        var mac = DiscoveryResponder.DefaultMac;
        Span<byte> buf = stackalloc byte[P2DiscoveryReply.ReplyLength];

        int n = P2DiscoveryReply.Build(buf, HermesII(), mac, codeVersion: 73, numReceivers: 2, busy: false);

        Assert.Equal(P2DiscoveryReply.ReplyLength, n);
        Assert.True(ReplyParser.TryParse(buf[..n].ToArray(), IPAddress.Parse("192.168.1.50"), out var radio));
        Assert.NotNull(radio);
        Assert.Equal(HpsdrBoardKind.HermesII, radio!.Board);
        Assert.Equal(mac, radio.Mac);
        Assert.Equal((byte)73, radio.FirmwareVersion);
        Assert.False(radio.Details.Busy);
        Assert.Equal((byte)0x02, radio.Details.RawBoardId);
        Assert.Equal((byte)2, radio.Details.NumReceivers);
    }

    [Fact]
    public void Build_BusyFlag_IsReportedByParser()
    {
        Span<byte> buf = stackalloc byte[P2DiscoveryReply.ReplyLength];
        P2DiscoveryReply.Build(buf, HermesII(), DiscoveryResponder.DefaultMac, 73, 2, busy: true);

        Assert.True(ReplyParser.TryParse(buf.ToArray(), IPAddress.Loopback, out var radio));
        Assert.True(radio!.Details.Busy);
    }

    [Fact]
    public void Build_ClampsNumReceiversToAtLeastTwo()
    {
        Span<byte> buf = stackalloc byte[P2DiscoveryReply.ReplyLength];
        P2DiscoveryReply.Build(buf, HermesII(), DiscoveryResponder.DefaultMac, 73, numReceivers: 0, busy: false);

        ReplyParser.TryParse(buf.ToArray(), IPAddress.Loopback, out var radio);
        Assert.True(radio!.Details.NumReceivers >= 2);
    }

    [Fact]
    public void Build_PreservesMacBytes()
    {
        var mac = new PhysicalAddress(new byte[] { 0x02, 0x11, 0x22, 0x33, 0x44, 0x55 });
        Span<byte> buf = stackalloc byte[P2DiscoveryReply.ReplyLength];
        P2DiscoveryReply.Build(buf, HermesII(), mac, 73, 2, busy: false);

        ReplyParser.TryParse(buf.ToArray(), IPAddress.Loopback, out var radio);
        Assert.Equal(mac, radio!.Mac);
    }

    [Theory]
    [InlineData(HpsdrBoardKind.Metis, 0x00)]
    [InlineData(HpsdrBoardKind.Hermes, 0x01)]
    [InlineData(HpsdrBoardKind.HermesII, 0x02)]
    [InlineData(HpsdrBoardKind.HermesLite2, 0x06)]
    [InlineData(HpsdrBoardKind.HermesC10, 0x14)]
    public void BoardByte_RoundTripsThroughReplyParserMap(HpsdrBoardKind board, int expected)
    {
        Assert.Equal((byte)expected, P2DiscoveryReply.BoardByte(board));
    }

    [Fact]
    public void IsDiscoveryProbe_DiscriminatesOnByte4()
    {
        // Discovery probe (RadioDiscoveryService): zero seq header + 0x02 at [4].
        var probe = new byte[P2Wire.DiscoveryPacketLength];
        probe[4] = 0x02;
        Assert.True(P2DiscoveryReply.IsDiscoveryProbe(probe));

        // CmdGeneral to the same port carries 0x00 there.
        var general = new byte[P2Wire.CmdSmallLength];
        general[4] = 0x00;
        Assert.False(P2DiscoveryReply.IsDiscoveryProbe(general));
    }

    [Fact]
    public void Build_ThrowsOnTooSmallBuffer()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            byte[] tiny = new byte[10];
            P2DiscoveryReply.Build(tiny, HermesII(), DiscoveryResponder.DefaultMac, 73, 2, false);
        });
    }
}
