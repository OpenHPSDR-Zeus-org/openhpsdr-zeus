// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Text;
using Zeus.Contracts;

namespace Zeus.Server.Cat;

/// <summary>
/// Pure parse/format helpers for the Kenwood TS-2000 CAT protocol (the subset
/// Thetis emulates and that WSJT-X / N1MM+ / fldigi / Hamlib require). All
/// I/O lives in <see cref="CatSession"/>; everything here is a side-effect-free
/// transform so it can be unit-tested in isolation. ASCII only.
///
/// Framing: commands are a 2-letter prefix + optional fixed-width args, each
/// terminated by ';'. They may arrive batched ("FA;MD;") or split across TCP
/// segments — <see cref="ExtractCommands"/> handles both.
///
/// The <see cref="BuildIfBody"/> field layout is taken byte-for-byte from
/// Thetis <c>CATCommands.IF()</c> (the authoritative reference): freq(11) +
/// step(4) + RIT/XIT-value(6) + RIT(1) + XIT(1) + membank(3) + TX/RX(1) +
/// mode(1) + FR/FT(1) + scan(1) + split(1) + balance(4) = 35 bytes.
/// </summary>
public static class CatProtocol
{
    public const char Terminator = ';';

    /// <summary>Wrap a payload into a full terminated response, e.g.
    /// <c>Response("FA", "00007074000")</c> → <c>"FA00007074000;"</c>.</summary>
    public static string Response(string cmd, string payload = "") => cmd + payload + Terminator;

    /// <summary>The Kenwood "command not understood" reply.</summary>
    public const string Error = "?;";

    /// <summary>
    /// Split an accumulator into complete commands (terminator stripped) and
    /// the trailing remainder (an incomplete command still arriving). Pure: the
    /// caller keeps the remainder for the next read. Returns whole tokens like
    /// "FA00007074000" or "ID".
    /// </summary>
    public static (List<string> Commands, string Remainder) ExtractCommands(string accumulated)
    {
        var commands = new List<string>();
        int start = 0;
        for (int i = 0; i < accumulated.Length; i++)
        {
            if (accumulated[i] != Terminator) continue;
            var token = accumulated.Substring(start, i - start).Trim();
            if (token.Length > 0) commands.Add(token);
            start = i + 1;
        }
        return (commands, accumulated[start..]);
    }

    /// <summary>The 2-letter command id (upper-cased), or "" if too short.</summary>
    public static string CommandId(string token) =>
        token.Length >= 2 ? token[..2].ToUpperInvariant() : token.ToUpperInvariant();

    /// <summary>The argument portion after the 2-letter id ("" for a bare query).</summary>
    public static string Args(string token) => token.Length > 2 ? token[2..] : "";

    /// <summary>Format a frequency in Hz as the 11-digit Kenwood field. Values
    /// wider than 11 digits keep the least-significant 11 (matches Thetis).</summary>
    public static string FormatFreq(long hz)
    {
        if (hz < 0) hz = 0;
        var s = hz.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return s.Length > 11 ? s[^11..] : s.PadLeft(11, '0');
    }

    /// <summary>Parse an 11-digit (or shorter) Kenwood frequency field to Hz.</summary>
    public static bool TryParseFreq(string field, out long hz) =>
        long.TryParse(field, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out hz);

    /// <summary>Zeus mode → Kenwood TS-2000 mode digit. Unknown/Zeus-only modes
    /// fall back to "2" (USB), matching Thetis's Mode2KString fallback.</summary>
    public static string ModeDigit(RxMode mode) => mode switch
    {
        RxMode.LSB => "1",
        RxMode.USB => "2",
        // Kenwood digit 3 = CW (normal, upper = CWU); 7 = CW-R (reverse = CWL).
        // Matches Thetis Mode2KString and Hamlib (3=RIG_MODE_CW, 7=RIG_MODE_CWR).
        RxMode.CWU => "3",
        RxMode.FM => "4",
        RxMode.AM => "5",
        RxMode.DIGL => "6",
        RxMode.CWL => "7",
        RxMode.DIGU => "9",
        RxMode.SAM => "5",   // no Kenwood equivalent — report as AM
        RxMode.DSB => "2",   // no Kenwood equivalent — report as USB
        RxMode.FreeDv => "2", // runs as USB at the WDSP layer
        _ => "2",
    };

    /// <summary>Kenwood TS-2000 mode digit → Zeus mode, or null if unmapped.</summary>
    public static RxMode? ParseMode(string field) => field switch
    {
        "1" => RxMode.LSB,
        "2" => RxMode.USB,
        "3" => RxMode.CWU,   // Kenwood 3 = CW (normal) → CWU
        "4" => RxMode.FM,
        "5" => RxMode.AM,
        "6" => RxMode.DIGL,
        "7" => RxMode.CWL,   // Kenwood 7 = CW-R (reverse) → CWL
        "9" => RxMode.DIGU,
        _ => null,
    };

    /// <summary>
    /// Map a receive level in dBm to the Kenwood SM meter range (0000–0030).
    /// Approximate, monotonic, Tier-1 (digital-mode/logging clients don't gate
    /// on SM accuracy; precise S-unit calibration is a documented follow-up).
    /// ~-121 dBm → 0, ~-1 dBm → 30 (≈4 dB per step).
    /// </summary>
    public static int SMeter(double dbm)
    {
        int v = (int)Math.Round((dbm + 121.0) / 4.0);
        return Math.Clamp(v, 0, 30);
    }

    /// <summary>4-digit zero-padded SM field, e.g. 14 → "0014".</summary>
    public static string SMeterField(double dbm) =>
        SMeter(dbm).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(4, '0');

    /// <summary>
    /// Build the 35-byte IF response body (NOT including the "IF" prefix or the
    /// ';' terminator). Field widths are exact per Thetis CATCommands.IF().
    /// RIT/XIT are Tier-2; for Tier-1 pass ritOn=xitOn=false (→ "+00000").
    /// </summary>
    public static string BuildIfBody(
        long freqHz, RxMode mode, bool mox, bool split,
        int ritXitHz = 0, bool ritOn = false, bool xitOn = false)
    {
        int it = ritOn ? ritXitHz : (xitOn ? ritXitHz : 0);
        string incr = (it < 0 ? "-" : "+")
            + Math.Min(Math.Abs(it), 99999).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5, '0');

        var sb = new StringBuilder(35);
        sb.Append(FormatFreq(freqHz)); // P1 freq            11
        sb.Append("0000");             // P2 step size        4 (Zeus exposes no tune-step)
        sb.Append(incr);               // P3 RIT/XIT value    6 (sign + 5)
        sb.Append(ritOn ? '1' : '0');  // P4 RIT status       1
        sb.Append(xitOn ? '1' : '0');  // P5 XIT status       1
        sb.Append("000");              // P6 memory bank      3 (dummy)
        sb.Append(mox ? '1' : '0');    // P7 TX/RX status     1
        sb.Append(ModeDigit(mode));    // P8 mode             1
        sb.Append('0');                // P9 FR/FT            1 (dummy)
        sb.Append('0');                // P10 scan            1 (dummy)
        sb.Append(split ? '1' : '0');  // P11 split           1
        sb.Append("0000");             // P12 balance         4 (dummy)
        return sb.ToString();          // total              35
    }
}
