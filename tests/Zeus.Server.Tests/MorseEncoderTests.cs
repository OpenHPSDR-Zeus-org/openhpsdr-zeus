// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class MorseEncoderTests
{
    // PARIS-method: 20 wpm → 60 ms per dit unit. Picked because the
    // resulting timings stay whole-millisecond (1200/20 = 60) so the
    // asserts read like a Morse-code primer.
    private const int Wpm20Unit = 60;

    [Theory]
    [InlineData(20, 60)]
    [InlineData(12, 100)]
    [InlineData(40, 30)]
    [InlineData(5, 240)]
    public void DitMs_FollowsPARIS(int wpm, int expected)
    {
        Assert.Equal(expected, MorseEncoder.DitMs(wpm));
    }

    [Fact]
    public void Encode_EmptyString_NoSymbols()
    {
        Assert.Empty(MorseEncoder.Encode(string.Empty, 20));
    }

    [Fact]
    public void Encode_SingleE_OneDit()
    {
        // 'E' is a single dit. No surrounding gaps — leading
        // inter-character gap is suppressed at the buffer start.
        var sym = MorseEncoder.Encode("E", 20).ToList();
        Assert.Single(sym);
        Assert.True(sym[0].KeyDown);
        Assert.Equal(Wpm20Unit, sym[0].DurationMs);
    }

    [Fact]
    public void Encode_SingleT_OneDah()
    {
        // 'T' is a single dah (3 units on).
        var sym = MorseEncoder.Encode("T", 20).ToList();
        Assert.Single(sym);
        Assert.True(sym[0].KeyDown);
        Assert.Equal(Wpm20Unit * 3, sym[0].DurationMs);
    }

    [Fact]
    public void Encode_LetterA_DitGapDah()
    {
        // 'A' = .- → dit, intra-element gap, dah.
        var sym = MorseEncoder.Encode("A", 20).ToList();
        Assert.Equal(3, sym.Count);
        Assert.Equal(new CwSymbol(true, Wpm20Unit),     sym[0]); // dit
        Assert.Equal(new CwSymbol(false, Wpm20Unit),    sym[1]); // intra gap
        Assert.Equal(new CwSymbol(true, Wpm20Unit * 3), sym[2]); // dah
    }

    [Fact]
    public void Encode_LowercaseFoldsToUpper()
    {
        Assert.Equal(MorseEncoder.Encode("A", 20), MorseEncoder.Encode("a", 20));
        Assert.Equal(MorseEncoder.Encode("CQ", 20), MorseEncoder.Encode("cq", 20));
    }

    [Fact]
    public void Encode_TwoLetters_HasInterCharGap()
    {
        // "EE" = dit, inter-char gap (3 units), dit.
        var sym = MorseEncoder.Encode("EE", 20).ToList();
        Assert.Equal(3, sym.Count);
        Assert.Equal(new CwSymbol(true, Wpm20Unit),     sym[0]);
        Assert.Equal(new CwSymbol(false, Wpm20Unit * 3), sym[1]);
        Assert.Equal(new CwSymbol(true, Wpm20Unit),     sym[2]);
    }

    [Fact]
    public void Encode_SpaceBetweenWords_EmitsSevenUnitGap()
    {
        // "E E" = dit, word gap (7 units, not 3+7), dit. The word gap
        // absorbs the would-be inter-character gap, so we should see
        // exactly one off-period between the two dits at 7 units total.
        var sym = MorseEncoder.Encode("E E", 20).ToList();
        Assert.Equal(3, sym.Count);
        Assert.Equal(new CwSymbol(true, Wpm20Unit),     sym[0]);
        Assert.Equal(new CwSymbol(false, Wpm20Unit * 7), sym[1]);
        Assert.Equal(new CwSymbol(true, Wpm20Unit),     sym[2]);
    }

    [Fact]
    public void Encode_LeadingSpaces_AreElided()
    {
        // Leading spaces shouldn't burn 7 units of silence before the
        // first dit. Trailing spaces are NOT trimmed — they're harmless
        // (silence after the last element) and dropping them would
        // require a buffered emit. Documented here so the next reader
        // knows it's deliberate, not a bug.
        var sym = MorseEncoder.Encode("   E", 20).ToList();
        Assert.Single(sym);
        Assert.Equal(new CwSymbol(true, Wpm20Unit), sym[0]);
    }

    [Fact]
    public void Encode_TrailingSpaces_EmitOneWordGap()
    {
        // Documents the deliberate "trailing word gap is not trimmed"
        // behaviour. Any number of trailing whitespace chars collapse
        // to a single 7-unit gap (the same word-gap collapsing rule
        // that runs for inter-word spacing).
        var sym = MorseEncoder.Encode("E    ", 20).ToList();
        Assert.Equal(2, sym.Count);
        Assert.Equal(new CwSymbol(true, Wpm20Unit), sym[0]);
        Assert.Equal(new CwSymbol(false, Wpm20Unit * 7), sym[1]);
    }

    [Fact]
    public void Encode_MultipleConsecutiveSpaces_CollapseToOneWordGap()
    {
        // Two spaces in a row should still emit only one 7-unit gap, not
        // two. Same principle as Thetis: a word boundary is a word
        // boundary regardless of how many spaces the operator typed.
        var sym = MorseEncoder.Encode("E    E", 20).ToList();
        var gaps = sym.Where(s => !s.KeyDown).ToList();
        Assert.Single(gaps);
        Assert.Equal(Wpm20Unit * 7, gaps[0].DurationMs);
    }

    [Theory]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('\r')]
    public void Encode_WhitespaceCharactersCountAsSpace(char ws)
    {
        var sym = MorseEncoder.Encode($"E{ws}E", 20).ToList();
        Assert.Equal(3, sym.Count);
        Assert.Equal(new CwSymbol(false, Wpm20Unit * 7), sym[1]);
    }

    [Fact]
    public void Encode_UnknownCharsAreSilentlySkipped()
    {
        // '~' isn't in the table — it should disappear entirely, not
        // synthesize a word gap or a "?" prosign. (Thetis treats unknown
        // entries the same way: silently skip when morsedef.txt has no
        // pattern for a slot.)
        var direct = MorseEncoder.Encode("EE", 20).ToList();
        var withTilde = MorseEncoder.Encode("E~E", 20).ToList();
        Assert.Equal(direct, withTilde);
    }

    [Fact]
    public void Encode_LoopAndLongDashMacros_AreSkippedAsUnknown()
    {
        // Thetis's `"` (loop), `#` (long-dash), `$` (long-space), `&`
        // (digit macro), `_` (reserved) are deliberately absent from
        // our table — they should behave like any other unmapped char
        // and skip cleanly without producing weird timings.
        foreach (char macro in new[] { '"', '#', '$', '&', '_' })
        {
            var sym = MorseEncoder.Encode($"E{macro}E", 20).ToList();
            Assert.Equal(MorseEncoder.Encode("EE", 20), sym);
        }
    }

    [Fact]
    public void Encode_CQDE_ProducesExpectedSequence()
    {
        // Real-world phrase: "CQ DE" at 20 wpm.
        // C = -.-. ; Q = --.- ; (word gap 7u) ; D = -.. ; E = .
        // Expected total elements (key-down + gap) easy to sanity-check.
        var sym = MorseEncoder.Encode("CQ DE", 20).ToList();
        var keyDownCount = sym.Count(s => s.KeyDown);
        // C=4 + Q=4 + D=3 + E=1 = 12 key-down events.
        Assert.Equal(12, keyDownCount);

        // Total transmission time at 20 wpm:
        //   C(-.-.) = 3+1+1+1+3+1+1 = 11 units
        //   inter-char = 3
        //   Q(--.-) = 3+1+3+1+1+1+3 = 13 units
        //   word gap = 7
        //   D(-..)  = 3+1+1+1+1 = 7 units
        //   inter-char = 3
        //   E(.)    = 1 unit
        // Total = 11+3+13+7+7+3+1 = 45 units = 45 * 60 = 2700 ms.
        long totalMs = sym.Sum(s => (long)s.DurationMs);
        Assert.Equal(45 * Wpm20Unit, totalMs);
    }

    [Fact]
    public void Encode_LeadsAlternatesKeyDownAndKeyUp()
    {
        // A well-formed CW stream must never have two consecutive
        // key-down or key-up symbols — that would mean we forgot a gap
        // or fused two pulses. Spot-check on a substantial sample.
        var sym = MorseEncoder.Encode("CQ CQ DE EI6LF EI6LF K", 20).ToList();
        Assert.NotEmpty(sym);
        for (int i = 1; i < sym.Count; i++)
        {
            Assert.NotEqual(sym[i - 1].KeyDown, sym[i].KeyDown);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Encode_RejectsNonPositiveWpm(int wpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MorseEncoder.Encode("E", wpm).ToList());
    }

    [Fact]
    public void Encode_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => MorseEncoder.Encode(null!, 20).ToList());
    }
}
