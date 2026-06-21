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
using System.Text;

namespace Zeus.Contracts;

public enum WisdomPhase : byte
{
    Idle = 0,
    Building = 1,
    Ready = 2,
}

// [0x15][phase:u8][statusUtf8…]. Phase byte is mandatory; status string is
// optional UTF-8 trailer carrying the live WDSP wisdom_get_status() string
// (e.g. "Planning COMPLEX FORWARD FFT size 1024") so the splash can show
// what's actually happening during the multi-minute first-run build. The
// status buffer is sized to match the 128-byte fixed buffer in WDSP's
// wisdom.c; longer strings are truncated to keep the frame bounded.
public readonly record struct WisdomStatusFrame(WisdomPhase Phase, string Status = "")
{
    public const int MinByteLength = 1 + 1;
    public const int MaxStatusBytes = 128;
    public const int MaxByteLength = MinByteLength + MaxStatusBytes;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var statusBytes = string.IsNullOrEmpty(Status)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(Status);
        var trimmedLen = Math.Min(statusBytes.Length, MaxStatusBytes);
        int total = MinByteLength + trimmedLen;

        var span = writer.GetSpan(total);
        span[0] = (byte)MsgType.WisdomStatus;
        span[1] = (byte)Phase;
        if (trimmedLen > 0)
            statusBytes.AsSpan(0, trimmedLen).CopyTo(span.Slice(2));
        writer.Advance(total);
    }

    public int ByteLength
    {
        get
        {
            if (string.IsNullOrEmpty(Status)) return MinByteLength;
            var len = Encoding.UTF8.GetByteCount(Status);
            return MinByteLength + Math.Min(len, MaxStatusBytes);
        }
    }

    public static WisdomStatusFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinByteLength)
            throw new InvalidDataException($"WisdomStatusFrame requires {MinByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.WisdomStatus)
            throw new InvalidDataException($"expected WisdomStatus (0x{(byte)MsgType.WisdomStatus:X2}), got 0x{bytes[0]:X2}");
        var phase = (WisdomPhase)bytes[1];
        var status = bytes.Length > MinByteLength
            ? Encoding.UTF8.GetString(bytes.Slice(MinByteLength))
            : string.Empty;
        return new WisdomStatusFrame(phase, status);
    }
}
