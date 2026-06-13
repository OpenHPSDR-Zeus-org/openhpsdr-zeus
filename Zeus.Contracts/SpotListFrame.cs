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
// statement and per-contribution attribution.
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

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Full spot-list snapshot frame. Broadcast by SpotBroadcastService whenever
/// SpotManager changes (add / remove / clear), and pushed once to each new
/// WS client on connect. Wire format:
///
/// <code>
/// [type:u8=0x32][count:u16 LE]
/// [for each spot:]
///   [freqHz:i64 LE][argb:u32 LE]
///   [callsignLen:u8][callsign:UTF-8]
///   [modeLen:u8][mode:UTF-8]
///   [commentLen:u16 LE][comment:UTF-8]
/// </code>
///
/// String fields are capped: callsign ≤ <see cref="MaxCallsignBytes"/>,
/// mode ≤ <see cref="MaxModeBytes"/>, comment ≤ <see cref="MaxCommentBytes"/>.
/// Spot count is capped at <see cref="MaxSpots"/>. Wire-frozen: future fields
/// append after the variable-length comment of each spot.
/// </summary>
public readonly record struct SpotListFrame(IReadOnlyList<SpotListFrame.SpotEntry> Spots)
{
    public const int MaxSpots = 2000;
    public const int MaxCallsignBytes = 32;
    public const int MaxModeBytes = 8;
    public const int MaxCommentBytes = 256;

    // Fixed per-spot overhead: freqHz(8) + argb(4) + callsignLen(1) + modeLen(1) + commentLen(2) = 16
    private const int PerSpotFixed = 16;
    // Frame header: type(1) + count(2) = 3
    private const int FrameHeaderBytes = 3;

    public readonly record struct SpotEntry(
        long FreqHz,
        uint Argb,
        string Callsign,
        string Mode,
        string? Comment);

    public void Serialize(IBufferWriter<byte> writer)
    {
        int count = Math.Min(Spots.Count, MaxSpots);

        // Pre-encode strings to know exact byte lengths.
        var entries = new (byte[] cs, byte[] md, byte[] cm)[count];
        int totalBytes = FrameHeaderBytes;
        for (int i = 0; i < count; i++)
        {
            var spot = Spots[i];
            var cs = Clip(Encoding.UTF8.GetBytes(spot.Callsign ?? string.Empty), MaxCallsignBytes);
            var md = Clip(Encoding.UTF8.GetBytes(spot.Mode ?? string.Empty), MaxModeBytes);
            var cm = Clip(Encoding.UTF8.GetBytes(spot.Comment ?? string.Empty), MaxCommentBytes);
            entries[i] = (cs, md, cm);
            totalBytes += PerSpotFixed + cs.Length + md.Length + cm.Length;
        }

        var span = writer.GetSpan(totalBytes);
        int pos = 0;

        span[pos++] = (byte)MsgType.SpotList;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(pos, 2), (ushort)count); pos += 2;

        for (int i = 0; i < count; i++)
        {
            var spot = Spots[i];
            var (cs, md, cm) = entries[i];

            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(pos, 8), spot.FreqHz); pos += 8;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(pos, 4), spot.Argb); pos += 4;

            span[pos++] = (byte)cs.Length;
            cs.CopyTo(span.Slice(pos, cs.Length)); pos += cs.Length;

            span[pos++] = (byte)md.Length;
            md.CopyTo(span.Slice(pos, md.Length)); pos += md.Length;

            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(pos, 2), (ushort)cm.Length); pos += 2;
            cm.CopyTo(span.Slice(pos, cm.Length)); pos += cm.Length;
        }

        writer.Advance(pos);
    }

    private static byte[] Clip(byte[] src, int maxBytes) =>
        src.Length <= maxBytes ? src : src[..maxBytes];
}
