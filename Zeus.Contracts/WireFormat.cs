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

// Web-side wire is deliberately little-endian: JS DataView/TypedArrays are LE-by-default
// on all target machines. Big-endian is only used on the HPSDR radio side (Protocol 1 UDP),
// which is a distinct concern handled inside Zeus.Protocol1.
using System.Buffers.Binary;

namespace Zeus.Contracts;

public static class WireFormat
{
    public const int HeaderSize = 16;

    public static void WriteHeader(
        Span<byte> dst,
        MsgType msgType,
        byte flags,
        ushort payloadLen,
        uint seq,
        double tsUnixMs)
    {
        if (dst.Length < HeaderSize)
            throw new ArgumentException($"header buffer must be at least {HeaderSize} bytes", nameof(dst));

        dst[0] = (byte)msgType;
        dst[1] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(2, 2), payloadLen);
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(4, 4), seq);
        BinaryPrimitives.WriteDoubleLittleEndian(dst.Slice(8, 8), tsUnixMs);
    }

    public static void ReadHeader(
        ReadOnlySpan<byte> src,
        out MsgType msgType,
        out byte flags,
        out ushort payloadLen,
        out uint seq,
        out double tsUnixMs)
    {
        if (src.Length < HeaderSize)
            throw new ArgumentException($"header buffer must be at least {HeaderSize} bytes", nameof(src));

        msgType = (MsgType)src[0];
        flags = src[1];
        payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(2, 2));
        seq = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4, 4));
        tsUnixMs = BinaryPrimitives.ReadDoubleLittleEndian(src.Slice(8, 8));
    }
}
