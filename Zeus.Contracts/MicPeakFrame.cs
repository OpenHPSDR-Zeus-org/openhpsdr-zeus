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

/// <summary>
/// Server → client mic level telemetry, ~10 Hz. Published only in
/// desktop host mode by NativeMicCapture, where the browser's getUserMedia
/// analyser is intentionally disabled (Phase 2c) so the SPA has no other
/// way to know the mic level. Server mode never emits this frame —
/// remote operators drive their own browser-side analyser via
/// getUserMedia, as before.
///
/// <para>Wire format (header-less, like RxMeterFrame / PaTempFrame):</para>
/// <code>
///   [0x1D] [peakDbfs:f32 LE] [tsUnixMs:i64 LE]
/// </code>
/// <para>Total = 1 + 4 + 8 = 13 bytes. The timestamp is the server's
/// best estimate of when the peak was sampled — not strictly required for
/// rendering (the MicMeter only needs the latest peak), but kept for
/// debugging / future drift diagnostics, mirroring the AudioFrame.TsUnixMs
/// convention.</para>
///
/// <para>Floor convention: <c>peakDbfs</c> is clamped to -120 dBFS so a
/// silent (TCC-muted) stream is distinguishable from a missing-mic case
/// (the latter never publishes the frame at all).</para>
/// </summary>
public readonly record struct MicPeakFrame(float PeakDbfs, long TimestampUnixMs)
{
    public const int ByteLength = 1 + 4 + 8;

    /// <summary>Silence floor — distinguishes a TCC-silenced stream from a live mic.</summary>
    public const float MinDbfs = -120f;

    /// <summary>
    /// Converts a 0..1 linear peak amplitude to dBFS, floored at
    /// <see cref="MinDbfs"/>. Handles the silence edge case (peak ≤ 0) by
    /// returning the floor directly, matching the frontend's existing
    /// dbfs convention in use-mic-uplink.ts.
    /// </summary>
    public static float LinearToDbfs(float linearPeak)
    {
        if (linearPeak <= 0f) return MinDbfs;
        // 20 * log10(x) — using Math.Log avoids a transient Math.Log10
        // intrinsic miss on older runtimes; the constant 1/ln(10) ≈ 0.4343
        // is exact enough for a meter. Floor at MinDbfs.
        double db = 20.0 * Math.Log10(linearPeak);
        if (db < MinDbfs) return MinDbfs;
        // Clip the upper end to 0 dBFS — values above 1.0 would imply
        // intra-callback clipping; the operator sees the meter pinned high.
        if (db > 0.0) return 0f;
        return (float)db;
    }

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.MicPeak;
        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(1, 4), PeakDbfs);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(5, 8), TimestampUnixMs);
        writer.Advance(ByteLength);
    }

    public static MicPeakFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"MicPeakFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.MicPeak)
            throw new InvalidDataException($"expected MicPeak (0x{(byte)MsgType.MicPeak:X2}), got 0x{bytes[0]:X2}");
        return new MicPeakFrame(
            PeakDbfs: BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(1, 4)),
            TimestampUnixMs: BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(5, 8)));
    }
}
