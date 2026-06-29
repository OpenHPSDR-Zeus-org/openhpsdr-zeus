// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Globalization;
using System.Text;
using Zeus.Contracts;

namespace Zeus.Server.Tci;

/// <summary>
/// TCI protocol string formatting and parsing. All TCI commands are ASCII
/// text frames with the format: <c>command:arg1,arg2,...;</c>
/// Semicolon-terminated, lowercase command names, comma-separated args.
/// </summary>
public static class TciProtocol
{
    // Protocol constants
    public const string ProtocolName = "ExpertSDR3";
    public const string ProtocolVersion = "2.0";
    public const string DeviceName = "Zeus";

    /// <summary>
    /// Build a TCI command string: <c>command:arg1,arg2,...;</c>
    /// Always semicolon-terminated per the wire format.
    /// </summary>
    public static string Command(string name, params object[] args)
    {
        var sb = new StringBuilder();
        sb.Append(name);
        if (args.Length > 0)
        {
            sb.Append(':');
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FormatArg(args[i]));
            }
        }
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Parse a TCI command line. Returns (command, args) or null if malformed.
    /// Input may or may not have a trailing semicolon; we strip it.
    /// </summary>
    public static (string command, string[] args)? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // Strip trailing semicolon if present
        line = line.TrimEnd(';', ' ', '\r', '\n');

        int colonIdx = line.IndexOf(':');
        if (colonIdx < 0)
        {
            // No args — bare command like "ready;"
            return (line.Trim(), Array.Empty<string>());
        }

        string command = line.Substring(0, colonIdx).Trim();
        string argsPart = line.Substring(colonIdx + 1);
        string[] args = argsPart.Split(',', StringSplitOptions.TrimEntries);
        return (command, args);
    }

    /// <summary>
    /// Map Zeus RxMode to TCI modulation string. TCI uses uppercase mode
    /// names: AM, SAM, DSB, LSB, USB, NFM, FM, CWL, CWU, DIGL, DIGU.
    /// </summary>
    public static string ModeToTci(RxMode mode) => mode switch
    {
        RxMode.AM => "AM",
        RxMode.SAM => "SAM",
        RxMode.DSB => "DSB",
        RxMode.LSB => "LSB",
        RxMode.USB => "USB",
        RxMode.FM => "FM",
        RxMode.CWL => "CWL",
        RxMode.CWU => "CWU",
        RxMode.DIGL => "DIGL",
        RxMode.DIGU => "DIGU",
        _ => "USB", // fallback
    };

    /// <summary>
    /// Map TCI modulation string to Zeus RxMode. Case-insensitive.
    /// Returns null if unknown.
    /// </summary>
    public static RxMode? TciToMode(string tciMode)
    {
        return tciMode.ToUpperInvariant() switch
        {
            "AM" => RxMode.AM,
            "SAM" => RxMode.SAM,
            "DSB" => RxMode.DSB,
            "LSB" => RxMode.LSB,
            "USB" => RxMode.USB,
            "FM" => RxMode.FM,
            "NFM" => RxMode.FM, // NFM alias for FM
            "CWL" => RxMode.CWL,
            "CWU" => RxMode.CWU,
            "DIGL" => RxMode.DIGL,
            "DIGU" => RxMode.DIGU,
            _ => null,
        };
    }

    /// <summary>
    /// Format a single argument for TCI wire format. Booleans become
    /// "true"/"false", numbers use invariant culture (dot decimal separator).
    /// </summary>
    private static string FormatArg(object arg)
    {
        return arg switch
        {
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("F1", CultureInfo.InvariantCulture),
            float f => f.ToString("F1", CultureInfo.InvariantCulture),
            _ => arg.ToString() ?? "",
        };
    }

    /// <summary>
    /// Try parse a boolean TCI arg. Accepts "true"/"false" (case-insensitive).
    /// </summary>
    public static bool TryParseBool(string arg, out bool value)
    {
        if (arg.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }
        if (arg.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }

    /// <summary>
    /// Try parse an integer TCI arg.
    /// </summary>
    public static bool TryParseInt(string arg, out int value) =>
        int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Try parse a long integer TCI arg (VFO frequencies are 64-bit).
    /// </summary>
    public static bool TryParseLong(string arg, out long value) =>
        long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Try parse a double TCI arg (volume in dB, etc.).
    /// </summary>
    public static bool TryParseDouble(string arg, out double value) =>
        double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
