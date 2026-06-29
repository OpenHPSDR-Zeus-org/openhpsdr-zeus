// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Zeus.Server.DxCluster;

namespace Zeus.Server.Tests;

public class DxSpotLineParserTests
{
    [Theory]
    // Canonical DXSpider line.
    [InlineData("DX de W3LPL:    14074.0  K1ABC        FT8  -12 dB         1432Z",
        "W3LPL", "K1ABC", 14074000L, "FT8")]
    // Integer frequency, CW.
    [InlineData("DX de G3XYZ:     7025.0  DL1ABC       CW                  0901Z",
        "G3XYZ", "DL1ABC", 7025000L, "CW")]
    // AR-Cluster-style tighter spacing.
    [InlineData("DX de N1XX: 21260.0 EA8XX SSB up 2 1700Z",
        "N1XX", "EA8XX", 21260000L, "SSB")]
    // FT4 derivation.
    [InlineData("DX de JA1ABC:   7047.5  VK3XYZ  FT4 +03  2230Z",
        "JA1ABC", "VK3XYZ", 7047500L, "FT4")]
    // Portable / slashed calls on both ends.
    [InlineData("DX de SV1XX/P:  10136.0  9A/DL1XX/MM   FT8         1010Z",
        "SV1XX/P", "9A/DL1XX/MM", 10136000L, "FT8")]
    // Six-digit MHz-band freq (VHF), no mode in comment → mode "".
    [InlineData("DX de W2ABC:   144174.0  W3DEF        nice sig     1300Z",
        "W2ABC", "W3DEF", 144174000L, "")]
    public void TryParse_ValidLines_Extracts(string line, string spotter, string dx, long freqHz, string mode)
    {
        Assert.True(DxSpotLineParser.TryParse(line, out var spot));
        Assert.Equal(spotter, spot.SpotterCall);
        Assert.Equal(dx, spot.DxCall);
        Assert.Equal(freqHz, spot.FreqHz);
        Assert.Equal(mode, spot.Mode);
    }

    [Fact]
    public void TryParse_StripsTrailingTimeFromComment()
    {
        Assert.True(DxSpotLineParser.TryParse(
            "DX de W3LPL:    14074.0  K1ABC        FT8  -12 dB         1432Z", out var spot));
        Assert.Equal("1432Z", spot.Time);
        Assert.DoesNotContain("1432Z", spot.Comment);
        Assert.Contains("FT8", spot.Comment);
        Assert.Contains("-12 dB", spot.Comment);
    }

    [Fact]
    public void TryParse_TrailingGridAfterTime_Ignored()
    {
        Assert.True(DxSpotLineParser.TryParse(
            "DX de EA1XX:    14025.0  K1ABC   CW   1432Z FN42", out var spot));
        Assert.Equal("1432Z", spot.Time);
        Assert.Equal("CW", spot.Mode);
    }

    [Fact]
    public void TryParse_NoTrailingTime_StillParses()
    {
        Assert.True(DxSpotLineParser.TryParse(
            "DX de W3LPL:    14074.0  K1ABC   FT8", out var spot));
        Assert.Equal("", spot.Time);
        Assert.Equal("FT8", spot.Mode);
        Assert.Equal("K1ABC", spot.DxCall);
    }

    [Fact]
    public void TryParse_DecimalFreq_RoundsToInteger()
    {
        Assert.True(DxSpotLineParser.TryParse(
            "DX de W1AW:     3573.1  K9XYZ   FT8   0000Z", out var spot));
        Assert.Equal(3573100L, spot.FreqHz);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    // Login banner / MOTD.
    [InlineData("Welcome to the W3LPL DX Cluster")]
    [InlineData("Please enter your call: ")]
    [InlineData("login: ")]
    // Prompt.
    [InlineData("W3LPL de K1ABC >")]
    // Talk / announce lines (not DX spots).
    [InlineData("To ALL de W3LPL: contest this weekend")]
    [InlineData("WX de G3XYZ: raining here 1200Z")]
    // WWV / WCY propagation bulletins.
    [InlineData("WWV de VE7CC <18>:   SFI=120, A=7, K=2 1500Z")]
    [InlineData("WCY de DK0WCY-1 <12> : K=1 expK=0 A=4 R=0 SFI=120 1200Z")]
    // Malformed: no frequency.
    [InlineData("DX de W3LPL: K1ABC nice signal 1432Z")]
    // Malformed: DX call has no digit (looks like a word, not a call).
    [InlineData("DX de W3LPL:  14074.0  CQ   calling   1432Z")]
    // Frequency out of plausible range.
    [InlineData("DX de W3LPL:  0.5  K1ABC   CW   1432Z")]
    // Not a DX line at all.
    [InlineData("set/filter on")]
    [InlineData("73 de W3LPL")]
    public void TryParse_NonSpotLines_Rejected(string? line)
    {
        Assert.False(DxSpotLineParser.TryParse(line, out _));
    }

    [Fact]
    public void TryParse_CaseInsensitivePrefix()
    {
        // Some nodes lowercase the prefix.
        Assert.True(DxSpotLineParser.TryParse(
            "dx de w3lpl:  14074.0  k1abc  ft8  1432Z", out var spot));
        Assert.Equal("W3LPL", spot.SpotterCall);
        Assert.Equal("K1ABC", spot.DxCall);
        Assert.Equal("FT8", spot.Mode);
    }
}
