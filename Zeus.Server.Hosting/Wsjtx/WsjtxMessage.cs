// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers.Binary;
using System.Text;

namespace Zeus.Server.Wsjtx;

/// <summary>
/// Encoder for the WSJT-X UDP "NetworkMessage" wire format — the de-facto
/// protocol that JTAlert / Log4OM / GridTracker / N1MM consume. We emit ONE
/// message type: 12 (LoggedADIF). The wire format is a Qt QDataStream: all
/// integers big-endian, and each string is a UTF-8 QByteArray written as a
/// 4-byte big-endian length followed by the bytes (length 0xFFFFFFFF = null).
///
/// Common header (every message): magic uint32 0xADBCCBDA, schema uint32,
/// message-type uint32, then the instance id (utf8). LoggedADIF appends a single
/// utf8 string: the ADIF record text.
///
/// Pure + allocation-light so it is trivially unit-testable; no I/O lives here
/// (see WsjtxUdpBroadcaster for the send).
/// </summary>
public static class WsjtxMessage
{
    /// <summary>WSJT-X NetworkMessage magic number.</summary>
    public const uint Magic = 0xADBCCBDA;

    /// <summary>LoggedADIF message type id.</summary>
    public const uint LoggedAdifType = 12;

    /// <summary>
    /// Schema number written into the header. WSJT-X has used schema 2 across the
    /// long-lived 2.x series and downstream tools accept it broadly; bumping is a
    /// one-line change if a future client demands 3. G2 bench: confirm JTAlert /
    /// GridTracker ingest before relying on this.
    /// </summary>
    public const uint DefaultSchema = 2;

    /// <summary>Encode a LoggedADIF (type 12) datagram for the given ADIF text.</summary>
    public static byte[] EncodeLoggedAdif(string instanceId, string adif, uint schema = DefaultSchema)
    {
        using var ms = new MemoryStream(64 + (adif?.Length ?? 0));
        WriteUInt32(ms, Magic);
        WriteUInt32(ms, schema);
        WriteUInt32(ms, LoggedAdifType);
        WriteUtf8(ms, instanceId);
        WriteUtf8(ms, adif);
        return ms.ToArray();
    }

    private static void WriteUInt32(Stream s, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        s.Write(b);
    }

    private static void WriteUtf8(Stream s, string? value)
    {
        if (value is null)
        {
            // Qt encodes a null QByteArray as length 0xFFFFFFFF.
            WriteUInt32(s, 0xFFFFFFFF);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(s, (uint)bytes.Length);
        s.Write(bytes);
    }
}
