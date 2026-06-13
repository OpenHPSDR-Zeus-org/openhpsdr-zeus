// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

using System.Text.RegularExpressions;

namespace Zeus.Server.Voyeur;

/// <summary>
/// Extracts candidate amateur callsigns from an ASR transcript of an HF SSB
/// "over" (Voyeur Mode, zeus-la5 Phase 2). Decodes spoken NATO phonetics
/// ("Whiskey Alpha 2 Delta Victor" → WA2DV) and digit words, tolerating the
/// specific mis-hearings Whisper produces on noisy HF voice (validated against
/// the Phase-0 spike on real eCARS audio: foxtrot→"foxtrops", india→"indi",
/// uniform→"unites/united", already-collapsed "wa2", etc.).
///
/// Output is candidates ranked by occurrence. The DOWNSTREAM step (QRZ
/// validation) is what separates a confirmed callsign from phonetic-decode
/// noise — see the Phase-0 finding that truncated decodes (WA2D vs WA2DV) can
/// both be real calls, so the caller prefers the longest QRZ-validated
/// candidate and cross-checks the operator's spoken name.
/// </summary>
public static partial class CallsignExtractor
{
    private static readonly Dictionary<string, char> Phon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alpha"] = 'A', ["bravo"] = 'B', ["charlie"] = 'C', ["delta"] = 'D',
        ["echo"] = 'E', ["foxtrot"] = 'F', ["foxtrops"] = 'F', ["foxtrop"] = 'F',
        ["golf"] = 'G', ["hotel"] = 'H', ["india"] = 'I', ["indi"] = 'I',
        ["indy"] = 'I', ["juliet"] = 'J', ["juliett"] = 'J', ["kilo"] = 'K',
        ["lima"] = 'L', ["mike"] = 'M', ["november"] = 'N', ["oscar"] = 'O',
        ["papa"] = 'P', ["papas"] = 'P', ["quebec"] = 'Q', ["romeo"] = 'R',
        ["sierra"] = 'S', ["tango"] = 'T', ["uniform"] = 'U', ["unites"] = 'U',
        ["united"] = 'U', ["victor"] = 'V', ["whiskey"] = 'W', ["xray"] = 'X',
        ["yankee"] = 'Y', ["zulu"] = 'Z',
    };

    private static readonly Dictionary<string, char> Digit = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3',
        ["four"] = '4', ["five"] = '5', ["six"] = '6', ["seven"] = '7',
        ["eight"] = '8', ["nine"] = '9', ["niner"] = '9',
    };

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    // Callsign anchor: 1-2 prefix letters + 1-2 digits. The suffix (1-4 letters)
    // is taken from the run after the anchor.
    [GeneratedRegex(@"[A-Z]{1,2}[0-9]{1,2}")]
    private static partial Regex AnchorRegex();

    // "X-ray" / "x ray" is the phonetic for X but Whisper writes it as two
    // words; collapse to "xray" so it stays one run character. Same for the
    // rarely-split "x-ray" in callsign readbacks.
    [GeneratedRegex(@"\bx[\s\-]?ray\b", RegexOptions.IgnoreCase)]
    private static partial Regex XrayRegex();

    /// <summary>
    /// Candidate callsigns from a transcript, ordered LONGEST-first then by
    /// occurrence. Longest-first is deliberate: each decoded run emits every
    /// valid-length truncation (WA2D, WA2DV, WA2DVU, WA2DVUE…), and the caller
    /// (QRZ validation) prefers the longest candidate that resolves to a real
    /// licensee — which is how the Phase-0 fragment-collision (WA2D vs WA2DVU
    /// both being real calls) is resolved correctly.
    /// </summary>
    public static IReadOnlyList<string> Extract(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return Array.Empty<string>();
        transcript = XrayRegex().Replace(transcript, "xray");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var run in DecodeRuns(transcript))
            foreach (var cand in CandidatesFromRun(run))
                counts[cand] = counts.TryGetValue(cand, out var n) ? n + 1 : 1;

        return counts.Keys
            .OrderByDescending(c => c.Length)
            .ThenByDescending(c => counts[c])
            .ThenBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    // For one decoded run, emit the valid-length truncations rooted at each
    // prefix+digit anchor: anchor + 1..4 of the immediately-following letters.
    private static IEnumerable<string> CandidatesFromRun(string run)
    {
        foreach (Match a in AnchorRegex().Matches(run))
        {
            int i = a.Index + a.Length;
            int letters = 0;
            while (i + letters < run.Length && char.IsLetter(run[i + letters]) && letters < 4)
                letters++;
            for (int L = 1; L <= letters; L++)
                yield return run.Substring(a.Index, a.Length + L);
        }
    }

    // Longest a decoded run can grow before we flush it — caps weak-word
    // absorption so a callsign readback can't swallow the rest of a sentence.
    // (2 prefix + 2 digit + 4 suffix + slack.)
    private const int MaxRunLength = 9;

    // Build runs of decodable callsign characters. STRONG tokens — exact NATO
    // phonetic words, digit words, bare single letters/digits, already-collapsed
    // "wa2"/"n2zkn" tokens — always contribute. WEAK tokens (any other spoken
    // word) contribute their FIRST LETTER, but ONLY once the run already holds a
    // real NATO phonetic: that's how hams' CUSTOM phonetics are handled
    // ("Kilo Echo 8 Jolly Shikers" → KE8 + J + S = KE8JS), since a custom
    // phonetic always starts with the letter it stands for. Requiring a real
    // phonetic first keeps ordinary conversation ("I got 8 of them") from
    // manufacturing callsigns — and QRZ validation filters whatever slips
    // through. A sentence terminator (. ! ? ;) or the length cap flushes the run.
    private static IEnumerable<string> DecodeRuns(string text)
    {
        var run = new System.Text.StringBuilder();
        bool strong = false; // run contains ≥1 exact NATO phonetic word
        int prevEnd = -1;

        foreach (Match tok in TokenRegex().Matches(text))
        {
            if (run.Length > 0 && prevEnd >= 0)
            {
                var gap = text.AsSpan(prevEnd, tok.Index - prevEnd);
                if (gap.IndexOfAny('.', '!', '?') >= 0 || gap.IndexOf(';') >= 0)
                {
                    yield return run.ToString();
                    run.Clear();
                    strong = false;
                }
            }
            prevEnd = tok.Index + tok.Length;

            var t = tok.Value;
            string? piece = null;
            bool isPhonetic = false;
            if (Phon.TryGetValue(t, out var pc)) { piece = pc.ToString(); isPhonetic = true; }
            else if (Digit.TryGetValue(t, out var dc)) piece = dc.ToString();
            else if (t.Length == 1 && char.IsLetter(t[0])) piece = char.ToUpperInvariant(t[0]).ToString();
            else if (t.Length == 1 && char.IsDigit(t[0])) piece = t;
            else if (IsCollapsedPrefix(t)) { piece = t.ToUpperInvariant(); isPhonetic = true; }
            else if (run.Length > 0 && strong && char.IsLetter(t[0]))
                // WEAK word inside an active callsign readback → custom phonetic,
                // take its first letter.
                piece = char.ToUpperInvariant(t[0]).ToString();

            if (piece is not null)
            {
                run.Append(piece);
                if (isPhonetic) strong = true;
                if (run.Length >= MaxRunLength)
                {
                    yield return run.ToString();
                    run.Clear();
                    strong = false;
                }
            }
            else if (run.Length > 0)
            {
                yield return run.ToString();
                run.Clear();
                strong = false;
            }
        }
        if (run.Length > 0) yield return run.ToString();
    }

    // "wa2", "k4", "n2zkn"-style already-spelled token: 1-2 letters then a
    // digit, optionally more letters (Whisper occasionally emits the whole
    // call as one token). Pure letter words are NOT collapsed prefixes (they'd
    // swallow ordinary words), so require an embedded digit.
    private static bool IsCollapsedPrefix(string t)
    {
        bool sawDigit = false;
        foreach (var c in t)
        {
            if (!char.IsLetterOrDigit(c)) return false;
            if (char.IsDigit(c)) sawDigit = true;
        }
        return sawDigit && t.Length >= 2 && t.Length <= 8 && char.IsLetter(t[0]);
    }
}
