// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

public class TciCwParserTests
{
    // --- cw_macros_speed ---

    [Fact]
    public void CwMacrosSpeed_NoArgs_IsQuery()
    {
        var r = TciCwParser.ParseCwMacrosSpeed(Array.Empty<string>());
        Assert.NotNull(r);
        Assert.Null(r!.Value.Wpm);
    }

    [Fact]
    public void CwMacrosSpeed_Set_ReturnsWpm()
    {
        var r = TciCwParser.ParseCwMacrosSpeed(new[] { "25" });
        Assert.NotNull(r);
        Assert.Equal(25, r!.Value.Wpm);
    }

    [Theory]
    [InlineData("0")]    // below floor
    [InlineData("4")]    // below MinWpm = 5
    [InlineData("51")]   // above MaxWpm = 50
    [InlineData("abc")]  // not a number
    public void CwMacrosSpeed_OutOfRangeOrInvalid_ReturnsNull(string arg)
    {
        Assert.Null(TciCwParser.ParseCwMacrosSpeed(new[] { arg }));
    }

    // --- cw_macros ---

    [Theory]
    [InlineData("1", 1)]
    [InlineData("6", 6)]
    [InlineData("32", 32)]   // CwSettingsStore.MaxMacros
    public void CwMacros_ValidSlot_ParsesAsOneBased(string arg, int expected)
    {
        var r = TciCwParser.ParseCwMacros(new[] { arg });
        Assert.NotNull(r);
        Assert.Equal(expected, r!.Value.Slot1Based);
    }

    [Theory]
    [InlineData("0")]    // 0 is reserved as "no slot"
    [InlineData("-1")]
    [InlineData("33")]   // above MaxMacros
    [InlineData("abc")]
    public void CwMacros_InvalidSlot_ReturnsNull(string arg)
    {
        Assert.Null(TciCwParser.ParseCwMacros(new[] { arg }));
    }

    [Fact]
    public void CwMacros_NoArgs_ReturnsNull()
    {
        Assert.Null(TciCwParser.ParseCwMacros(Array.Empty<string>()));
    }

    // --- cw_msg ---

    [Fact]
    public void CwMsg_SingleText_ParsesText()
    {
        var r = TciCwParser.ParseCwMsg(new[] { "0", "CQ TEST" });
        Assert.NotNull(r);
        Assert.Equal("CQ TEST", r!.Value.Text);
        Assert.Equal(1, r.Value.Repeats);
    }

    [Fact]
    public void CwMsg_PrefixCallsignSuffix_ConcatenatesParts()
    {
        // ExpertSDR3 wire convention: each part already includes its own
        // trailing spacing. Parser concatenates without injecting separators.
        var r = TciCwParser.ParseCwMsg(new[] { "0", "CQ ", "EI6LF", " K" });
        Assert.NotNull(r);
        Assert.Equal("CQ EI6LF K", r!.Value.Text);
        Assert.Equal(1, r.Value.Repeats);
    }

    [Fact]
    public void CwMsg_TrailingSmallInteger_IsRepeatCount()
    {
        var r = TciCwParser.ParseCwMsg(new[] { "0", "CQ ", "EI6LF", " K", "3" });
        Assert.NotNull(r);
        Assert.Equal("CQ EI6LF K", r!.Value.Text);
        Assert.Equal(3, r.Value.Repeats);
    }

    [Fact]
    public void CwMsg_TrailingLargeInteger_IsTextNotRepeat()
    {
        // Contest exchange "5NN 001" — the trailing 001 is a serial number,
        // not a repeat count. Above MaxCwMsgRepeats it stays in the text.
        var r = TciCwParser.ParseCwMsg(new[] { "0", "5NN ", "100" });
        Assert.NotNull(r);
        // 100 is above MaxCwMsgRepeats (99) so treated as text.
        Assert.Equal("5NN 100", r!.Value.Text);
        Assert.Equal(1, r.Value.Repeats);
    }

    [Fact]
    public void CwMsg_EmptyText_ReturnsNull()
    {
        Assert.Null(TciCwParser.ParseCwMsg(new[] { "0", "" }));
    }

    [Fact]
    public void CwMsg_NoTextPart_ReturnsNull()
    {
        Assert.Null(TciCwParser.ParseCwMsg(new[] { "0" }));
    }

    [Fact]
    public void CwMsg_NonNumericRx_ReturnsNull()
    {
        Assert.Null(TciCwParser.ParseCwMsg(new[] { "abc", "CQ" }));
    }

    // --- keyer ---

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    public void Keyer_BoolArg_ParsesAsKeyDown(string arg, bool expected)
    {
        var r = TciCwParser.ParseKeyer(new[] { "0", arg });
        Assert.NotNull(r);
        Assert.Equal(expected, r!.Value.KeyDown);
        Assert.Null(r.Value.DurationMs);
    }

    [Fact]
    public void Keyer_WithDuration_ParsesDuration()
    {
        var r = TciCwParser.ParseKeyer(new[] { "0", "true", "500" });
        Assert.NotNull(r);
        Assert.True(r!.Value.KeyDown);
        Assert.Equal(500, r.Value.DurationMs);
    }

    [Fact]
    public void Keyer_ZeroDuration_ParsesAsNoTimer()
    {
        // Spec convention: duration=0 means "no auto-release timer", so
        // surface as null rather than 0 so the engine doesn't generate a
        // zero-sample-duration job.
        var r = TciCwParser.ParseKeyer(new[] { "0", "true", "0" });
        Assert.NotNull(r);
        Assert.True(r!.Value.KeyDown);
        Assert.Null(r.Value.DurationMs);
    }

    [Fact]
    public void Keyer_MissingBool_ReturnsNull()
    {
        Assert.Null(TciCwParser.ParseKeyer(new[] { "0" }));
    }

    [Fact]
    public void Keyer_MissingArgs_ReturnsNull()
    {
        Assert.Null(TciCwParser.ParseKeyer(Array.Empty<string>()));
    }

    [Fact]
    public void Keyer_NonBool_ReturnsNull()
    {
        Assert.Null(TciCwParser.ParseKeyer(new[] { "0", "maybe" }));
    }
}
