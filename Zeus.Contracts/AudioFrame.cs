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

public readonly record struct AudioFrame(
    uint Seq,
    double TsUnixMs,
    byte RxId,
    byte Channels,
    uint SampleRateHz,
    ushort SampleCount,
    ReadOnlyMemory<float> Samples)
{
    public const int BodyHeaderSize = 1 + 1 + 4 + 2;

    public int BodyByteLength => BodyHeaderSize + SampleCount * Channels * 4;

    public int TotalByteLength => WireFormat.HeaderSize + BodyByteLength;

    public void Serialize(IBufferWriter<byte> writer, byte headerFlags = 0)
    {
        if (Channels == 0) throw new InvalidOperationException("Channels must be >= 1.");
        if (Samples.Length != SampleCount * Channels)
            throw new InvalidOperationException("Samples length must equal SampleCount * Channels.");

        int total = TotalByteLength;
        var span = writer.GetSpan(total);

        WireFormat.WriteHeader(
            span,
            MsgType.AudioPcm,
            headerFlags,
            checked((ushort)BodyByteLength),
            Seq,
            TsUnixMs);

        var body = span.Slice(WireFormat.HeaderSize, BodyByteLength);
        body[0] = RxId;
        body[1] = Channels;
        BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(2, 4), SampleRateHz);
        BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(6, 2), SampleCount);

        int sampleBytes = SampleCount * Channels * 4;
        MemoryMarshal.AsBytes(Samples.Span).CopyTo(body.Slice(BodyHeaderSize, sampleBytes));

        writer.Advance(total);
    }

    public static AudioFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        WireFormat.ReadHeader(bytes, out var msgType, out _, out var payloadLen, out var seq, out var ts);
        if (msgType != MsgType.AudioPcm)
            throw new InvalidDataException($"expected AudioPcm, got {msgType}");

        var body = bytes.Slice(WireFormat.HeaderSize, payloadLen);
        byte rxId = body[0];
        byte channels = body[1];
        uint sampleRateHz = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(2, 4));
        ushort sampleCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(6, 2));

        int sampleFloats = sampleCount * channels;
        var samples = new float[sampleFloats];
        int sampleBytes = sampleFloats * 4;
        body.Slice(BodyHeaderSize, sampleBytes).CopyTo(MemoryMarshal.AsBytes(samples.AsSpan()));

        return new AudioFrame(seq, ts, rxId, channels, sampleRateHz, sampleCount, samples);
    }
}
