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

/// <summary>
/// Alert frame carrying a kind byte (0 = SWR trip, others reserved) and a
/// UTF-8 message. Payload: [kind:u8][msgUtf8…]. Total length variable;
/// contract guarantees ≤ 256 bytes for the full frame so clients can
/// stack-allocate a decode buffer.
/// </summary>
/// <remarks>
/// Provenance: PRD FR-6 — SWR trip at 2.5:1 sustained 500 ms. The AlertFrame
/// is server-generated, sent once when the trip condition fires; clients
/// should not resend unless the user dismisses and re-triggers the fault.
/// </remarks>
public readonly record struct AlertFrame(AlertKind Kind, string Message)
{
    public const int MaxByteLength = 1 + 1 + 254; // type + kind + msg

    public void Serialize(IBufferWriter<byte> writer)
    {
        var msgBytes = Encoding.UTF8.GetBytes(Message);
        int totalLen = 1 + 1 + msgBytes.Length;
        if (totalLen > MaxByteLength)
            throw new InvalidOperationException($"AlertFrame message too long: {msgBytes.Length} bytes");

        var span = writer.GetSpan(totalLen);
        span[0] = (byte)MsgType.Alert;
        span[1] = (byte)Kind;
        msgBytes.CopyTo(span.Slice(2));
        writer.Advance(totalLen);
    }

    public static AlertFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
            throw new InvalidDataException($"AlertFrame requires ≥2 bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.Alert)
            throw new InvalidDataException($"expected Alert (0x{(byte)MsgType.Alert:X2}), got 0x{bytes[0]:X2}");

        var kind = (AlertKind)bytes[1];
        var msg = Encoding.UTF8.GetString(bytes.Slice(2));
        return new AlertFrame(kind, msg);
    }
}

/// <summary>
/// Alert kind enum. Kind 0 = SWR trip, Kind 1 = TX timeout (MOX or TUN keyed
/// for &gt; 120 s), Kind 2 = out-of-band TX guard (frequency/mode not permitted
/// by the active band plan). Additional kinds reserved for future protection
/// events (ADC overload, WS-drop-while-keyed, etc.).
/// </summary>
public enum AlertKind : byte
{
    SwrTrip = 0,
    TxTimeout = 1,
    OutOfBand = 2,
}
