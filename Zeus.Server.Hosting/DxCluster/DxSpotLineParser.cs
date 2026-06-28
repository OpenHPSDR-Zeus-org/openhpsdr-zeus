// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Zeus.Server.DxCluster;

/// <summary>
/// A single parsed DX-cluster spot line. Pure data — no I/O. <see cref="FreqHz"/>
/// is integer Hz (kHz on the wire × 1000). <see cref="Mode"/> is "" when it
/// cannot be derived from the comment.
/// </summary>
public sealed record DxSpotLine(
    string SpotterCall,
    string DxCall,
    long FreqHz,
    string Comment,
    string Mode,
    string Time);

/// <summary>
/// Pure parser for standard DX-cluster spot lines. No sockets, no state — a
/// static function over a line of text. Handles the DXSpider / AR-Cluster /
/// CC-Cluster spacing variants and rejects everything that is not a spot
/// (banners, prompts, talk / announce / WWV lines).
///
/// Canonical form:
///   <c>DX de &lt;SPOTTER&gt;:   &lt;FREQ_kHz&gt;  &lt;DXCALL&gt;   &lt;comment...&gt;   &lt;HHMMZ&gt;</c>
/// e.g. <c>DX de W3LPL:    14074.0  K1ABC        FT8  -12 dB         1432Z</c>
/// </summary>
public static class DxSpotLineParser
{
    // A callsign-ish token: letters, digits, slash, dash, optional SSID/portable.
    // Deliberately lenient — cluster node calls and portable suffixes vary — but
    // it must contain at least one letter and one digit somewhere so plain words
    // (banner text) are rejected as the spotter/DX call.
    private const string Call = @"[A-Za-z0-9][A-Za-z0-9/\-]*";

    // ^DX de <spotter>:?  <freq>  <dxcall>  <rest...>
    // - "DX de" is case-insensitive and tolerant of extra spaces.
    // - The colon after the spotter is optional (a few nodes omit it).
    // - Freq is kHz, integer or decimal.
    private static readonly Regex SpotRe = new(
        @"^\s*DX\s+de\s+(?<spotter>" + Call + @")\s*:?\s+(?<freq>\d{1,9}(?:\.\d+)?)\s+(?<dx>" + Call + @")\s+(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Trailing UTC time token "1432Z" (3 or 4 digits). Anchored to the end,
    // tolerating an optional 4/6-char grid square after it on some nodes.
    private static readonly Regex TrailingTimeRe = new(
        @"\b(?<time>\d{3,4}Z)\b(?:\s+[A-Z]{2}\d{2}(?:[A-Za-z]{2})?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Try to parse a single line as a DX spot. Returns false for any line that
    /// is not a well-formed spot (login banner, prompt, talk/announce, blank).
    /// </summary>
    public static bool TryParse(string? line, out DxSpotLine spot)
    {
        spot = null!;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var m = SpotRe.Match(line);
        if (!m.Success)
            return false;

        // Frequency: kHz on the wire → integer Hz. Reject implausible values so a
        // stray number-shaped banner line can't masquerade as a spot.
        if (!double.TryParse(m.Groups["freq"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var khz))
            return false;
        if (khz < 1.0 || khz > 10_000_000.0) // 1 kHz .. 10 GHz
            return false;
        var freqHz = (long)Math.Round(khz * 1000.0, MidpointRounding.AwayFromZero);

        var spotter = m.Groups["spotter"].Value.ToUpperInvariant();
        var dxCall = m.Groups["dx"].Value.ToUpperInvariant();
        if (!LooksLikeCall(dxCall))
            return false;

        var rest = m.Groups["rest"].Value.Trim();

        // Pull the trailing time token off the end; the remainder is the comment.
        string time = "";
        string comment = rest;
        var tm = TrailingTimeRe.Match(rest);
        if (tm.Success)
        {
            time = tm.Groups["time"].Value.ToUpperInvariant();
            comment = rest[..tm.Index].Trim();
        }

        var mode = DeriveMode(comment);

        spot = new DxSpotLine(spotter, dxCall, freqHz, comment, mode, time);
        return true;
    }

    // A real callsign has at least one letter AND at least one digit. This is the
    // guard that keeps "DX de NODE: 14074 CQ ..." style noise out — a DX call of
    // "CQ" (no digit) is rejected.
    private static bool LooksLikeCall(string s)
    {
        bool hasLetter = false, hasDigit = false;
        foreach (var c in s)
        {
            if (char.IsLetter(c)) hasLetter = true;
            else if (char.IsDigit(c)) hasDigit = true;
        }
        return hasLetter && hasDigit;
    }

    // Best-effort mode from the comment text. Only the common, unambiguous modes
    // the issue calls out; "" when nothing matches.
    private static readonly (string Token, string Mode)[] ModeTokens =
    {
        ("FT8", "FT8"),
        ("FT4", "FT4"),
        ("RTTY", "RTTY"),
        ("PSK", "PSK"),
        ("JT65", "JT65"),
        ("CW", "CW"),
        ("USB", "SSB"),
        ("LSB", "SSB"),
        ("SSB", "SSB"),
    };

    private static string DeriveMode(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return "";
        var upper = comment.ToUpperInvariant();
        foreach (var (token, mode) in ModeTokens)
        {
            // Word-boundary match so "FT8" in "FT8" but not inside a serial number.
            if (Regex.IsMatch(upper, $@"\b{Regex.Escape(token)}\b"))
                return mode;
        }
        return "";
    }
}
