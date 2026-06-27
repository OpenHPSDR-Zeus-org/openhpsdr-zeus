// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Text.RegularExpressions;

namespace Zeus.Dsp.Ft8;

/// <summary>
/// Minimal backend port of the SENDER-extraction half of the frontend
/// <c>ft8-message.ts</c> parser. Spotting (PSK Reporter) only needs to know
/// "who transmitted this decode and, if present, their grid" — it does NOT need
/// the full classification (report/RR73/73/calling-me) the UI uses. Kept here in
/// <c>Zeus.Dsp.Ft8</c> (alongside the decoder) so it has no Server/web
/// dependency; the frontend parser stays the source of truth for the UI.
///
/// Standard FT8/FT4 message grammar (all uppercase, space-separated):
///   CQ [DIRECTIVE] CALL [GRID4]   — sender = CALL, grid if a 4-char Maidenhead
///   TARGET DE PAYLOAD             — sender = DE (token 1); grid only when
///                                   PAYLOAD is a plain 4-char locator
/// Anything else (free text, no plausible callsign) yields false.
/// </summary>
public static class Ft8MessageParse
{
    // A 4-character Maidenhead locator: two A–R letters then two digits.
    private static readonly Regex GridRe = new("^[A-R]{2}[0-9]{2}$", RegexOptions.Compiled);

    // The "core" of a plausible amateur callsign: at least one letter AND one
    // digit, composed only of letters/digits/slash (covers /P, /MM, prefixes).
    private static readonly Regex CallCoreRe = new("^[A-Z0-9/]+$", RegexOptions.Compiled);

    private static readonly char[] Whitespace = { ' ', '\t', '\r', '\n' };

    /// <summary>
    /// Extracts the sender (transmitting) callsign and, when present, the grid
    /// from a decoded FT8/FT4 message line. Never throws. Returns false when no
    /// plausible, reportable callsign can be identified (free text, or a
    /// hashed/unresolvable <c>&lt;...&gt;</c> call we cannot report).
    /// </summary>
    public static bool TryParseSender(string? text, out string call, out string? grid)
    {
        call = "";
        grid = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = text.Trim().ToUpperInvariant().Split(Whitespace, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        // CQ family: "CQ [DIRECTIVE] CALL [GRID]".
        if (tokens[0] == "CQ")
        {
            // A directive (DX / POTA / TEST / a zone number, etc.) sits between
            // CQ and the callsign and is not itself a call.
            int i = 1;
            if (tokens.Length >= 3 && !LooksLikeCall(tokens[1]) && LooksLikeCall(tokens[2]))
                i = 2;

            if (i >= tokens.Length) return false;
            if (!TryRealCall(tokens[i], out call)) return false;

            if (i + 1 < tokens.Length && IsGrid(tokens[i + 1]))
                grid = tokens[i + 1];
            return true;
        }

        // Directed: "TARGET DE PAYLOAD" — sender is the DE call (token 1).
        if (tokens.Length >= 2 && (LooksLikeCall(tokens[0]) || tokens[0].StartsWith('<')))
        {
            if (!TryRealCall(tokens[1], out call)) return false;

            // The grid only appears as a plain Tx1 locator. RR73 also matches the
            // grid regex (R,R,7,3) so it must be excluded explicitly; reports
            // (+05 / R-12) and RRR/73 never match GridRe.
            if (tokens.Length >= 3)
            {
                var payload = tokens[2];
                if (payload != "RR73" && IsGrid(payload))
                    grid = payload;
            }
            return true;
        }

        return false;
    }

    private static bool IsGrid(string token) => GridRe.IsMatch(token);

    // Frontend parity: a hashed <...> token "looks like" a call for grammar
    // disambiguation, as does any token with a letter+digit core.
    private static bool LooksLikeCall(string token)
    {
        if (token.Length >= 2 && token[0] == '<' && token[^1] == '>') return true;
        var core = StripHash(token);
        return HasLetter(core) && HasDigit(core) && CallCoreRe.IsMatch(core);
    }

    // A reportable callsign: the hash markers are stripped and the remainder must
    // be a real call (letter+digit, valid charset). A purely-hashed unknown call
    // (e.g. "<...>") strips to junk and is rejected — we can't report it. A bare
    // 4-char Maidenhead locator (e.g. "FN42") also satisfies the letter+digit core
    // check but is NEVER a reportable callsign; rejecting it here stops free-text
    // decodes such as "K1ABC FN42" or "G0XYZ FN42 73" from uploading a spot for a
    // nonexistent station to a public network.
    private static bool TryRealCall(string token, out string call)
    {
        var core = StripHash(token);
        if (GridRe.IsMatch(core))
        {
            call = "";
            return false;
        }
        if (HasLetter(core) && HasDigit(core) && CallCoreRe.IsMatch(core))
        {
            call = core;
            return true;
        }
        call = "";
        return false;
    }

    private static string StripHash(string token)
    {
        int start = 0, end = token.Length;
        if (end > 0 && token[0] == '<') start = 1;
        if (end > start && token[end - 1] == '>') end--;
        return token.Substring(start, end - start);
    }

    private static bool HasLetter(string s)
    {
        foreach (var c in s) if (c is >= 'A' and <= 'Z') return true;
        return false;
    }

    private static bool HasDigit(string s)
    {
        foreach (var c in s) if (c is >= '0' and <= '9') return true;
        return false;
    }
}
