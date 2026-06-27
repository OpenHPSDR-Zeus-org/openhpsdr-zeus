// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the backend FT8/FT4 sender-extraction parser used by PSK Reporter
// spotting. Cross-checked against the frontend ft8-message.ts behaviour so the
// two stay in lockstep.

using Zeus.Dsp.Ft8;

namespace Zeus.Dsp.Ft8.Tests;

public sealed class Ft8MessageParseTests
{
    [Fact]
    public void Cq_With_Grid()
    {
        Assert.True(Ft8MessageParse.TryParseSender("CQ K1ABC FN42", out var call, out var grid));
        Assert.Equal("K1ABC", call);
        Assert.Equal("FN42", grid);
    }

    [Fact]
    public void Cq_Dx_Directive()
    {
        Assert.True(Ft8MessageParse.TryParseSender("CQ DX G0XYZ IO91", out var call, out var grid));
        Assert.Equal("G0XYZ", call);
        Assert.Equal("IO91", grid);
    }

    [Fact]
    public void Cq_Pota_Directive()
    {
        Assert.True(Ft8MessageParse.TryParseSender("CQ POTA W9XYZ EN52", out var call, out var grid));
        Assert.Equal("W9XYZ", call);
        Assert.Equal("EN52", grid);
    }

    [Fact]
    public void Cq_Without_Grid()
    {
        Assert.True(Ft8MessageParse.TryParseSender("CQ W9XYZ", out var call, out var grid));
        Assert.Equal("W9XYZ", call);
        Assert.Null(grid);
    }

    [Fact]
    public void Directed_Tx1_Grid_Reply_Sender_Is_De()
    {
        // "TARGET DE GRID" — sender is the DE call (token 1), not the target.
        Assert.True(Ft8MessageParse.TryParseSender("K1ABC G0XYZ IO91", out var call, out var grid));
        Assert.Equal("G0XYZ", call);
        Assert.Equal("IO91", grid);
    }

    [Fact]
    public void Directed_Report_No_Grid()
    {
        Assert.True(Ft8MessageParse.TryParseSender("K1ABC G0XYZ -12", out var call, out var grid));
        Assert.Equal("G0XYZ", call);
        Assert.Null(grid);
    }

    [Fact]
    public void Directed_RReport_No_Grid()
    {
        Assert.True(Ft8MessageParse.TryParseSender("K1ABC G0XYZ R+05", out var call, out var grid));
        Assert.Equal("G0XYZ", call);
        Assert.Null(grid);
    }

    [Fact]
    public void Rr73_Is_Not_A_Grid()
    {
        // "RR73" matches the 4-char grid regex (R,R,7,3) but must NOT be reported
        // as a locator. Sender is still extracted.
        Assert.True(Ft8MessageParse.TryParseSender("K1ABC G0XYZ RR73", out var call, out var grid));
        Assert.Equal("G0XYZ", call);
        Assert.Null(grid);
    }

    [Fact]
    public void Rrr_And_73_Sender_Extracted_No_Grid()
    {
        Assert.True(Ft8MessageParse.TryParseSender("K1ABC G0XYZ 73", out var call, out _));
        Assert.Equal("G0XYZ", call);
        Assert.True(Ft8MessageParse.TryParseSender("K1ABC G0XYZ RRR", out var call2, out _));
        Assert.Equal("G0XYZ", call2);
    }

    [Fact]
    public void Hashed_Sender_Is_Rejected()
    {
        // A purely-hashed DE call cannot be reported.
        Assert.False(Ft8MessageParse.TryParseSender("K1ABC <...> RR73", out _, out _));
    }

    [Fact]
    public void Hashed_Target_With_Real_Sender_Is_Accepted()
    {
        Assert.True(Ft8MessageParse.TryParseSender("<...> G0XYZ IO91", out var call, out var grid));
        Assert.Equal("G0XYZ", call);
        Assert.Equal("IO91", grid);
    }

    [Fact]
    public void Hashed_Sender_With_Real_Call_Inside_Is_Accepted()
    {
        Assert.True(Ft8MessageParse.TryParseSender("K1ABC <PJ4/K1ABC> +03", out var call, out _));
        Assert.Equal("PJ4/K1ABC", call);
    }

    [Fact]
    public void Lowercase_Is_Normalized()
    {
        Assert.True(Ft8MessageParse.TryParseSender("cq k1abc fn42", out var call, out var grid));
        Assert.Equal("K1ABC", call);
        Assert.Equal("FN42", grid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HELLO WORLD")]
    [InlineData("TNX FER QSO")]
    public void Free_Text_Returns_False(string text)
    {
        Assert.False(Ft8MessageParse.TryParseSender(text, out var call, out var grid));
        Assert.Equal("", call);
        Assert.Null(grid);
    }

    [Fact]
    public void Null_Returns_False()
    {
        Assert.False(Ft8MessageParse.TryParseSender(null, out _, out _));
    }

    [Theory]
    [InlineData("K1ABC FN42")]      // 2-token free text: token[1] is a grid, not a call
    [InlineData("G0XYZ FN42 73")]   // 3-token free text: token[1] is a grid, not a call
    [InlineData("CQ FN42")]         // CQ followed by a grid where the call should be
    public void Grid_Like_Sender_Token_Is_Rejected(string text)
    {
        // A bare 4-char Maidenhead locator satisfies the loose letter+digit core
        // check but must never be reported as a transmitting callsign (it would
        // upload a spot for a nonexistent station to PSK Reporter).
        Assert.False(Ft8MessageParse.TryParseSender(text, out var call, out var grid));
        Assert.Equal("", call);
        Assert.Null(grid);
    }

    [Fact]
    public void Portable_Call_With_Slash_Is_Accepted()
    {
        Assert.True(Ft8MessageParse.TryParseSender("CQ K1ABC/P FN42", out var call, out var grid));
        Assert.Equal("K1ABC/P", call);
        Assert.Equal("FN42", grid);
    }
}
