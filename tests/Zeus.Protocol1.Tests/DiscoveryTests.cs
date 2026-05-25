// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

public class DiscoveryTests
{
    private static readonly IPAddress FromIp = IPAddress.Parse("192.168.1.42");

    [Fact]
    public void Parses_HermesLite2_Reply()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x1C, 0xC0, 0xDE, 0xCA, 0xFE },
            CodeVersion: 73,
            BoardId: 0x06,
            Hl2Flags: 0x00,
            GatewareBuild: 19,
            Hl2Minor: 2));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.HermesLite2, radio.Board);
        Assert.Equal(new PhysicalAddress(new byte[] { 0x00, 0x1C, 0xC0, 0xDE, 0xCA, 0xFE }), radio.Mac);
        Assert.Equal(FromIp, radio.Ip);
        Assert.Equal((byte)73, radio.FirmwareVersion);
        Assert.Equal("73.2", radio.FirmwareString);
        Assert.Equal((byte?)2, radio.Details.HermesLite2MinorVersion);
        Assert.False(radio.Details.FixedIpEnabled);
        Assert.False(radio.Details.Busy);
    }

    [Fact]
    public void Parses_Hermes_Reply()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 },
            CodeVersion: 31,
            BoardId: 0x01,
            GatewareBuild: 7));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.Hermes, radio.Board);
        Assert.Equal("3.1", radio.FirmwareString);
        Assert.Null(radio.Details.HermesLite2MinorVersion);
        Assert.False(radio.Details.FixedIpEnabled);
        Assert.Equal((byte)0x01, radio.Details.RawBoardId);
    }

    [Theory]
    [InlineData((byte)0x00, HpsdrBoardKind.Metis, (byte)31)]      // original HPSDR Mercury+Penelope+Metis
    [InlineData((byte)0x01, HpsdrBoardKind.Hermes, (byte)31)]
    [InlineData((byte)0x02, HpsdrBoardKind.HermesII, (byte)31)]    // ANAN-10E / 100B / Hermes-II firmware
    [InlineData((byte)0x04, HpsdrBoardKind.Angelia, (byte)31)]    // ANAN-100D
    [InlineData((byte)0x05, HpsdrBoardKind.Orion, (byte)31)]      // ANAN-200D
    [InlineData((byte)0x06, HpsdrBoardKind.HermesLite2, (byte)73)] // version >= 40 -> genuine HL2
    [InlineData((byte)0x0A, HpsdrBoardKind.OrionMkII, (byte)31)]  // 0x0A alias family — see issue #218
    [InlineData((byte)0x14, HpsdrBoardKind.HermesC10, (byte)31)]  // ANAN-G2E (N1GP firmware)
    public void Maps_Every_Recognised_WireByte_To_BoardKind(byte boardId, HpsdrBoardKind expected, byte codeVersion)
    {
        // Pin every wire byte that ramdor/Thetis (MW0LGE) recognises in
        // HPSDRHW (enums.cs:389-402) so a regression in MapBoard cannot
        // silently land. Cross-references docs/references/protocol-1/thetis-board-matrix.md.
        // codeVersion is parameterised: 0x06 needs version >= 40 to stay classified as
        // HermesLite2 after the issue #493 reclassification guard.
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            CodeVersion: codeVersion,
            BoardId: boardId,
            GatewareBuild: 0));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(expected, radio.Board);
        Assert.Equal(boardId, radio.Details.RawBoardId);
    }

    [Fact]
    public void Parses_HermesC10_Reply_AnanG2E()
    {
        // Apache Labs ANAN-G2E reports board id 0x14 (Thetis HPSDRHW.HermesC10).
        // Before this mapping landed, a G2E discovery fell through to
        // HpsdrBoardKind.Unknown and skipped per-board PA / calibration dispatch.
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x55, 0x66, 0x77, 0x88, 0x99 },
            CodeVersion: 71,
            BoardId: 0x14,
            GatewareBuild: 11));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.HermesC10, radio.Board);
        Assert.Equal((byte)0x14, radio.Details.RawBoardId);
        Assert.Equal("7.1", radio.FirmwareString);
        Assert.Null(radio.Details.HermesLite2MinorVersion);
    }

    [Fact]
    public void Parses_Anan10_Or_Orion_Reply()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE },
            CodeVersion: 42,
            BoardId: 0x05,
            GatewareBuild: 11));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.Orion, radio.Board);
        Assert.Equal("4.2", radio.FirmwareString);
        Assert.False(radio.Details.Busy);
    }

    [Fact]
    public void Parses_Busy_Reply_SetsBusyFlag()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x03,
            Mac: new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 },
            CodeVersion: 31,
            BoardId: 0x01));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.True(radio.Details.Busy);
    }

    [Fact]
    public void Rejects_MalformedReply_ShortLength()
    {
        var buf = new byte[10];
        buf[0] = 0xEF;
        buf[1] = 0xFE;
        buf[2] = 0x02;

        Assert.False(ReplyParser.TryParse(buf, FromIp, out var radio));
        Assert.Null(radio);
    }

    [Fact]
    public void Rejects_BadSyncBytes()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 1, 2, 3, 4, 5, 6 },
            CodeVersion: 31,
            BoardId: 0x01));
        reply[0] = 0x00;

        Assert.False(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Null(radio);
    }

    [Fact]
    public void Rejects_BadStatusByte()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x99,
            Mac: new byte[] { 1, 2, 3, 4, 5, 6 },
            CodeVersion: 31,
            BoardId: 0x01));

        Assert.False(ReplyParser.TryParse(reply, FromIp, out _));
    }


    [Fact]
    public void WireByte_0x06_BelowHl2CodeThreshold_ReclassifiesAsHermesII()
    {
        // Regression for issue #493: ANAN-10E with Hermes10E_v1.5.rbf firmware
        // reports board byte 0x06 (same as Hermes Lite 2) but code version 15,
        // which is well below the HL2 threshold (40). The parser must reclassify
        // to HermesII so downstream drive-model / calibration dispatch selects the
        // correct 8-bit full-byte profile instead of HL2's nibble/percentage model.
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0xAA, 0xBB, 0xCC, 0x00, 0x01, 0x02 },
            CodeVersion: 15,       // Hermes10E_v1.5.rbf -> version 15
            BoardId: 0x06,
            GatewareBuild: 0));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.HermesII, radio.Board);
        Assert.Equal((byte)0x06, radio.Details.RawBoardId);         // raw wire byte preserved
        Assert.Equal("1.5", radio.FirmwareString);                  // version formatted as X.Y
        Assert.Null(radio.Details.HermesLite2MinorVersion);          // no HL2-specific extras
    }

    [Fact]
    public void Dedupes_Identical_Replies()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 },
            CodeVersion: 31,
            BoardId: 0x01));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var first));
        Assert.True(ReplyParser.TryParse(reply, FromIp, out var second));

        var byMac = new Dictionary<PhysicalAddress, DiscoveredRadio>
        {
            [first.Mac] = first,
            [second.Mac] = second,
        };

        Assert.Single(byMac);
        Assert.Equal(first.Mac, second.Mac);
    }

    [Fact]
    public void Parses_HL2_WithFixedIpFlag()
    {
        var reply = BuildReply(new ReplyFields(
            Status: 0x02,
            Mac: new byte[] { 0x00, 0x1C, 0xC0, 0xAA, 0xBB, 0xCC },
            CodeVersion: 73,
            BoardId: 0x06,
            Hl2Flags: 0xA0,
            FixedIpBytes: new byte[] { 192, 168, 44, 50 },
            Hl2MacOverride: new byte[] { 0xDE, 0xAD },
            GatewareBuild: 19,
            Hl2Minor: 2));

        Assert.True(ReplyParser.TryParse(reply, FromIp, out var radio));
        Assert.Equal(HpsdrBoardKind.HermesLite2, radio.Board);
        Assert.True(radio.Details.FixedIpEnabled);
        Assert.True(radio.Details.FixedIpOverridesDhcp);
        Assert.Equal(IPAddress.Parse("192.168.44.50"), radio.Details.FixedIpAddress);
        Assert.Equal("73.2", radio.FirmwareString);
        Assert.Equal((byte?)2, radio.Details.HermesLite2MinorVersion);
        Assert.Equal(FromIp, radio.Ip);
    }

    private sealed record ReplyFields(
        byte Status,
        byte[] Mac,
        byte CodeVersion,
        byte BoardId,
        byte Hl2Flags = 0x00,
        byte[]? FixedIpBytes = null,
        byte[]? Hl2MacOverride = null,
        byte GatewareBuild = 0,
        byte Hl2Minor = 0);

    private static byte[] BuildReply(ReplyFields f)
    {
        var buf = new byte[60];
        buf[0] = 0xEF;
        buf[1] = 0xFE;
        buf[2] = f.Status;
        Array.Copy(f.Mac, 0, buf, 3, 6);
        buf[9] = f.CodeVersion;
        buf[10] = f.BoardId;
        buf[11] = f.Hl2Flags;
        buf[12] = 0;
        if (f.FixedIpBytes is { } ip) Array.Copy(ip, 0, buf, 13, 4);
        if (f.Hl2MacOverride is { } mm) Array.Copy(mm, 0, buf, 17, 2);
        buf[19] = f.GatewareBuild;
        buf[20] = 1;
        buf[21] = f.Hl2Minor;
        return buf;
    }
}
