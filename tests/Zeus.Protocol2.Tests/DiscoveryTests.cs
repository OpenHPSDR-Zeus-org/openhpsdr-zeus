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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Net;
using System.Net.NetworkInformation;
using Zeus.Contracts;
using Zeus.Protocol2.Discovery;

namespace Zeus.Protocol2.Tests;

public class DiscoveryTests
{
    private static readonly IPAddress FromIp = IPAddress.Parse("192.168.1.42");

    [Fact]
    public void Parses_OrionMkII_Reply()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x1C, 0xC0, 0xDE, 0xCA, 0xFE },
            BoardId: 0x0A,
            ProtocolSupported: 38,
            CodeVersion: 21,
            NumReceivers: 2));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.OrionMkII, radio.Board);
        Assert.Equal(new PhysicalAddress(new byte[] { 0x00, 0x1C, 0xC0, 0xDE, 0xCA, 0xFE }), radio.Mac);
        Assert.Equal(FromIp, radio.Ip);
        Assert.Equal((byte)21, radio.FirmwareVersion);
        Assert.Equal("2.1", radio.FirmwareString);
        Assert.Equal((byte)38, radio.Details.ProtocolSupported);
        Assert.Equal((byte)2, radio.Details.NumReceivers);
        Assert.False(radio.Details.Busy);
    }

    [Fact]
    public void Parses_Anan200D_Orion_Reply()
    {
        // ANAN-200D on Protocol 2 sends device byte 0x04 (NOT 0x05 — that is
        // OrionMkII on the P2 wire). Regression guard for issue #780, where the
        // P1 numbering (Orion=0x05) was wrongly applied to P2 and a real 200D
        // mis-detected as Angelia (ANAN-100D).
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 },
            BoardId: 0x04,
            ProtocolSupported: 36,
            CodeVersion: 19,
            NumReceivers: 1));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.Orion, radio.Board);
        Assert.Equal((byte)0x04, radio.Details.RawBoardId);
    }

    [Fact]
    public void Parses_Hermes_Reply()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE },
            BoardId: 0x01,
            CodeVersion: 31));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.Hermes, radio.Board);
        Assert.Equal("3.1", radio.FirmwareString);
    }

    [Theory]
    [InlineData((byte)0x00, HpsdrBoardKind.Metis)]
    [InlineData((byte)0x01, HpsdrBoardKind.Hermes)]
    [InlineData((byte)0x02, HpsdrBoardKind.HermesII)]
    [InlineData((byte)0x03, HpsdrBoardKind.Angelia)]     // ANAN-100D (P2 wire — issue #780)
    [InlineData((byte)0x04, HpsdrBoardKind.Orion)]       // ANAN-200D (P2 wire — issue #780)
    [InlineData((byte)0x05, HpsdrBoardKind.OrionMkII)]   // ANAN-7000DLE/8000DLE (P2 wire)
    [InlineData((byte)0x06, HpsdrBoardKind.HermesLite2)]
    [InlineData((byte)0x0A, HpsdrBoardKind.OrionMkII)]   // Saturn / ANAN-G2
    [InlineData((byte)0x14, HpsdrBoardKind.HermesC10)]
    public void Maps_Every_Recognised_WireByte_To_BoardKind(byte boardId, HpsdrBoardKind expected)
    {
        // P2 counterpart to the P1 exhaustive-mapping test. The dual-ADC family
        // uses DIFFERENT wire bytes on P2 vs P1 (issue #780): on P2,
        // 0x03=Angelia(100D), 0x04=Orion(200D), 0x05=OrionMkII; on P1 those are
        // 0x04/0x05/0x0A. 0x00 names "Atlas" on P2 vs "Metis" on P1 — same wire
        // byte, different historical labelling.
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            BoardId: boardId,
            ProtocolSupported: 38,
            CodeVersion: 21));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(expected, radio.Board);
        Assert.Equal(boardId, radio.Details.RawBoardId);
    }

    [Fact]
    public void Parses_HermesC10_Reply_AnanG2E()
    {
        // Apache Labs ANAN-G2E reports board id 0x14 on P2 discovery
        // (Thetis HPSDRHW.HermesC10 = 20). Before this mapping landed, G2E
        // fell through to HpsdrBoardKind.Unknown.
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x55, 0x66, 0x77, 0x88, 0x99 },
            BoardId: 0x14,
            ProtocolSupported: 38,
            CodeVersion: 71,
            NumReceivers: 1));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.HermesC10, radio.Board);
        Assert.Equal((byte)0x14, radio.Details.RawBoardId);
        Assert.Equal("7.1", radio.FirmwareString);
    }

    [Fact]
    public void Detects_Busy_Status()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x03,
            Mac: new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 },
            BoardId: 0x0A));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.True(radio.Details.Busy);
    }

    [Fact]
    public void Formats_BetaVersion_When_NonZero()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 },
            BoardId: 0x0A,
            CodeVersion: 21,
            BetaVersion: 3));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal("2.1b3", radio.FirmwareString);
        Assert.Equal((byte)3, radio.Details.BetaVersion);
    }

    [Fact]
    public void Rejects_P1_Reply()
    {
        var p1 = new byte[24];
        p1[0] = 0xEF;
        p1[1] = 0xFE;
        p1[2] = 0x02;

        Assert.False(ReplyParser.TryParse(p1, FromIp, out _));
    }

    [Fact]
    public void Rejects_ShortPacket()
    {
        var tiny = new byte[10];
        Assert.False(ReplyParser.TryParse(tiny, FromIp, out _));
    }

    [Fact]
    public void Rejects_InvalidStatus()
    {
        var bad = new byte[24];
        bad[4] = 0x99;

        Assert.False(ReplyParser.TryParse(bad, FromIp, out _));
    }

    [Fact]
    public void Maps_UnknownBoardId_To_Unknown()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 },
            BoardId: 0x7F));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.Unknown, radio.Board);
        Assert.Equal((byte)0x7F, radio.Details.RawBoardId);
    }

    private sealed record ReplyFields(
        byte Status,
        byte[] Mac,
        byte BoardId,
        byte ProtocolSupported = 0,
        byte CodeVersion = 0,
        byte NumReceivers = 1,
        byte BetaVersion = 0,
        byte MercuryVersion0 = 0,
        byte MercuryVersion1 = 0,
        byte MercuryVersion2 = 0,
        byte MercuryVersion3 = 0,
        byte PennyVersion = 0,
        byte MetisVersion = 0);

    private static byte[] BuildReply(ReplyFields f)
    {
        var reply = new byte[24];
        reply[0] = 0x00;
        reply[1] = 0x00;
        reply[2] = 0x00;
        reply[3] = 0x00;
        reply[4] = f.Status;
        Array.Copy(f.Mac, 0, reply, 5, 6);
        reply[11] = f.BoardId;
        reply[12] = f.ProtocolSupported;
        reply[13] = f.CodeVersion;
        reply[14] = f.MercuryVersion0;
        reply[15] = f.MercuryVersion1;
        reply[16] = f.MercuryVersion2;
        reply[17] = f.MercuryVersion3;
        reply[18] = f.PennyVersion;
        reply[19] = f.MetisVersion;
        reply[20] = f.NumReceivers;
        reply[23] = f.BetaVersion;
        return reply;
    }
}
