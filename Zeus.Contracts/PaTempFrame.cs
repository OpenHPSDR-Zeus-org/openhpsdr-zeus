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

// Compact PA-temperature frame. 5 bytes total:
//
//   [0x17] [tempC:f32]
//
// Broadcast at 2 Hz regardless of MOX state — temperature is a protection
// signal that matters during RX-only operation as well (the HL2 gateware
// auto-disables TX at 55 °C on the Q6 sensor, so the operator wants to see
// the climb even when not keyed). Moves on a seconds timescale, so the
// 10 Hz TX-meter cadence is overkill.
//
// Value is the smoothed-and-clamped Celsius reading from the HL2 Q6
// sensor, arriving as <c>reading.Ain0</c> on the C0=0x08 echo slot (same
// slot that carries Alex FWD power in <c>Ain1</c>). Clamped into the
// plausible sensor range at the server so a floating ADC input can't
// trip the UI's 55 °C red zone on boot.
//
// Deliberately does NOT use the 16-byte WireFormat header — matches
// RxMeterFrame / TxMetersFrame conventions: a per-frame header would more
// than quadruple the wire cost for a value that updates twice a second.
public readonly record struct PaTempFrame(float TempC)
{
    public const int ByteLength = 1 + 4;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.PaTemp;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(1, 4), TempC);
        writer.Advance(ByteLength);
    }

    public static PaTempFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"PaTempFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.PaTemp)
            throw new InvalidDataException($"expected PaTemp (0x{(byte)MsgType.PaTemp:X2}), got 0x{bytes[0]:X2}");
        return new PaTempFrame(
            TempC: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(1, 4)));
    }
}
