// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Small read-only helpers shared by the known-issue rules: find a probe's
/// section by id, read a key's value out of it, and scan the recent log for a
/// structured marker. Kept deliberately defensive — every helper tolerates
/// missing sections / keys and returns a safe default, so a rule can be written
/// as a chain of these without null-guards everywhere.
/// </summary>
internal static class RuleInspection
{
    /// <summary>The section a given probe id contributed, or null if it didn't run.</summary>
    public static DiagnosticSection? Section(
        IReadOnlyList<DiagnosticSection> sections, string probeId)
    {
        for (int i = 0; i < sections.Count; i++)
            if (string.Equals(sections[i].Id, probeId, StringComparison.OrdinalIgnoreCase))
                return sections[i];
        return null;
    }

    /// <summary>
    /// The value of <paramref name="key"/> in <paramref name="section"/>, or null
    /// if the section is null or the key is absent. Case-insensitive on the key.
    /// </summary>
    public static string? Value(DiagnosticSection? section, string key)
    {
        if (section is null) return null;
        var items = section.Items;
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return items[i].Value;
        return null;
    }

    /// <summary>Convenience: read a key directly out of a probe's section.</summary>
    public static string? Value(
        IReadOnlyList<DiagnosticSection> sections, string probeId, string key)
        => Value(Section(sections, probeId), key);

    /// <summary>True when <paramref name="value"/> reads as a boolean false-ish flag.</summary>
    public static bool IsFalse(string? value) =>
        value is not null &&
        (value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
         value == "0");

    /// <summary>True when <paramref name="value"/> reads as a boolean true-ish flag.</summary>
    public static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
         value == "1");

    /// <summary>
    /// True if any recent-log line contains <paramref name="marker"/>
    /// (case-insensitive substring — the structured markers are ASCII).
    /// </summary>
    public static bool LogContains(IReadOnlyList<string> recentLog, string marker)
    {
        for (int i = 0; i < recentLog.Count; i++)
        {
            var line = recentLog[i];
            if (line is not null &&
                line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True if any recent-log line contains <em>all</em> of the given markers.
    /// Used to spot a single structured line carrying several fields, e.g.
    /// <c>p1.tx.rate ... drv=240 peak=0</c>.
    /// </summary>
    public static bool LogLineContainsAll(IReadOnlyList<string> recentLog, params string[] markers)
    {
        for (int i = 0; i < recentLog.Count; i++)
        {
            var line = recentLog[i];
            if (line is null) continue;
            bool all = true;
            for (int m = 0; m < markers.Length; m++)
            {
                if (!line.Contains(markers[m], StringComparison.OrdinalIgnoreCase))
                {
                    all = false;
                    break;
                }
            }
            if (all) return true;
        }
        return false;
    }

    /// <summary>
    /// Scan recent-log lines for a <c>p1.tx.rate</c> line that reports a non-zero
    /// drive byte (<c>drv=</c>) while the measured peak is zero (<c>peak=0</c>) —
    /// the IQ-write-gate fingerprint (audio present, no IQ on the wire).
    /// </summary>
    public static bool HasTxRateDriveButZeroPeak(IReadOnlyList<string> recentLog)
    {
        for (int i = 0; i < recentLog.Count; i++)
        {
            var line = recentLog[i];
            if (line is null || !line.Contains("p1.tx.rate", StringComparison.OrdinalIgnoreCase))
                continue;
            if (PeakIsZero(line) && DriveIsNonZero(line))
                return true;
        }
        return false;
    }

    private static bool PeakIsZero(string line)
    {
        int v = FieldInt(line, "peak=", -1);
        return v == 0;
    }

    private static bool DriveIsNonZero(string line)
    {
        int v = FieldInt(line, "drv=", 0);
        return v > 0;
    }

    /// <summary>
    /// True if any value in the board section contains <paramref name="needle"/>
    /// (case-insensitive). The board probe's exact key names aren't part of our
    /// contract, so we scan every value rather than guess a key. Returns false
    /// when the board probe didn't run.
    /// </summary>
    public static bool BoardSectionMentions(
        IReadOnlyList<DiagnosticSection> sections, string needle)
    {
        var section = Section(sections, "board");
        if (section is null) return false;
        var items = section.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var v = items[i].Value;
            if (v is not null && v.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True when the connected board is Hermes Lite 2 (HL2).</summary>
    public static bool IsHermesLite2(IReadOnlyList<DiagnosticSection> sections) =>
        BoardSectionMentions(sections, "HermesLite2") ||
        BoardSectionMentions(sections, "Hermes Lite 2") ||
        BoardSectionMentions(sections, "Hermes-Lite 2") ||
        BoardSectionMentions(sections, "HL2");

    /// <summary>
    /// True when the connected board is in the Hermes / Hermes-II family
    /// (Hermes, HermesII, ANAN-10/10E/100/100B). Excludes Hermes Lite 2, which
    /// has its own profile and is matched by <see cref="IsHermesLite2"/>.
    /// </summary>
    public static bool IsHermesClass(IReadOnlyList<DiagnosticSection> sections)
    {
        if (IsHermesLite2(sections)) return false;
        return BoardSectionMentions(sections, "Hermes") ||
               BoardSectionMentions(sections, "ANAN-10") ||
               BoardSectionMentions(sections, "Anan10") ||
               BoardSectionMentions(sections, "ANAN-100") ||
               BoardSectionMentions(sections, "Anan100");
    }

    /// <summary>Parse the integer immediately after <paramref name="token"/> in a log line.</summary>
    private static int FieldInt(string line, string token, int fallback)
    {
        int idx = line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return fallback;
        int start = idx + token.Length;
        int end = start;
        while (end < line.Length && (char.IsDigit(line[end]) || (end == start && line[end] == '-')))
            end++;
        if (end == start) return fallback;
        return int.TryParse(
            line.AsSpan(start, end - start),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed)
            ? parsed
            : fallback;
    }
}
