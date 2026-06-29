// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using System.Text;
using Zeus.Server.DxCluster;

namespace Zeus.Server.Tests;

public class TelnetByteFilterTests
{
    private static string Filter(params byte[][] chunks)
    {
        var f = new TelnetByteFilter();
        var sink = new List<byte>();
        foreach (var c in chunks)
            f.Process(c, c.Length, sink);
        return Encoding.Latin1.GetString(sink.ToArray());
    }

    [Fact]
    public void PlainText_PassesThrough()
    {
        Assert.Equal("DX de W3LPL", Filter(Encoding.Latin1.GetBytes("DX de W3LPL")));
    }

    [Fact]
    public void Strips_DoWillNegotiation()
    {
        // IAC DO ECHO (FF FD 01) + "hi" + IAC WILL SGA (FF FB 03)
        var bytes = new byte[] { 0xFF, 0xFD, 0x01, (byte)'h', (byte)'i', 0xFF, 0xFB, 0x03 };
        Assert.Equal("hi", Filter(bytes));
    }

    [Fact]
    public void EscapedIac_YieldsLiteralFF()
    {
        // IAC IAC (FF FF) → a single literal 0xFF data byte.
        var bytes = new byte[] { (byte)'a', 0xFF, 0xFF, (byte)'b' };
        var result = Filter(bytes);
        Assert.Equal(3, result.Length);
        Assert.Equal('a', result[0]);
        Assert.Equal((char)0xFF, result[1]);
        Assert.Equal('b', result[2]);
    }

    [Fact]
    public void Strips_Subnegotiation()
    {
        // IAC SB <opt> <data...> IAC SE  (FF FA ... FF F0)
        var bytes = new byte[] { (byte)'x', 0xFF, 0xFA, 0x18, 0x00, (byte)'A', (byte)'B', 0xFF, 0xF0, (byte)'y' };
        Assert.Equal("xy", Filter(bytes));
    }

    [Fact]
    public void Sequence_SplitAcrossChunks_HandledStatefully()
    {
        // IAC at end of one chunk, DO ECHO continuing in the next.
        var c1 = new byte[] { (byte)'a', 0xFF };
        var c2 = new byte[] { 0xFD, 0x01, (byte)'b' };
        Assert.Equal("ab", Filter(c1, c2));
    }
}
