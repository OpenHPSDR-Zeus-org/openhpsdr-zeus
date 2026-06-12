// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Tests for the Voyeur Mode callsign extractor (zeus-la5 Phase 2). The phrases
// are the ACTUAL whisper.cpp output from the Phase-0 spike on real eCARS
// (7255 kHz) audio, so this pins the C# decoder to the behavior validated end
// to end against live HF SSB — including Whisper's specific mis-hearings.

using System.Linq;
using Xunit;
using Zeus.Server.Voyeur;

namespace Zeus.Server.Tests;

public class CallsignExtractorTests
{
    [Theory]
    // Net control, spelled in full NATO — the canonical case.
    [InlineData("This is Whiskey Alpha 2 Delta Victor United E-Corps 7255, check ins", "WA2DVU")]
    // VE3 with Whisper's "Foxtrops" mis-hearing of Foxtrot.
    [InlineData("Victor Echo 3, Hotel X-ray, fox drop", "VE3HX")]
    [InlineData("The Victor Echo 3 Hotel X-ray Foxtrops", "VE3HXF")]
    // Clearly-worked check-ins from the spike.
    [InlineData("Kilo Bravo 1, Hotel Romeo Lima", "KB1HRL")]
    [InlineData("Kilo One, Papa November", "K1PN")]
    [InlineData("Kilo Delta Two, Oscar Alpha Mike, mobile", "KD2OAM")]
    [InlineData("Absolutely, this is K-4-U-E-K, standing by", "K4UEK")]
    public void DecodesSpokenCallsign(string transcript, string expected)
    {
        var calls = CallsignExtractor.Extract(transcript);
        Assert.Contains(expected, calls);
    }

    [Fact]
    public void EmptyAndNonCallsignText_YieldNoCandidates()
    {
        Assert.Empty(CallsignExtractor.Extract(null));
        Assert.Empty(CallsignExtractor.Extract(""));
        Assert.Empty(CallsignExtractor.Extract(
            "the band really sucks today, heavy QSB, can really only hear every other word"));
    }

    [Fact]
    public void EmitsTruncations_LongestFirst_ForQrzToDisambiguate()
    {
        // The full call and its fragments are all candidates (QRZ picks the
        // longest that validates — the Phase-0 fragment-collision fix). They're
        // ordered longest-first, so the full call precedes its shorter fragment.
        var calls = CallsignExtractor.Extract(
            "This is Whiskey Alpha 2 Delta Victor. WA2 Delta Victor United, check ins.");
        Assert.Contains("WA2DVU", calls);
        Assert.Contains("WA2DV", calls);
        Assert.True(
            calls.ToList().IndexOf("WA2DVU") < calls.ToList().IndexOf("WA2DV"),
            "longer call should rank before its fragment");
    }

    [Fact]
    public void SentenceBoundaryBreaksRuns()
    {
        // "Victor. WA2" must not merge into one impossible run across the period.
        var calls = CallsignExtractor.Extract("Whiskey Alpha 2 Delta Victor. Kilo One Papa November");
        Assert.Contains("K1PN", calls);
        Assert.DoesNotContain(calls, c => c.Contains("VK", System.StringComparison.Ordinal));
    }

    [Fact]
    public void HandlesAlreadyCollapsedTokens()
    {
        // Whisper sometimes emits the whole call (or prefix+digit) as one token.
        Assert.Contains("N2ZKN", CallsignExtractor.Extract("This is N2ZKN, net control for eCARS"));
        Assert.Contains("N2BK", CallsignExtractor.Extract("Okay, this is N2BK and net control"));
    }

    [Fact]
    public void DoesNotInventCallsFromOrdinaryWords()
    {
        // "one" / "two" are digit words but with no surrounding letters they
        // can't form a callsign; plain prose must not yield a candidate.
        var calls = CallsignExtractor.Extract("I worked one or two stations, nothing in close");
        Assert.Empty(calls);
    }
}
