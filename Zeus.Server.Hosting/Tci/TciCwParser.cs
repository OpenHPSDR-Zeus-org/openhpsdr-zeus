// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using System.Globalization;
using System.Text;

namespace Zeus.Server.Tci;

/// <summary>
/// Pure-function parsers for the four TCI CW commands routed into
/// <c>CwEngine</c> by <see cref="TciSession"/> (PR zeus-j3t). Kept out of
/// the session class so the wire-format edge cases (repeat-count trailing
/// arg, optional duration, empty-text rejection) have direct unit-test
/// coverage independent of the WebSocket plumbing.
///
/// Wire references — ExpertSDR3 TCI 2.0 spec and the per-spec rows in
/// <c>docs/wiki/TCI.md</c>. None of these parsers mutate state; they
/// return null on any malformed input rather than throwing, matching
/// the silent-drop convention used by the rest of <see cref="TciSession"/>.
/// </summary>
public static class TciCwParser
{
    /// <summary>Upper bound on the <c>cw_msg</c> trailing repeat count.
    /// Above this we treat the trailing integer as part of the text
    /// (contesters routinely include numeric serials like <c>5NN 001</c>
    /// in the suffix; we don't want to swallow them as repeats). Picked
    /// to match the conservative "logger sent &gt; 999 repeats" never-
    /// happens-in-practice limit; bigger values look like a count-typo
    /// or a serial.</summary>
    public const int MaxCwMsgRepeats = 99;

    /// <summary>WPM clamp matches <c>CwEngine.WpmMin/WpmMax</c>. Kept
    /// here so a parser-level rejection logs once rather than the engine
    /// silently clamping on every send.</summary>
    public const int MinWpm = 5;
    public const int MaxWpm = 50;

    /// <summary>Parsed shape of <c>cw_msg:rx,part[,part...][,repeat]</c>.
    /// The text is the concatenation of the string parts (no separator —
    /// matches ExpertSDR3 wire convention where prefix / callsign / suffix
    /// already include their own spacing).</summary>
    public readonly record struct CwMsgArgs(string Text, int Repeats);

    /// <summary>Parsed shape of <c>cw_macros:slot</c>. Slot is 1-based
    /// per the TCI spec; the session resolves it against
    /// <see cref="Zeus.Server.CwSettingsStore"/>.</summary>
    public readonly record struct CwMacrosArgs(int Slot1Based);

    /// <summary>Parsed shape of <c>cw_macros_speed:wpm</c>. Null indicates
    /// the query form (no args) — caller responds with the current value.</summary>
    public readonly record struct CwMacrosSpeedArgs(int? Wpm);

    /// <summary>Parsed shape of <c>keyer:rx,bool[,durationMs]</c>. Duration
    /// is the optional auto-release timer from TCI 1.9.1.</summary>
    public readonly record struct KeyerArgs(bool KeyDown, int? DurationMs);

    /// <summary>
    /// Parse <c>cw_msg:rx,part[,part...][,N];</c>. Accepts both the
    /// single-text form (<c>cw_msg:0,CQ TEST;</c>) and the prefix /
    /// callsign / suffix form (<c>cw_msg:0,CQ ,EI6LF, K;</c>). When the
    /// trailing argument parses as a small integer (1..<see cref="MaxCwMsgRepeats"/>)
    /// it is treated as a repeat count; otherwise it is appended verbatim
    /// to the text — see <see cref="MaxCwMsgRepeats"/> for the bound.
    /// </summary>
    public static CwMsgArgs? ParseCwMsg(string[] args)
    {
        if (args is null || args.Length < 2) return null;
        if (!TciProtocol.TryParseInt(args[0], out _)) return null;

        int endIdx = args.Length;
        int repeats = 1;
        if (args.Length >= 3
            && int.TryParse(args[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r)
            && r >= 1 && r <= MaxCwMsgRepeats)
        {
            repeats = r;
            endIdx = args.Length - 1;
        }

        var sb = new StringBuilder();
        for (int i = 1; i < endIdx; i++) sb.Append(args[i]);
        string text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new CwMsgArgs(text, repeats);
    }

    /// <summary>
    /// Parse <c>cw_macros:slot;</c>. Slot is 1-based, rejected if &lt;= 0
    /// or above <see cref="Zeus.Server.CwSettingsStore.MaxMacros"/>.
    /// </summary>
    public static CwMacrosArgs? ParseCwMacros(string[] args)
    {
        if (args is null || args.Length < 1) return null;
        if (!TciProtocol.TryParseInt(args[0], out int slot)) return null;
        if (slot < 1 || slot > Zeus.Server.CwSettingsStore.MaxMacros) return null;
        return new CwMacrosArgs(slot);
    }

    /// <summary>
    /// Parse <c>cw_macros_speed:wpm;</c> (set) or <c>cw_macros_speed;</c>
    /// (query). WPM outside <see cref="MinWpm"/>..<see cref="MaxWpm"/> is
    /// rejected at this layer so the parser-level test can pin the bound;
    /// the engine still clamps defensively at <c>SendAsync</c>.
    /// </summary>
    public static CwMacrosSpeedArgs? ParseCwMacrosSpeed(string[] args)
    {
        if (args is null || args.Length == 0)
            return new CwMacrosSpeedArgs(null);
        if (!TciProtocol.TryParseInt(args[0], out int wpm)) return null;
        if (wpm < MinWpm || wpm > MaxWpm) return null;
        return new CwMacrosSpeedArgs(wpm);
    }

    /// <summary>
    /// Parse <c>keyer:rx,bool[,durationMs];</c>. Returns null on missing
    /// receiver or missing boolean. Duration ≤ 0 is treated as absent —
    /// matches the spec convention "0 = no timer, key stays down until
    /// keyer:0".
    /// </summary>
    public static KeyerArgs? ParseKeyer(string[] args)
    {
        if (args is null || args.Length < 2) return null;
        if (!TciProtocol.TryParseInt(args[0], out _)) return null;
        if (!TciProtocol.TryParseBool(args[1], out bool keyDown)) return null;
        int? durationMs = null;
        if (args.Length >= 3 && TciProtocol.TryParseInt(args[2], out int d) && d > 0)
            durationMs = d;
        return new KeyerArgs(keyDown, durationMs);
    }
}
