// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net.NetworkInformation;
using Zeus.Contracts;
using Zeus.Protocol2.Discovery;

namespace Zeus.VirtualRadio.P2;

/// <summary>
/// Builds a Protocol-2 discovery reply — the inverse of
/// <c>Zeus.Protocol2.Discovery.ReplyParser.TryParse</c>. The P2 discovery wire
/// differs from P1: the reply has a four-byte zero seq header, the status byte
/// is at offset 4, the MAC at 5..10, and the board id at offset 11 (NOT 10 as
/// in P1). The board-id numbering is also the NEW-protocol map (Angelia 0x03,
/// Orion 0x04) — but HermesII / ANAN-10E is 0x02 in both, which is all the
/// Phase-1 vertical slice needs. Feeding this reply back through the real
/// <c>ReplyParser</c> round-trips it to the same board, the anti-drift guard.
/// </summary>
internal static class P2DiscoveryReply
{
    /// <summary>
    /// Map a board kind to its Protocol-2 discovery wire byte (offset 11),
    /// the inverse of <c>ReplyParser.MapBoard</c>. The 0x0A/0x05 OrionMkII
    /// ambiguity in the parser means the inverse picks one canonical byte;
    /// HermesII (0x02) — the emulator's vertical slice — is unambiguous.
    /// </summary>
    public static byte BoardByte(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.Metis       => 0x00,
        HpsdrBoardKind.Hermes      => 0x01,
        HpsdrBoardKind.HermesII    => 0x02,
        HpsdrBoardKind.Angelia     => 0x03, // P2 numbering (P1 is 0x04)
        HpsdrBoardKind.Orion       => 0x04, // P2 numbering (P1 is 0x05)
        HpsdrBoardKind.OrionMkII   => 0x0A, // Saturn / ANAN-G2
        HpsdrBoardKind.HermesLite2 => 0x06,
        HpsdrBoardKind.HermesC10   => 0x14,
        _ => throw new ArgumentOutOfRangeException(
            nameof(board), board, "No Protocol-2 discovery board byte for this board kind."),
    };

    /// <summary>The canonical Protocol-2 discovery reply length.</summary>
    public const int ReplyLength = 60;

    /// <summary>
    /// True when <paramref name="datagram"/> is a Protocol-2 discovery probe:
    /// a 60-byte packet with a zero sequence header and the discovery command
    /// byte 0x02 at offset 4 (<c>RadioDiscoveryService.BuildDiscoveryPacket</c>).
    /// A <c>CmdGeneral</c> packet to the same port 1024 carries 0x00 there, so
    /// byte 4 discriminates discovery from a connect/config command.
    /// </summary>
    public static bool IsDiscoveryProbe(ReadOnlySpan<byte> datagram) =>
        datagram.Length >= 5
        && datagram[0] == 0 && datagram[1] == 0 && datagram[2] == 0 && datagram[3] == 0
        && datagram[P2Wire.GeneralCmdByte] == P2Wire.DiscoveryProbe;

    /// <summary>
    /// Write a Protocol-2 discovery reply into <paramref name="dest"/>.
    /// </summary>
    /// <param name="dest">Destination buffer (≥ 60 bytes).</param>
    /// <param name="profile">The radio being impersonated.</param>
    /// <param name="mac">The MAC address to advertise.</param>
    /// <param name="codeVersion">Gateware code version (offset 13).</param>
    /// <param name="numReceivers">Receiver count advertised at offset 20 (≥ 2).</param>
    /// <param name="busy">Report busy (0x03) rather than idle (0x02) at offset 4.</param>
    /// <returns>The number of bytes written.</returns>
    public static int Build(
        Span<byte> dest,
        VirtualRadioProfile profile,
        PhysicalAddress mac,
        byte codeVersion,
        byte numReceivers,
        bool busy)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(mac);
        if (dest.Length < ReplyLength)
        {
            throw new ArgumentException(
                $"P2 discovery reply needs at least {ReplyLength} bytes, got {dest.Length}.",
                nameof(dest));
        }

        dest[..ReplyLength].Clear();

        // [0..3] zero sequence header (ReplyParser rejects any non-zero here).
        // [4] status: 0x02 idle / 0x03 busy.
        dest[4] = busy ? ReplyParser.StatusBusy : ReplyParser.StatusIdle;

        // [5..10] MAC (6 bytes).
        byte[] macBytes = mac.GetAddressBytes();
        if (macBytes.Length != 6)
        {
            throw new ArgumentException(
                $"Discovery MAC must be a 6-byte EUI-48, got {macBytes.Length} bytes.",
                nameof(mac));
        }
        macBytes.CopyTo(dest.Slice(5, 6));

        // [11] board id (0x02 HermesII / ANAN-10E).
        dest[11] = BoardByte(profile.Board);

        // [12] protocols supported — advertise P2 (informational; ReplyParser
        // surfaces it but does not gate on it).
        dest[12] = 0x02;

        // [13] gateware code version.
        dest[13] = codeVersion;

        // [20] number of receivers (≥ 2 so the host sees full DDC capability).
        dest[20] = numReceivers < 2 ? (byte)2 : numReceivers;

        // [14..19] mercury/penny/metis sub-versions and [23] beta — informational, left zero.
        return ReplyLength;
    }
}
