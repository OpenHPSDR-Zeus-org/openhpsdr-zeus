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

using Zeus.Contracts;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

public class TciProtocolTests
{
    [Fact]
    public void Command_WithNoArgs_ReturnsSemicolonTerminated()
    {
        var result = TciProtocol.Command("ready");
        Assert.Equal("ready;", result);
    }

    [Fact]
    public void Command_WithSingleArg_FormatsCorrectly()
    {
        var result = TciProtocol.Command("device", "Zeus");
        Assert.Equal("device:Zeus;", result);
    }

    [Fact]
    public void Command_WithMultipleArgs_CommaSeparated()
    {
        var result = TciProtocol.Command("vfo", 0, 0, 14074000);
        Assert.Equal("vfo:0,0,14074000;", result);
    }

    [Fact]
    public void Command_WithBoolArgs_FormatsAsLowercase()
    {
        var result1 = TciProtocol.Command("mute", true);
        var result2 = TciProtocol.Command("mute", false);
        Assert.Equal("mute:true;", result1);
        Assert.Equal("mute:false;", result2);
    }

    [Fact]
    public void Command_WithDoubleArg_FormatsWithDecimal()
    {
        var result = TciProtocol.Command("volume", -12.5);
        Assert.Equal("volume:-12.5;", result);
    }

    [Fact]
    public void Parse_BareCommand_ReturnsCommandWithEmptyArgs()
    {
        var parsed = TciProtocol.Parse("ready;");
        Assert.NotNull(parsed);
        Assert.Equal("ready", parsed.Value.command);
        Assert.Empty(parsed.Value.args);
    }

    [Fact]
    public void Parse_CommandWithArgs_SplitsCorrectly()
    {
        var parsed = TciProtocol.Parse("vfo:0,0,14074000;");
        Assert.NotNull(parsed);
        Assert.Equal("vfo", parsed.Value.command);
        Assert.Equal(3, parsed.Value.args.Length);
        Assert.Equal("0", parsed.Value.args[0]);
        Assert.Equal("0", parsed.Value.args[1]);
        Assert.Equal("14074000", parsed.Value.args[2]);
    }

