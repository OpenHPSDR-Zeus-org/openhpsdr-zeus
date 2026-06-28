// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the two pure pieces of WSPRnet upload: the WSPR message splitter and the
// wsprd-compatible form builder (deterministic date/time/MHz formatting). The
// HTTP plumbing itself is BCL (FormUrlEncodedContent + HttpClient) and is
// wire-validated on the bench.

using Zeus.Server.Spotting;

namespace Zeus.Server.Tests.Spotting;

public sealed class WsprnetMessageTests
{
    [Fact]
    public void Type1_Call_Grid4_Power()
    {
        Assert.True(WsprnetMessage.TrySplit("K1ABC FN42 37", out var call, out var grid, out var dbm));
        Assert.Equal("K1ABC", call);
        Assert.Equal("FN42", grid);
        Assert.Equal(37, dbm);
    }

    [Fact]
    public void Type2_Call_Power_No_Grid()
    {
        Assert.True(WsprnetMessage.TrySplit("K1ABC 30", out var call, out var grid, out var dbm));
        Assert.Equal("K1ABC", call);
        Assert.Null(grid);
        Assert.Equal(30, dbm);
    }

    [Fact]
    public void Type3_Hashed_Call_Grid6_Power()
    {
        Assert.True(WsprnetMessage.TrySplit("<PJ4/K1ABC> FK52UD 33", out var call, out var grid, out var dbm));
        Assert.Equal("<PJ4/K1ABC>", call);
        Assert.Equal("FK52UD", grid);
        Assert.Equal(33, dbm);
    }

    [Fact]
    public void Lowercase_Is_Normalized()
    {
        Assert.True(WsprnetMessage.TrySplit("k1abc fn42 37", out var call, out var grid, out _));
        Assert.Equal("K1ABC", call);
        Assert.Equal("FN42", grid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("K1ABC FN42 NOTANUMBER")]   // power not numeric
    [InlineData("K1ABC NOTAGRID 37")]       // middle token not a grid
    [InlineData("ONLYONE")]
    [InlineData("A B C D")]                  // four tokens
    public void Junk_Returns_False(string msg)
    {
        Assert.False(WsprnetMessage.TrySplit(msg, out var call, out var grid, out var dbm));
        Assert.Equal("", call);
        Assert.Null(grid);
        Assert.Equal(0, dbm);
    }

    [Fact]
    public void Null_Returns_False()
    {
        Assert.False(WsprnetMessage.TrySplit(null, out _, out _, out _));
    }
}

public sealed class WsprnetFormBuilderTests
{
    private static string Get(IReadOnlyList<KeyValuePair<string, string>> form, string key)
    {
        foreach (var kv in form)
            if (kv.Key == key) return kv.Value;
        throw new KeyNotFoundException(key);
    }

    [Fact]
    public void Builds_Wsprd_Compatible_Field_Set_From_Fixed_Utc()
    {
        // 2026-06-26 14:08:00 UTC -> date 260626, time 1408.
        var slot = new DateTime(2026, 6, 26, 14, 8, 0, DateTimeKind.Utc);

        var form = WsprnetReporter.BuildSpotForm(
            rcall: "K1ABC", rgrid: "FN42",
            dialFreqMhz: 14.095600, slotStartUtc: slot,
            snrDb: -21.4f, dtSec: 0.3f, driftHz: -1, freqMhz: 14.097061,
            tcall: "W1AW", tgrid: "FN31", dbm: 37, version: "Zeus 0.10.0");

        Assert.Equal("wspr", Get(form, "function"));
        Assert.Equal("K1ABC", Get(form, "rcall"));
        Assert.Equal("FN42", Get(form, "rgrid"));
        Assert.Equal("14.095600", Get(form, "rqrg"));
        Assert.Equal("260626", Get(form, "date"));
        Assert.Equal("1408", Get(form, "time"));
        Assert.Equal("-21", Get(form, "sig"));        // rounded to int dB
        Assert.Equal("0.3", Get(form, "dt"));
        Assert.Equal("-1", Get(form, "drift"));
        Assert.Equal("14.097061", Get(form, "tqrg"));
        Assert.Equal("W1AW", Get(form, "tcall"));
        Assert.Equal("FN31", Get(form, "tgrid"));
        Assert.Equal("37", Get(form, "dbm"));
        Assert.Equal("Zeus 0.10.0", Get(form, "version"));
        Assert.Equal("2", Get(form, "mode"));
    }

    [Fact]
    public void Local_Kind_Time_Is_Converted_To_Utc()
    {
        // A non-UTC slot time must be normalised to UTC before formatting.
        var local = new DateTime(2026, 6, 26, 14, 8, 0, DateTimeKind.Local);
        var expected = local.ToUniversalTime();

        var form = WsprnetReporter.BuildSpotForm(
            "K1ABC", "FN42", 14.0956, local,
            -20f, 0f, 0, 14.097, "W1AW", "FN31", 30, "Zeus");

        Assert.Equal(expected.ToString("yyMMdd"), Get(form, "date"));
        Assert.Equal(expected.ToString("HHmm"), Get(form, "time"));
    }

    [Fact]
    public void Missing_Tgrid_Is_Empty_String()
    {
        var slot = new DateTime(2026, 6, 26, 14, 8, 0, DateTimeKind.Utc);
        var form = WsprnetReporter.BuildSpotForm(
            "K1ABC", "FN42", 14.0956, slot, -20f, 0f, 0, 14.097, "W1AW", null, 30, "Zeus");
        Assert.Equal("", Get(form, "tgrid"));
    }
}
