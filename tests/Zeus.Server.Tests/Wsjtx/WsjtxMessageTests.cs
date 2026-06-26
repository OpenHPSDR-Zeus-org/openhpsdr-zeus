// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

using System.Buffers.Binary;
using System.Text;
using Zeus.Server.Wsjtx;

namespace Zeus.Server.Tests;

public sealed class WsjtxMessageTests
{
    // Cursor over a WSJT-X NetworkMessage (Qt QDataStream: big-endian ints,
    // length-prefixed utf8 strings).
    private sealed class Reader(byte[] buf)
    {
        private int _pos;

        public uint UInt32()
        {
            var v = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        public string? Utf8()
        {
            var len = UInt32();
            if (len == 0xFFFFFFFF) return null;
            var s = Encoding.UTF8.GetString(buf, _pos, (int)len);
            _pos += (int)len;
            return s;
        }

        public int Remaining => buf.Length - _pos;
    }

    [Fact]
    public void EncodeLoggedAdif_WritesHeaderThenIdThenAdif()
    {
        var bytes = WsjtxMessage.EncodeLoggedAdif("WSJT-X", "<call:5>K1ABC<eor>");

        var r = new Reader(bytes);
        Assert.Equal(0xADBCCBDAu, r.UInt32());                 // magic
        Assert.Equal(WsjtxMessage.DefaultSchema, r.UInt32());  // schema (2)
        Assert.Equal(WsjtxMessage.LoggedAdifType, r.UInt32()); // type 12
        Assert.Equal("WSJT-X", r.Utf8());                      // instance id
        Assert.Equal("<call:5>K1ABC<eor>", r.Utf8());          // adif payload
        Assert.Equal(0, r.Remaining);                          // nothing trailing
    }

    [Fact]
    public void EncodeLoggedAdif_FirstFourBytesAreTheMagic()
    {
        var bytes = WsjtxMessage.EncodeLoggedAdif("WSJT-X", "x");
        Assert.Equal(new byte[] { 0xAD, 0xBC, 0xCB, 0xDA }, bytes[..4]);
    }

    [Fact]
    public void EncodeLoggedAdif_RoundTripsUtf8AndEmptyString()
    {
        // Non-ASCII instance id + empty ADIF exercises utf8 length + zero-length.
        var bytes = WsjtxMessage.EncodeLoggedAdif("Zëus", string.Empty);

        var r = new Reader(bytes);
        r.UInt32();
        r.UInt32();
        r.UInt32();
        Assert.Equal("Zëus", r.Utf8());
        Assert.Equal(string.Empty, r.Utf8());
    }

    [Fact]
    public void EncodeLoggedAdif_HonoursSchemaOverride()
    {
        var bytes = WsjtxMessage.EncodeLoggedAdif("WSJT-X", "x", schema: 3);
        var r = new Reader(bytes);
        r.UInt32();
        Assert.Equal(3u, r.UInt32());
    }
}
