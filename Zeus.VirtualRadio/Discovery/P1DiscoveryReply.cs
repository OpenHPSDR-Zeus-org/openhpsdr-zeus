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
using Zeus.Protocol1.Discovery;

namespace Zeus.VirtualRadio.Discovery;

/// <summary>
/// Builds a Protocol-1 discovery reply — the inverse of
/// <c>Zeus.Protocol1.Discovery.ReplyParser.TryParse</c>. Encodes the 0xEF 0xFE
/// header, idle/busy status, MAC, code version, and the board byte for the
/// profile's <see cref="HpsdrBoardKind"/> (e.g. HermesII → 0x02), so the real
/// <c>ReplyParser</c> round-trips it back to the same board.
/// </summary>
internal static class P1DiscoveryReply
{
    /// <summary>
    /// Map a board kind to its Protocol-1 discovery wire byte (inverse of
    /// <c>ReplyParser.MapBoard</c>). NOTE: ANAN-10E (<see cref="HpsdrBoardKind.HermesII"/>)
    /// reports wire byte 0x02 directly; the 0x06-with-low-version reclassification
    /// path in <c>ReplyParser</c> is a separate HL2-vs-10E disambiguation the
    /// emulator does not need to reproduce for the 0x02 board.
    /// </summary>
    /// <returns>The discovery board byte for <paramref name="board"/>.</returns>
    public static byte BoardByte(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.Metis       => 0x00,
        HpsdrBoardKind.Hermes      => 0x01,
        HpsdrBoardKind.HermesII    => 0x02,
        HpsdrBoardKind.Angelia     => 0x04,
        HpsdrBoardKind.Orion       => 0x05,
        HpsdrBoardKind.HermesLite2 => 0x06,
        HpsdrBoardKind.OrionMkII   => 0x0A,
        HpsdrBoardKind.HermesC10   => 0x14,
        _ => throw new ArgumentOutOfRangeException(
            nameof(board), board, "No Protocol-1 discovery board byte for this board kind."),
    };

    /// <summary>
    /// Write a Protocol-1 discovery reply into <paramref name="dest"/>.
    /// </summary>
    /// <param name="dest">Destination buffer (≥ 60 bytes; the canonical reply is
    /// 60 bytes, of which <c>ReplyParser.MinimumReplyLength</c>=24 are parsed).</param>
    /// <param name="profile">The radio being impersonated.</param>
    /// <param name="mac">The MAC address to advertise.</param>
    /// <param name="codeVersion">Gateware code version byte to advertise.</param>
    /// <param name="busy">Whether to report the busy status (0x03) rather than idle (0x02).</param>
    /// <returns>The number of bytes written.</returns>
    public static int Build(
        Span<byte> dest,
        VirtualRadioProfile profile,
        PhysicalAddress mac,
        byte codeVersion,
        bool busy)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(mac);
        if (dest.Length < ReplyLength)
        {
            throw new ArgumentException(
                $"Discovery reply needs at least {ReplyLength} bytes, got {dest.Length}.",
                nameof(dest));
        }

        // Start from a clean slate so unused C&C / HL2 fields read as zero —
        // ReplyParser only meaningfully reads bytes [0..21] for a non-HL2 board.
        dest[..ReplyLength].Clear();

        // [0:1] fixed 0xEF 0xFE preamble (ReplyParser rejects anything else).
        dest[0] = 0xEF;
        dest[1] = 0xFE;

        // [2] status: 0x02 idle / 0x03 busy (ReplyParser.StatusIdle / StatusBusy).
        dest[2] = busy ? ReplyParser.StatusBusy : ReplyParser.StatusIdle;

        // [3:8] MAC (6 bytes). PhysicalAddress.GetAddressBytes is always 6 for EUI-48.
        byte[] macBytes = mac.GetAddressBytes();
        if (macBytes.Length != 6)
        {
            throw new ArgumentException(
                $"Discovery MAC must be a 6-byte EUI-48, got {macBytes.Length} bytes.",
                nameof(mac));
        }
        macBytes.CopyTo(dest.Slice(3, 6));

        // [9] gateware/code version (FirmwareVersion). For a 0x02 board this is
        // purely informational — it never triggers the HL2 reclassification path,
        // which only fires for board byte 0x06.
        dest[9] = codeVersion;

        // [10] board id byte (0x02 for HermesII / ANAN-10E).
        dest[10] = BoardByte(profile.Board);

        // [11] HL2 flag byte — left zero; ReplyParser ignores it for non-HL2 boards.
        // [19] gateware build — informational, left zero.
        // [21] HL2 minor version — ignored for non-HL2 boards, left zero.

        return ReplyLength;
    }

    /// <summary>
    /// Canonical Protocol-1 discovery reply length. The real radio emits a
    /// 60-byte datagram; <c>ReplyParser.MinimumReplyLength</c> (24) of those are
    /// parsed. We always emit the full 60 so any future field a parser adds is
    /// present and zeroed rather than truncated.
    /// </summary>
    public const int ReplyLength = 60;
}
