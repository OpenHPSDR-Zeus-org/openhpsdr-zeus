// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.RegularExpressions;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Pure, deterministic PII/secret scrubber for log + free-text lines that ship
/// in a "Report a problem" diagnostic bundle. The goal is to keep a line useful
/// for diagnosis (callsigns, board names, firmware versions, frequencies all
/// survive) while removing things that identify the operator or leak secrets:
/// passwords/tokens, email addresses, IP/MAC addresses, the username embedded in
/// home-directory paths, and the precise tail of a 6-character Maidenhead grid.
///
/// No I/O, no locale-dependent parsing, culture-invariant. All regexes are
/// compiled once and reused; anchoring is deliberately conservative so ordinary
/// prose is left intact.
/// </summary>
public static class Redaction
{
    private const RegexOptions Opts =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // key=value / key: value where key is a known secret-ish name. The value is
    // everything up to whitespace, a quote, comma, semicolon, or end-of-line so
    // we don't swallow the rest of a structured line. An optional auth scheme
    // word (Bearer/Basic/Token) immediately after the separator is preserved so
    // "Authorization: Bearer xyz" masks the actual credential, not the scheme.
    private static readonly Regex SecretKv = new(
        @"\b(password|passwd|pwd|secret|token|apikey|api[_-]?key|bearer|authorization)\b(\s*[:=]\s*)(""?)(?:(Bearer|Basic|Token)\s+)?([^\s""',;]+)",
        Opts);

    // Email address. Local-part and domain both collapse to ***.
    private static readonly Regex Email = new(
        @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b",
        Opts);

    // MAC address (six colon- or hyphen-separated hex octets). Matched BEFORE
    // IPv6 so we don't confuse it with an address; replaced with a fixed mask.
    private static readonly Regex Mac = new(
        @"\b(?:[0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}\b",
        Opts);

    // IPv4 dotted quad, each octet 0-255, word-boundaried so version strings
    // like "10.0.0" partials inside longer tokens are not clipped mid-number.
    private static readonly Regex Ipv4 = new(
        @"\b(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\b",
        Opts);

    // IPv6, optionally bracketed. Two disjoint shapes so a 2-colon timestamp
    // (HH:MM:SS) can never match:
    //   (a) any token containing "::" (compressed form), with hex groups around it;
    //   (b) the full uncompressed form of exactly eight hex groups (seven colons).
    // A "HH:MM:SS.fff" timestamp has no "::" and only two colons, so neither fires.
    private static readonly Regex Ipv6 = new(
        @"\[?(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4})?(?::[0-9A-Fa-f]{1,4})*\]?" +
        @"|\[?::(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{1,4}\]?" +
        @"|\[?(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}\]?",
        Opts);

    // Home-directory username in a path. Three platform shapes; capture the
    // leading prefix so it is preserved and only the <name> segment is masked.
    private static readonly Regex UnixHomeUsers = new(
        @"(/Users/|/home/)[^/\\\s:]+",
        Opts);
    private static readonly Regex WinHomeUsers = new(
        @"([A-Za-z]:\\Users\\)[^\\/\s:]+",
        Opts);

    // 6-character Maidenhead grid (field-square-subsquare), e.g. FN31pr. Keep
    // the 4-char field+square (coarse, useful), drop the precise subsquare.
    // Anchored with word boundaries and a strict shape so words like "letter"
    // or "an12cd"-style tokens elsewhere are not mangled.
    private static readonly Regex GridSquare6 = new(
        @"\b([A-R]{2}\d{2})[a-x]{2}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Scrub a single line. Returns the line with PII/secrets masked.</summary>
    public static string Scrub(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;

        string s = line;

        // Secrets first: a token could itself look like an email/grid otherwise.
        s = SecretKv.Replace(s, static m =>
        {
            string scheme = m.Groups[4].Success ? m.Groups[4].Value + " " : string.Empty;
            return string.Concat(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, scheme, "***");
        });

        // MAC before IPv6 (a MAC shares the colon-hex shape).
        s = Mac.Replace(s, "xx:xx:xx:xx:xx:xx");
        s = Email.Replace(s, "***@***");
        s = Ipv4.Replace(s, "x.x.x.x");
        s = Ipv6.Replace(s, "[ipv6]");

        s = UnixHomeUsers.Replace(s, static m => m.Groups[1].Value + "<user>");
        s = WinHomeUsers.Replace(s, static m => m.Groups[1].Value + "<user>");

        s = GridSquare6.Replace(s, static m => m.Groups[1].Value);

        return s;
    }

    /// <summary>Scrub every line in the input, preserving order.</summary>
    public static IReadOnlyList<string> ScrubAll(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<string>();
        foreach (var line in lines)
            result.Add(Scrub(line));
        return result;
    }
}
