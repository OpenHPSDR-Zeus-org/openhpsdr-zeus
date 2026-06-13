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

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using Zeus.Contracts;

namespace Zeus.Protocol1.Discovery;

public static class ReplyParser
{
    public const int MinimumReplyLength = 24;
    public const byte StatusIdle = 0x02;
    public const byte StatusBusy = 0x03;

    private const byte Hl2FixedIpBit = 0x80;
    private const byte Hl2DhcpOverrideBit = 0x20;
    private const byte Hl2MacModifiedBit = 0x40;
    private const byte HermesLite2CodeVersionThreshold = 40;

    public static bool TryParse(
        ReadOnlySpan<byte> raw,
        IPAddress fromIp,
        [NotNullWhen(true)] out DiscoveredRadio? radio)
    {
        radio = null;
        if (raw.Length < MinimumReplyLength) return false;
        if (raw[0] != 0xEF || raw[1] != 0xFE) return false;

        var status = raw[2];
        if (status != StatusIdle && status != StatusBusy) return false;

        var macBytes = raw.Slice(3, 6).ToArray();
        var mac = new PhysicalAddress(macBytes);

        var codeVersion = raw[9];
        var rawBoardId = raw[10];
        var hl2Flags = raw[11];
        var gatewareBuild = raw[19];
        var hl2Minor = raw[21];

        var board = MapBoard(rawBoardId);
        var isHl2 = board == HpsdrBoardKind.HermesLite2 && codeVersion >= HermesLite2CodeVersionThreshold;

        // Wire byte 0x06 is shared by genuine Hermes Lite 2 units AND by the
        // Hermes-II / ANAN-10E firmware family (e.g. Hermes10E_v1.5.rbf).
        // Genuine HL2 units always report code versions >= HermesLite2CodeVersionThreshold (40).
        // A board that sent 0x06 with a version below that threshold is a Hermes-II / ANAN-10E;
        // reclassify so downstream drive-model and calibration dispatch select the correct
        // 8-bit full-byte profile instead of HL2's 4-bit nibble/percentage model.
        // Issue #493: ANAN-10E with Hermes10E_v1.5.rbf sends byte 0x06, version 15.
        if (!isHl2 && board == HpsdrBoardKind.HermesLite2)
            board = HpsdrBoardKind.HermesII;

        var fixedIp = isHl2 && (hl2Flags & Hl2FixedIpBit) != 0;
        var fixedIpOverridesDhcp = isHl2 && (hl2Flags & (Hl2FixedIpBit | Hl2DhcpOverrideBit)) == (Hl2FixedIpBit | Hl2DhcpOverrideBit);
        var macModified = isHl2 && (hl2Flags & Hl2MacModifiedBit) != 0;

        IPAddress? overrideIp = null;
        if (fixedIp)
        {
            overrideIp = new IPAddress(raw.Slice(13, 4).ToArray());
        }

        var firmwareString = FormatFirmware(board, codeVersion, hl2Minor, isHl2);

        var details = new DiscoveryDetails(
            RawReply: raw.ToArray(),
            RawBoardId: rawBoardId,
            Busy: status == StatusBusy,
            FixedIpEnabled: fixedIp,
            FixedIpOverridesDhcp: fixedIpOverridesDhcp,
            MacAddressModified: macModified,
            FixedIpAddress: overrideIp,
            GatewareBuild: gatewareBuild,
            HermesLite2MinorVersion: isHl2 ? hl2Minor : null);

        radio = new DiscoveredRadio(
            Ip: fromIp,
            Mac: mac,
            Board: board,
            FirmwareVersion: codeVersion,
            FirmwareString: firmwareString,
            Details: details);
        return true;
    }

    private static HpsdrBoardKind MapBoard(byte raw) => raw switch
    {
        0x00 => HpsdrBoardKind.Metis,
        0x01 => HpsdrBoardKind.Hermes,
        0x02 => HpsdrBoardKind.HermesII,
        0x04 => HpsdrBoardKind.Angelia,
        0x05 => HpsdrBoardKind.Orion,
        0x06 => HpsdrBoardKind.HermesLite2,
        0x0A => HpsdrBoardKind.OrionMkII,
        0x14 => HpsdrBoardKind.HermesC10,
        _ => HpsdrBoardKind.Unknown,
    };

    private static string FormatFirmware(HpsdrBoardKind board, byte codeVersion, byte hl2Minor, bool isHl2)
    {
        if (isHl2) return $"{codeVersion}.{hl2Minor}";
        return $"{codeVersion / 10}.{codeVersion % 10}";
    }
}
