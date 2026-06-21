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

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using Zeus.Contracts;

namespace Zeus.Protocol2.Discovery;

public static class ReplyParser
{
    public const int MinimumReplyLength = 24;
    public const byte StatusIdle = 0x02;
    public const byte StatusBusy = 0x03;

    public static bool TryParse(
        ReadOnlySpan<byte> raw,
        IPAddress fromIp,
        [NotNullWhen(true)] out DiscoveredRadio? radio)
    {
        radio = null;
        if (raw.Length < MinimumReplyLength) return false;
        if (raw[0] != 0x00 || raw[1] != 0x00 || raw[2] != 0x00 || raw[3] != 0x00) return false;

        var status = raw[4];
        if (status != StatusIdle && status != StatusBusy) return false;

        var macBytes = raw.Slice(5, 6).ToArray();
        var mac = new PhysicalAddress(macBytes);

        var rawBoardId = raw[11];
        var protocolSupported = raw[12];
        var codeVersion = raw[13];
        var mercuryVersion0 = raw[14];
        var mercuryVersion1 = raw[15];
        var mercuryVersion2 = raw[16];
        var mercuryVersion3 = raw[17];
        var pennyVersion = raw[18];
        var metisVersion = raw[19];
        var numReceivers = raw[20];
        var betaVersion = raw.Length > 23 ? raw[23] : (byte)0;

        var board = MapBoard(rawBoardId);
        var firmwareString = FormatFirmware(codeVersion, betaVersion);

        var details = new DiscoveryDetails(
            RawReply: raw.ToArray(),
            RawBoardId: rawBoardId,
            Busy: status == StatusBusy,
            ProtocolSupported: protocolSupported,
            NumReceivers: numReceivers,
            BetaVersion: betaVersion,
            MercuryVersion0: mercuryVersion0,
            MercuryVersion1: mercuryVersion1,
            MercuryVersion2: mercuryVersion2,
            MercuryVersion3: mercuryVersion3,
            PennyVersion: pennyVersion,
            MetisVersion: metisVersion);

        radio = new DiscoveredRadio(
            Ip: fromIp,
            Mac: mac,
            Board: board,
            FirmwareVersion: codeVersion,
            FirmwareString: firmwareString,
            Details: details);
        return true;
    }

    // Protocol-2 discovery device byte (reply offset 11). This is the
    // NEW-protocol numbering and is DIFFERENT from Protocol 1: the dual-ADC
    // family is shifted by one. Authoritative sources agree —
    // piHPSDR discovered.h (NEW_DEVICE_*), Thetis network.h / enums.cs,
    // NereusSDR HpsdrModel.h:
    //   P2: 0x03 Angelia(100D), 0x04 Orion(200D), 0x05 OrionMkII(7000/8000)
    //   P1: 0x04 Angelia,       0x05 Orion,       0x0A OrionMkII
    // Reusing the P1 numbers here was the cause of issue #780 (a real
    // ANAN-200D sends P2 byte 0x04 and was mis-classified as Angelia/100D).
    // DO NOT copy these values into Zeus.Protocol1.Discovery.ReplyParser —
    // its 0x04/0x05/0x0A mapping is correct for the P1 wire and must stay.
    private static HpsdrBoardKind MapBoard(byte raw) => raw switch
    {
        0x00 => HpsdrBoardKind.Metis,        // Atlas
        0x01 => HpsdrBoardKind.Hermes,
        0x02 => HpsdrBoardKind.HermesII,
        0x03 => HpsdrBoardKind.Angelia,      // ANAN-100D
        0x04 => HpsdrBoardKind.Orion,        // ANAN-200D (issue #780)
        0x05 => HpsdrBoardKind.OrionMkII,    // ANAN-7000DLE / 8000DLE (Orion2)
        0x06 => HpsdrBoardKind.HermesLite2,
        0x0A => HpsdrBoardKind.OrionMkII,    // Saturn / ANAN-G2
        0x14 => HpsdrBoardKind.HermesC10,    // ANAN-G2E
        _ => HpsdrBoardKind.Unknown,
    };

    private static string FormatFirmware(byte codeVersion, byte betaVersion)
    {
        var major = codeVersion / 10;
        var minor = codeVersion % 10;
        return betaVersion == 0 ? $"{major}.{minor}" : $"{major}.{minor}b{betaVersion}";
    }
}
