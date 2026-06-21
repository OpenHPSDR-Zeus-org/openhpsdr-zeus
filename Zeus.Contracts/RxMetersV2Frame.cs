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

// Extended RX-telemetry frame (v2). 29 bytes total:
//
//   [0x19] [signalPk:f32] [signalAv:f32]
//          [adcPk:f32]    [adcAv:f32]
//          [agcGain:f32]
//          [agcEnvPk:f32] [agcEnvAv:f32]
//
// Compatible additive extension of RxMeterFrame (0x14): carries the full
// set of WDSP RXA stage readings — signal peak/avg, ADC peak/avg, AGC
// gain, AGC envelope peak/avg. Mirrors the TxMetersV2Frame (0x16)
// design: a single bare-payload frame with no 16-byte WireFormat header
// keeps the 5 Hz cadence light, and giving every reading its own field
// (rather than a keyed map) matches the existing record-struct +
// BinaryPrimitives serialization shape used everywhere else in the
// telemetry pipeline.
//
// Units & sign conventions:
//
//   Signal*  — dBm, calibrated (RXA_S_PK / RXA_S_AV with the per-board
//              cal offset already added, e.g. HL2 +0.98 dB).
//   Adc*     — dBFS, raw ADC input (RXA_ADC_PK / RXA_ADC_AV); the cal
//              offset is NOT applied here because dBFS is board-
//              independent.
//   AgcGain  — dB, signed (RXA_AGC_GAIN). Positive = AGC is boosting a
//              weak signal; negative = AGC is cutting a hot signal; 0
//              when AGC is off. Differs from TX *Gr fields which are
//              negated to a positive "reduction" scale — RX AGC
//              genuinely swings both ways.
//   AgcEnv*  — dBm, calibrated (RXA_AGC_PK / RXA_AGC_AV); the AGC
//              envelope is downstream of the smeter tap in the WDSP
//              RXA chain so the same cal offset applies.
//
// Sentinel handling: a stage whose underlying WDSP path hasn't started
// (channel just opened, IQ not yet flowing, etc.) returns ≈ −400. The
// frame passes the sentinel through unchanged so the frontend can render
// an em-dash for "bypassed" stages — same convention as TxMetersV2.
public readonly record struct RxMetersV2Frame(
    float SignalPk,
    float SignalAv,
    float AdcPk,
    float AdcAv,
    float AgcGain,
    float AgcEnvPk,
    float AgcEnvAv)
{
    public const int ByteLength = 1 + 4 * 7;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.RxMetersV2;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(1, 4), SignalPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(5, 4), SignalAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(9, 4), AdcPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(13, 4), AdcAv);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(17, 4), AgcGain);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(21, 4), AgcEnvPk);
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(25, 4), AgcEnvAv);
        writer.Advance(ByteLength);
    }

    public static RxMetersV2Frame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"RxMetersV2Frame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.RxMetersV2)
            throw new InvalidDataException($"expected RxMetersV2 (0x{(byte)MsgType.RxMetersV2:X2}), got 0x{bytes[0]:X2}");
        return new RxMetersV2Frame(
            SignalPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(1, 4)),
            SignalAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(5, 4)),
            AdcPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(9, 4)),
            AdcAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(13, 4)),
            AgcGain: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(17, 4)),
            AgcEnvPk: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(21, 4)),
            AgcEnvAv: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(25, 4)));
    }
}
