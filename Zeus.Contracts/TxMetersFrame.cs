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

namespace Zeus.Contracts;

// Compact TX-telemetry frame. 37 bytes total:
//
//   [0x11] [fwdW:f32] [refW:f32] [swr:f32] [micDbfs:f32]
//          [eqPk:f32] [lvlrPk:f32] [alcPk:f32] [alcGr:f32] [outPk:f32]
//
// The front four floats (fwdW/refW/swr/micDbfs) are unchanged; the trailing
// five are TXA per-stage peak readings published by ProcessTxBlock during
// MOX (NegativeInfinity / 0 when TXA isn't processing — see TxStageMeters).
// Deliberately does NOT use the 16-byte WireFormat header: TX meters fire
// at 10 Hz during key-down and a per-frame header would more than double
// the wire cost without adding anything the client needs.
//
// Level meters are dBFS (typically −60..0); ALC-GR is dB of gain reduction
// (≥0, with 0 meaning "no reduction").
public readonly record struct TxMetersFrame(
    float FwdWatts,
    float RefWatts,
    float Swr,
    float MicDbfs,
    float EqPk,
    float LvlrPk,
    float AlcPk,
    float AlcGr,
    float OutPk)
{
    public const int ByteLength = 1 + 4 * 9;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.TxMeters;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(1, 4), FwdWatts);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(5, 4), RefWatts);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(9, 4), Swr);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(13, 4), MicDbfs);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(17, 4), EqPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(21, 4), LvlrPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(25, 4), AlcPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(29, 4), AlcGr);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(33, 4), OutPk);
        writer.Advance(ByteLength);
    }

    public static TxMetersFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"TxMetersFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.TxMeters)
            throw new InvalidDataException($"expected TxMeters (0x{(byte)MsgType.TxMeters:X2}), got 0x{bytes[0]:X2}");
        return new TxMetersFrame(
            FwdWatts: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(1, 4)),
            RefWatts: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(5, 4)),
            Swr: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(9, 4)),
            MicDbfs: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(13, 4)),
            EqPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(17, 4)),
            LvlrPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(21, 4)),
            AlcPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(25, 4)),
            AlcGr: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(29, 4)),
            OutPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(33, 4)));
    }
}
