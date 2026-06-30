// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Dsp.Ft8;

/// <summary>
/// Best-effort, pure-data resolver from an amateur callsign to an ABBREVIATED
/// DXCC country/entity label. FT8/FT4 messages never transmit the country, so
/// the FT8 workspace derives it from the callsign prefix here.
///
/// This is a SELF-CONTAINED FLOOR, not a full cty.dat: it covers roughly the
/// ~90 most-active DXCC entities by longest-prefix match. There is no cty.dat
/// in-tree (see zeus-web/src/dsp/ft8-qso-log.ts) and the only richer source
/// (QRZ) is subscription-gated, rate-limited and blocking — unusable per
/// decode. Unknown prefixes return <c>null</c> (the UI shows a blank cell).
///
/// Abbreviation format: a short 2–4 letter uppercase label, biased toward the
/// familiar ITU/contest shorthand (USA, CAN, ENG, GER, JPN, AUS, …). It is a
/// display hint, not an ADIF COUNTRY or DXCC number, and intentionally collapses
/// some sub-entities to keep the table legible.
///
/// Pure and cross-platform: no I/O, no culture-sensitive operations, no statics
/// that vary by platform — safe on macOS / Windows / Linux / arm64 / Pi.
/// </summary>
public static class CallsignCountryResolver
{
    // Portable / status suffixes that do NOT change the resolved entity for our
    // purposes (e.g. K1ABC/P, G0XYZ/MM, DL1ABC/QRP). A bare single digit (region
    // change, e.g. W1ABC/7) is handled separately in the splitter.
    private static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "P", "M", "MM", "AM", "QRP", "R", "A", "LH", "B",
    };

    /// <summary>
    /// Resolve an abbreviated country label from a callsign, or <c>null</c> when
    /// the prefix is unknown or the input is not a plausible call. Never throws.
    /// Handles compound calls: a slash-prefix wins (DL/K1ABC → Germany), a
    /// slash-appended prefix wins (K1ABC/VE3 → Canada), and portable suffixes are
    /// stripped (G0XYZ/P → England).
    /// </summary>
    public static string? Resolve(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return null;

        var prefix = LocationPrefixToken(callsign.Trim().ToUpperInvariant());
        if (prefix.Length == 0) return null;

        // Longest-prefix match: try the leading 4,3,2,1 characters in turn so a
        // specific entry (GM = Scotland) beats the generic one (G = England).
        int max = Math.Min(4, prefix.Length);
        for (int len = max; len >= 1; len--)
        {
            if (Table.TryGetValue(prefix[..len], out var country))
                return country;
        }
        return null;
    }

    /// <summary>
    /// Reduce a (possibly compound) call to the single token whose prefix names
    /// the operating entity. Splits on '/', drops portable/status suffixes and
    /// bare region digits, then — for a true PREFIX/CALL or CALL/PREFIX compound —
    /// picks the shorter remaining token as the location indicator (DL in
    /// DL/K1ABC, VE3 in K1ABC/VE3). Returns "" when nothing plausible remains.
    ///
    /// Suffix stripping is POSITION-AWARE: a status token (P/M/MM/AM/R/…) is only
    /// dropped when it is a TRAILING segment. The first (leading) segment is always
    /// kept as a candidate location prefix, because several status tokens double as
    /// real CEPT prefixes (MM = Scotland, AM = Spain, R = Russia). This keeps the
    /// standard visitor form MM/DL1ABC → Scotland while still stripping DL1ABC/MM
    /// (maritime-mobile) → Germany.
    /// </summary>
    private static string LocationPrefixToken(string call)
    {
        if (!call.Contains('/'))
            return IsCallChars(call) ? call : string.Empty;

        var core = new List<string>();
        var segments = call.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            // Only a TRAILING segment may be a status suffix (/P, /MM, /QRP). The
            // leading segment is never stripped, so MM/DL1ABC keeps MM (Scotland).
            if (i > 0 && Suffixes.Contains(seg)) continue;
            if (IsAllDigits(seg)) continue;               // /7 region change
            if (!IsCallChars(seg)) continue;              // junk
            core.Add(seg);
        }

        if (core.Count == 0) return string.Empty;
        if (core.Count == 1) return core[0];

        // PREFIX/CALL or CALL/PREFIX: the location indicator is the shorter token
        // (a bare prefix like DL/VE3/KH6 is shorter than the full home call). On a
        // tie, keep the first — the leading slash-prefix form is the common one.
        var shortest = core[0];
        foreach (var seg in core)
            if (seg.Length < shortest.Length) shortest = seg;
        return shortest;
    }

    private static bool IsCallChars(string s)
    {
        foreach (var c in s)
            if (!((c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9')))
                return false;
        return s.Length > 0;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
            if (c is < '0' or > '9') return false;
        return s.Length > 0;
    }

    // Prefix → abbreviated entity. Keys are 1–4 chars; longest-prefix wins, so a
    // 2-char key (GM) overrides the generic 1-char key (G). Best-effort floor —
    // extend as bench experience dictates. Ordinal lookup (keys are A–Z/0–9).
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        // ---- North America ----
        ["K"] = "USA", ["W"] = "USA", ["N"] = "USA",
        ["AA"] = "USA", ["AB"] = "USA", ["AC"] = "USA", ["AD"] = "USA",
        ["AE"] = "USA", ["AF"] = "USA", ["AG"] = "USA", ["AI"] = "USA",
        ["AJ"] = "USA", ["AK"] = "USA", ["AL"] = "USA",
        ["KH6"] = "HI", ["AH6"] = "HI", ["NH6"] = "HI", ["WH6"] = "HI",
        ["KL7"] = "AK", ["AL7"] = "AK", ["NL7"] = "AK", ["WL7"] = "AK",
        ["VE"] = "CAN", ["VA"] = "CAN", ["VO"] = "CAN", ["VY"] = "CAN", ["VB"] = "CAN",
        ["XE"] = "MEX", ["XF"] = "MEX", ["4A"] = "MEX", ["6D"] = "MEX",

        // ---- Caribbean / Central America (common DX) ----
        ["KP4"] = "PR", ["KP2"] = "VI", ["CO"] = "CUB", ["CM"] = "CUB",
        ["HI"] = "DOM", ["HH"] = "HTI", ["J3"] = "GRD", ["FG"] = "GLP",
        ["TI"] = "CTR", ["TG"] = "GTM", ["YN"] = "NCA", ["HP"] = "PAN",

        // ---- South America ----
        ["PY"] = "BRA", ["PP"] = "BRA", ["PR"] = "BRA", ["PT"] = "BRA",
        ["PU"] = "BRA", ["PV"] = "BRA", ["ZZ"] = "BRA",
        ["LU"] = "ARG", ["LW"] = "ARG", ["L2"] = "ARG", ["AY"] = "ARG", ["AZ"] = "ARG",
        ["CE"] = "CHL", ["CA"] = "CHL", ["XQ"] = "CHL", ["XR"] = "CHL",
        ["HK"] = "COL", ["HJ"] = "COL", ["YV"] = "VEN", ["YY"] = "VEN",
        ["OA"] = "PER", ["CX"] = "URU", ["CP"] = "BOL", ["HC"] = "ECU",
        ["ZP"] = "PAR",

        // ---- Western Europe ----
        ["G"] = "ENG", ["M"] = "ENG", ["2E"] = "ENG",
        ["GM"] = "SCO", ["MM"] = "SCO", ["2M"] = "SCO",
        ["GW"] = "WAL", ["MW"] = "WAL", ["2W"] = "WAL",
        ["GI"] = "NIR", ["MI"] = "NIR", ["2I"] = "NIR",
        ["GD"] = "IOM", ["GU"] = "GSY", ["GJ"] = "JSY",
        ["EI"] = "IRL", ["EJ"] = "IRL",
        ["F"] = "FRA", ["DA"] = "GER", ["DB"] = "GER", ["DC"] = "GER",
        ["DD"] = "GER", ["DF"] = "GER", ["DG"] = "GER", ["DH"] = "GER",
        ["DJ"] = "GER", ["DK"] = "GER", ["DL"] = "GER", ["DM"] = "GER",
        ["DO"] = "GER", ["DP"] = "GER", ["DR"] = "GER",
        ["I"] = "ITA", ["EA"] = "ESP", ["EB"] = "ESP", ["EC"] = "ESP",
        ["ED"] = "ESP", ["EE"] = "ESP", ["EF"] = "ESP", ["EG"] = "ESP", ["EH"] = "ESP",
        ["CT"] = "POR", ["CR"] = "POR", ["CS"] = "POR",
        ["PA"] = "NED", ["PB"] = "NED", ["PC"] = "NED", ["PD"] = "NED",
        ["PE"] = "NED", ["PF"] = "NED", ["PG"] = "NED", ["PH"] = "NED", ["PI"] = "NED",
        ["ON"] = "BEL", ["OO"] = "BEL", ["OP"] = "BEL", ["OQ"] = "BEL",
        ["OR"] = "BEL", ["OS"] = "BEL", ["OT"] = "BEL",
        ["LX"] = "LUX", ["HB9"] = "SUI", ["HB0"] = "LIE", ["HB"] = "SUI",
        ["OE"] = "AUT", ["EA6"] = "BAL",

        // ---- Northern Europe ----
        ["OH"] = "FIN", ["OF"] = "FIN", ["OG"] = "FIN", ["OI"] = "FIN", ["OH0"] = "ALD",
        ["SM"] = "SWE", ["SA"] = "SWE", ["SB"] = "SWE", ["SC"] = "SWE",
        ["SD"] = "SWE", ["SE"] = "SWE", ["SF"] = "SWE", ["SG"] = "SWE",
        ["SH"] = "SWE", ["SI"] = "SWE", ["SJ"] = "SWE", ["SK"] = "SWE",
        ["SL"] = "SWE", ["7S"] = "SWE", ["8S"] = "SWE",
        ["LA"] = "NOR", ["LB"] = "NOR", ["LC"] = "NOR", ["LD"] = "NOR",
        ["LG"] = "NOR", ["LJ"] = "NOR", ["LN"] = "NOR",
        ["OZ"] = "DEN", ["OU"] = "DEN", ["OV"] = "DEN", ["OW"] = "DEN", ["5P"] = "DEN", ["5Q"] = "DEN",
        ["TF"] = "ISL",

        // ---- Eastern Europe / Balkans / Baltics ----
        ["SP"] = "POL", ["SN"] = "POL", ["SO"] = "POL", ["SQ"] = "POL", ["SR"] = "POL", ["3Z"] = "POL",
        ["OK"] = "CZE", ["OL"] = "CZE", ["OM"] = "SVK",
        ["HA"] = "HUN", ["HG"] = "HUN",
        ["YO"] = "ROU", ["YP"] = "ROU", ["YQ"] = "ROU", ["YR"] = "ROU",
        ["LZ"] = "BUL", ["YU"] = "SRB", ["YT"] = "SRB",
        ["9A"] = "CRO", ["S5"] = "SVN", ["E7"] = "BIH",
        ["Z3"] = "MKD", ["ZA"] = "ALB", ["Z6"] = "KOS",
        ["SV"] = "GRE", ["SW"] = "GRE", ["SX"] = "GRE", ["SY"] = "GRE", ["SZ"] = "GRE", ["J4"] = "GRE",
        ["YL"] = "LVA", ["LY"] = "LTU", ["ES"] = "EST",
        ["UA"] = "RUS", ["UB"] = "RUS", ["UC"] = "RUS", ["UD"] = "RUS",
        ["UE"] = "RUS", ["UF"] = "RUS", ["UG"] = "RUS", ["UH"] = "RUS", ["UI"] = "RUS",
        ["R"] = "RUS",
        ["UR"] = "UKR", ["US"] = "UKR", ["UT"] = "UKR", ["UU"] = "UKR",
        ["UV"] = "UKR", ["UW"] = "UKR", ["UX"] = "UKR", ["UY"] = "UKR", ["UZ"] = "UKR", ["EM"] = "UKR", ["EO"] = "UKR",
        ["EU"] = "BLR", ["EV"] = "BLR", ["EW"] = "BLR",
        ["4L"] = "GEO", ["EK"] = "ARM", ["4J"] = "AZE", ["4K"] = "AZE",

        // ---- Asia ----
        ["JA"] = "JPN", ["JB"] = "JPN", ["JC"] = "JPN", ["JD"] = "JPN",
        ["JE"] = "JPN", ["JF"] = "JPN", ["JG"] = "JPN", ["JH"] = "JPN",
        ["JI"] = "JPN", ["JJ"] = "JPN", ["JK"] = "JPN", ["JL"] = "JPN",
        ["JM"] = "JPN", ["JN"] = "JPN", ["JO"] = "JPN", ["JP"] = "JPN",
        ["JQ"] = "JPN", ["JR"] = "JPN", ["JS"] = "JPN",
        ["7J"] = "JPN", ["7K"] = "JPN", ["7L"] = "JPN", ["7M"] = "JPN", ["7N"] = "JPN",
        ["8J"] = "JPN", ["8N"] = "JPN",
        ["BA"] = "CHN", ["BD"] = "CHN", ["BG"] = "CHN", ["BH"] = "CHN",
        ["BI"] = "CHN", ["BY"] = "CHN", ["BT"] = "CHN",
        ["BV"] = "TWN", ["BU"] = "TWN", ["BW"] = "TWN", ["BX"] = "TWN",
        ["HL"] = "KOR", ["DS"] = "KOR", ["DT"] = "KOR", ["6K"] = "KOR", ["6L"] = "KOR", ["6M"] = "KOR", ["6N"] = "KOR",
        ["VU"] = "IND", ["AT"] = "IND", ["8T"] = "IND", ["VT"] = "IND",
        ["HS"] = "THA", ["E2"] = "THA", ["9M"] = "MAL", ["9V"] = "SGP",
        ["YB"] = "INA", ["YC"] = "INA", ["YD"] = "INA", ["YE"] = "INA", ["YF"] = "INA", ["YG"] = "INA", ["YH"] = "INA",
        ["DU"] = "PHL", ["DV"] = "PHL", ["DW"] = "PHL", ["DX"] = "PHL", ["DY"] = "PHL", ["DZ"] = "PHL", ["4D"] = "PHL",
        ["XV"] = "VTN", ["3W"] = "VTN", ["AP"] = "PAK", ["S2"] = "BAN",
        ["4S"] = "SRI", ["A4"] = "OMA", ["A6"] = "UAE", ["A7"] = "QAT",
        ["A9"] = "BHR", ["HZ"] = "SAU", ["7Z"] = "SAU", ["8Z"] = "SAU",
        ["9K"] = "KWT", ["YK"] = "SYR", ["YI"] = "IRQ", ["EP"] = "IRN", ["EQ"] = "IRN",
        ["4X"] = "ISR", ["4Z"] = "ISR", ["JY"] = "JOR", ["OD"] = "LBN",
        ["TA"] = "TUR", ["TB"] = "TUR", ["TC"] = "TUR", ["YA"] = "AFG", ["UN"] = "KAZ", ["UP"] = "KAZ",

        // ---- Oceania ----
        ["VK"] = "AUS", ["AX"] = "AUS", ["VI"] = "AUS",
        ["ZL"] = "NZL", ["ZM"] = "NZL", ["ZK"] = "NZL",
        ["KH2"] = "GUM", ["FK"] = "NCL", ["FO"] = "FPO", ["3D2"] = "FIJ",
        ["DU1"] = "PHL", ["P2"] = "PNG", ["H4"] = "SOL", ["YJ"] = "VUT",
        ["KH8"] = "ASA", ["5W"] = "SAM", ["A3"] = "TON",

        // ---- Africa ----
        ["ZS"] = "RSA", ["ZR"] = "RSA", ["ZT"] = "RSA", ["ZU"] = "RSA",
        ["SU"] = "EGY", ["CN"] = "MAR", ["7X"] = "ALG", ["3V"] = "TUN",
        ["5A"] = "LBY", ["EL"] = "LBR", ["5N"] = "NIG", ["5U"] = "NGR",
        ["9G"] = "GHA", ["TR"] = "GAB", ["TY"] = "BEN", ["TU"] = "CIV",
        ["5R"] = "MAD", ["5Z"] = "KEN", ["5H"] = "TAN", ["5X"] = "UGA",
        ["9J"] = "ZAM", ["Z2"] = "ZIM", ["C9"] = "MOZ", ["V5"] = "NAM",
        ["A2"] = "BOT", ["7P"] = "LES", ["3DA"] = "SWZ", ["D2"] = "AGL",
        ["9Q"] = "DRC", ["TJ"] = "CAM", ["3C"] = "GEQ", ["6W"] = "SEN",
        ["EA8"] = "CNR", ["EA9"] = "CEU", ["CT3"] = "MDR", ["D4"] = "CPV", ["FR"] = "REU",
    };
}