    [Fact]
    public void Parse_WithoutTrailingSemicolon_StillWorks()
    {
        var parsed = TciProtocol.Parse("modulation:0,USB");
        Assert.NotNull(parsed);
        Assert.Equal("modulation", parsed.Value.command);
        Assert.Equal(2, parsed.Value.args.Length);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        var parsed = TciProtocol.Parse("");
        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_Whitespace_ReturnsNull()
    {
        var parsed = TciProtocol.Parse("   ");
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData(RxMode.AM, "AM")]
    [InlineData(RxMode.SAM, "SAM")]
    [InlineData(RxMode.DSB, "DSB")]
    [InlineData(RxMode.LSB, "LSB")]
    [InlineData(RxMode.USB, "USB")]
    [InlineData(RxMode.FM, "FM")]
    [InlineData(RxMode.CWL, "CWL")]
    [InlineData(RxMode.CWU, "CWU")]
    [InlineData(RxMode.DIGL, "DIGL")]
    [InlineData(RxMode.DIGU, "DIGU")]
    public void ModeToTci_AllModes_UpperCase(RxMode mode, string expected)
    {
        var result = TciProtocol.ModeToTci(mode);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("AM", RxMode.AM)]
    [InlineData("am", RxMode.AM)]
    [InlineData("SAM", RxMode.SAM)]
    [InlineData("LSB", RxMode.LSB)]
    [InlineData("lsb", RxMode.LSB)]
    [InlineData("USB", RxMode.USB)]
    [InlineData("FM", RxMode.FM)]
    [InlineData("NFM", RxMode.FM)] // NFM alias
    [InlineData("CWL", RxMode.CWL)]
    [InlineData("CWU", RxMode.CWU)]
    [InlineData("DIGL", RxMode.DIGL)]
    [InlineData("DIGU", RxMode.DIGU)]
    public void TciToMode_ValidModes_CaseInsensitive(string tciMode, RxMode expected)
    {
        var result = TciProtocol.TciToMode(tciMode);
        Assert.NotNull(result);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void TciToMode_UnknownMode_ReturnsNull()
    {
        var result = TciProtocol.TciToMode("INVALID");
        Assert.Null(result);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("False", false)]
    public void TryParseBool_ValidValues_ParsesCorrectly(string input, bool expected)
    {
        bool success = TciProtocol.TryParseBool(input, out bool value);
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("")]
    public void TryParseBool_InvalidValues_ReturnsFalse(string input)
    {
        bool success = TciProtocol.TryParseBool(input, out _);
        Assert.False(success);
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-10", -10)]
    [InlineData("0", 0)]
    public void TryParseInt_ValidValues_ParsesCorrectly(string input, int expected)
    {
        bool success = TciProtocol.TryParseInt(input, out int value);
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("14074000", 14074000L)]
    [InlineData("61440000", 61440000L)]
    public void TryParseLong_ValidValues_ParsesCorrectly(string input, long expected)
    {
        bool success = TciProtocol.TryParseLong(input, out long value);
        Assert.True(success);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("-12.5", -12.5)]
    [InlineData("0.0", 0.0)]
    [InlineData("80.0", 80.0)]
    public void TryParseDouble_ValidValues_ParsesCorrectly(string input, double expected)
    {
        bool success = TciProtocol.TryParseDouble(input, out double value);
        Assert.True(success);
        Assert.Equal(expected, value, precision: 5);
    }

    [Fact]
    public void Command_AgcGain_FormatsQueryCorrectly()
    {
        // agc_gain:<rx>,<db> — query response
        var result = TciProtocol.Command("agc_gain", 0, 80);
        Assert.Equal("agc_gain:0,80;", result);
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(120)]
    public void Command_AgcGain_FormatsWithValidRange(int db)
    {
        var result = TciProtocol.Command("agc_gain", 0, db);
        Assert.Equal($"agc_gain:0,{db};", result);
    }

    [Fact]
    public void Parse_AgcGainQuery_ParsesCorrectly()
    {
        var parsed = TciProtocol.Parse("agc_gain:0;");
        Assert.NotNull(parsed);
        Assert.Equal("agc_gain", parsed.Value.command);
        Assert.Single(parsed.Value.args);
        Assert.Equal("0", parsed.Value.args[0]);
    }

    [Fact]
    public void Parse_AgcGainSet_ParsesCorrectly()
    {
        var parsed = TciProtocol.Parse("agc_gain:0,85;");
        Assert.NotNull(parsed);
        Assert.Equal("agc_gain", parsed.Value.command);
        Assert.Equal(2, parsed.Value.args.Length);
        Assert.Equal("0", parsed.Value.args[0]);
        Assert.Equal("85", parsed.Value.args[1]);
    }

    [Fact]
    public void Command_RxSmeter_FormatsCorrectly()
    {
        // rx_smeter:<rx>,<chan>,<dbm>
        var result = TciProtocol.Command("rx_smeter", 0, 0, -73);
        Assert.Equal("rx_smeter:0,0,-73;", result);
    }

    [Theory]
    [InlineData(0, -120)]
    [InlineData(0, -73)]
    [InlineData(0, -40)]
    [InlineData(0, 0)]
    public void Command_RxSmeter_FormatsWithVariousDbm(int channel, int dbm)
    {
        var result = TciProtocol.Command("rx_smeter", 0, channel, dbm);
        Assert.Equal($"rx_smeter:0,{channel},{dbm};", result);
    }

    [Fact]
    public void Command_TxPower_FormatsCorrectly()
    {
        // tx_power:<watts>
        var result = TciProtocol.Command("tx_power", 50);
        Assert.Equal("tx_power:50;", result);
    }

    [Fact]
    public void Command_TxSwr_FormatsCorrectly()
    {
        // tx_swr:<ratio> — formatted as decimal string
        var result = TciProtocol.Command("tx_swr", "1.5");
        Assert.Equal("tx_swr:1.5;", result);
    }

    [Fact]
    public void Command_TxAlc_FormatsCorrectly()
    {
        // tx_alc:<percent> — ALC as percentage 0-100
        var result = TciProtocol.Command("tx_alc", 25);
        Assert.Equal("tx_alc:25;", result);
    }
}
