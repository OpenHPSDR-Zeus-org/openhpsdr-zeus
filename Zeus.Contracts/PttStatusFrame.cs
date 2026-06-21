// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers;

namespace Zeus.Contracts;

// Hardware-PTT-IN (footswitch / mic PTT / rear KEY) status edge frame. 2 bytes:
//   [PttStatus][keyed:u8]
//
// Read-only status for the Radio Settings "PTT-IN: idle / keyed" lamp. The
// SOURCE of the edge is per-protocol: P1 boards are driven by the
// Protocol1Client HardwarePttChanged event (C0[0] / ptt_resp), P2 boards by
// the UDP-1025 hi-priority status PttIn bit (byte0 bit0). This is a pure
// indicator — it does NOT drive MOX (ExternalPttService promotes the same
// edges into MOX separately, through TxService.TrySetMox arbitration). The
// lamp tracks every physical edge regardless of the enable gate.
public readonly record struct PttStatusFrame(bool Keyed)
{
    public const int ByteLength = 2;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.PttStatus;
        span[1] = Keyed ? (byte)1 : (byte)0;
        writer.Advance(ByteLength);
    }

    public static PttStatusFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException($"PttStatusFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.PttStatus)
            throw new InvalidDataException($"expected PttStatus (0x{(byte)MsgType.PttStatus:X2}), got 0x{bytes[0]:X2}");
        return new PttStatusFrame(Keyed: bytes[1] != 0);
    }
}
