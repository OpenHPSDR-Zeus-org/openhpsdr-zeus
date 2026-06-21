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

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Zeus.Contracts;

[Flags]
public enum DisplayBodyFlags : byte
{
    None = 0,
    PanValid = 1 << 0,
    WfValid = 1 << 1,
}

public readonly record struct DisplayFrame(
    uint Seq,
    double TsUnixMs,
    byte RxId,
    DisplayBodyFlags BodyFlags,
    ushort Width,
    long CenterHz,
    float HzPerPixel,
    ReadOnlyMemory<float> PanDb,
    ReadOnlyMemory<float> WfDb)
{
    public const int BodyHeaderSize = 1 + 1 + 2 + 8 + 4;

    public int BodyByteLength => BodyHeaderSize + Width * 4 * 2;

    public int TotalByteLength => WireFormat.HeaderSize + BodyByteLength;

    public void Serialize(IBufferWriter<byte> writer, byte headerFlags = 1)
    {
        if (PanDb.Length != Width || WfDb.Length != Width)
            throw new InvalidOperationException("PanDb/WfDb must be Width floats long.");

        int total = TotalByteLength;
        var span = writer.GetSpan(total);

        WireFormat.WriteHeader(
            span,
            MsgType.DisplayFrame,
            headerFlags,
            checked((ushort)BodyByteLength),
            Seq,
            TsUnixMs);

        var body = span.Slice(WireFormat.HeaderSize, BodyByteLength);
        body[0] = RxId;
        body[1] = (byte)BodyFlags;
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(2, 2), Width);
        BinaryPrimitives.WriteInt64LittleEndian(body.Slice(4, 8), CenterHz);
        BinaryPrimitives.WriteSingleLittleEndian(body.Slice(12, 4), HzPerPixel);

        int panBytes = Width * 4;
        MemoryMarshal.AsBytes(PanDb.Span).CopyTo(body.Slice(16, panBytes));
        MemoryMarshal.AsBytes(WfDb.Span).CopyTo(body.Slice(16 + panBytes, panBytes));

        writer.Advance(total);
    }

    public static DisplayFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        WireFormat.ReadHeader(bytes, out var msgType, out _, out var payloadLen, out var seq, out var ts);
        if (msgType != MsgType.DisplayFrame)
            throw new InvalidDataException($"expected DisplayFrame, got {msgType}");

        var body = bytes.Slice(WireFormat.HeaderSize, payloadLen);
        byte rxId = body[0];
        var flags = (DisplayBodyFlags)body[1];
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
        long centerHz = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(4, 8));
        float hzPerPixel = BinaryPrimitives.ReadSingleLittleEndian(body.Slice(12, 4));

        int panBytes = width * 4;
        var panArr = new float[width];
        var wfArr = new float[width];
        body.Slice(16, panBytes).CopyTo(MemoryMarshal.AsBytes(panArr.AsSpan()));
        body.Slice(16 + panBytes, panBytes).CopyTo(MemoryMarshal.AsBytes(wfArr.AsSpan()));

        return new DisplayFrame(seq, ts, rxId, flags, width, centerHz, hzPerPixel, panArr, wfArr);
    }
}
