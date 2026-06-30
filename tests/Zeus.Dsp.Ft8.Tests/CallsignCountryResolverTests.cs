// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins the offline DXCC-prefix country resolver used to fill the FT8 decode
// table's Country column (FT8 never transmits country). Best-effort floor — the
// table is intentionally a subset of cty.dat, so these cases assert the
// documented behaviour (longest-prefix wins, compound/portable handling), not
// exhaustive DXCC coverage.

using Zeus.Dsp.Ft8;

namespace Zeus.Dsp.Ft8.Tests;

public sealed class CallsignCountryResolverTests
{
    [Theory]
    [InlineData("K1ABC", "USA")]
    [InlineData("W5XYZ", "USA")]
    [InlineData("N9WAR", "USA")]
    [InlineData("AA4ZZ", "USA")]
    [InlineData("VE3ABC", "CAN")]
    [InlineData("VA7XY", "CAN")]
    [InlineData("G0XYZ", "ENG")]
    [InlineData("M0ABC", "ENG")]
    [InlineData("2E0AAA", "ENG")]
    [InlineData("GM4XYZ", "SCO")]   // GM beats the generic G
    [InlineData("GW1ABC", "WAL")]
    [InlineData("GI4ABC", "NIR")]
    [InlineData("DL1ABC", "GER")]
    [InlineData("DJ0XYZ", "GER")]
    [InlineData("JA1XYZ", "JPN")]
    [InlineData("7K4AAA", "JPN")]
    [InlineData("VK2ABC", "AUS")]
    [InlineData("ZL1ABC", "NZL")]
    [InlineData("I2ABC", "ITA")]
    [InlineData("EA3XYZ", "ESP")]
    [InlineData("F5ABC", "FRA")]
    [InlineData("OH2ABC", "FIN")]
    [InlineData("SM5ABC", "SWE")]
    [InlineData("UA3ABC", "RUS")]
    [InlineData("RK9AX", "RUS")]
    [InlineData("UR5XYZ", "UKR")]
    [InlineData("BA1ABC", "CHN")]
    [InlineData("BV1XYZ", "TWN")]   // BV beats nothing generic; distinct from BA
    [InlineData("HL2ABC", "KOR")]
    [InlineData("PY2ABC", "BRA")]
    [InlineData("LU1ABC", "ARG")]
    public void Resolve_KnownPrefixes(string call, string expected)
    {
        Assert.Equal(expected, CallsignCountryResolver.Resolve(call));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Equal("GER", CallsignCountryResolver.Resolve("dl1abc"));
        Assert.Equal("USA", CallsignCountryResolver.Resolve("k1abc"));
    }

    [Theory]
    // Slash-PREFIX form: the prefix segment names the entity.
    [InlineData("DL/K1ABC", "GER")]
    [InlineData("EA8/DL1ABC", "CNR")]   // Canary Is. — EA8 beats EA
    [InlineData("OH/SM5ABC", "FIN")]
    // Slash-appended prefix: the shorter prefix segment still wins.
    [InlineData("K1ABC/VE3", "CAN")]
    [InlineData("W1ABC/KH6", "HI")]     // Hawaii
    public void Resolve_CompoundPrefixWins(string call, string expected)
    {
        Assert.Equal(expected, CallsignCountryResolver.Resolve(call));
    }

    [Theory]
    // A LEADING short segment that also doubles as a status suffix must be read as
    // the location prefix, not stripped. MM = Scotland (CEPT visitor form), R =
    // Russia. Regression guard: MM/DL1ABC used to resolve to GER (MM dropped).
    [InlineData("MM/DL1ABC", "SCO")]    // Scotland visitor — NOT Germany
    [InlineData("R/DL1ABC", "RUS")]     // leading R = Russia prefix, not stripped
    public void Resolve_LeadingStatusLikePrefix_IsLocation(string call, string expected)
    {
        Assert.Equal(expected, CallsignCountryResolver.Resolve(call));
    }

    [Theory]
    // The SAME tokens, when TRAILING, are still stripped as status suffixes so the
    // home call resolves. This is the position-aware counterpart of the cases above.
    [InlineData("DL1ABC/MM", "GER")]    // trailing MM (maritime mobile) → Germany
    [InlineData("K1ABC/R", "USA")]      // trailing R stripped → USA
    public void Resolve_TrailingStatusSuffix_IsStripped(string call, string expected)
    {
        Assert.Equal(expected, CallsignCountryResolver.Resolve(call));
    }

    [Theory]
    // Portable / status suffixes are stripped; the home call resolves.
    [InlineData("G0XYZ/P", "ENG")]
    [InlineData("DL1ABC/M", "GER")]
    [InlineData("GM4ABC/MM", "SCO")]
    [InlineData("K1ABC/QRP", "USA")]
    [InlineData("VK2ABC/R", "AUS")]
    [InlineData("W1ABC/7", "USA")]      // bare region digit — still USA
    public void Resolve_StripsPortableSuffixes(string call, string expected)
    {
        Assert.Equal(expected, CallsignCountryResolver.Resolve(call));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("CQ")]              // not a call
    [InlineData("Q7ZZZ")]          // unallocated-ish prefix not in the floor table
    [InlineData("1ABC")]           // leading digit, no entity letter
    [InlineData("///")]            // only separators
    public void Resolve_UnknownOrInvalid_ReturnsNull(string? call)
    {
        Assert.Null(CallsignCountryResolver.Resolve(call));
    }
}
