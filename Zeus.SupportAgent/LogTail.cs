// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Text;

namespace Zeus.SupportAgent;

/// <summary>
/// Reads the trailing lines of a (small, bounded) on-disk log. Opens shared so it
/// can read a file the backend may still be writing, and is tolerant of a missing
/// or unreadable file — a crash record with an empty tail is better than no record.
/// </summary>
public static class LogTail
{
    /// <summary>Last <paramref name="maxLines"/> lines of one file, oldest first. Empty if missing/unreadable.</summary>
    public static IReadOnlyList<string> ReadLastLines(string? path, int maxLines)
    {
        if (maxLines <= 0 || string.IsNullOrEmpty(path) || !File.Exists(path))
            return [];

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            // Files are size-capped (~2 MiB) so a ring of the last N lines is cheap
            // and avoids holding the whole file.
            var ring = new string[maxLines];
            int count = 0, next = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ring[next] = line;
                next = (next + 1) % maxLines;
                if (count < maxLines) count++;
            }

            var result = new string[count];
            int start = ((next - count) % maxLines + maxLines) % maxLines;
            for (int i = 0; i < count; i++)
                result[i] = ring[(start + i) % maxLines];
            return result;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Trailing lines of the rolling app log, spanning the previous roll if the
    /// active file just rotated and is short. Given the active path
    /// <c>…/zeus-app.log</c> it also considers <c>…/zeus-app.1.log</c> so a crash
    /// that lands moments after a roll still yields a useful tail.
    /// </summary>
    public static IReadOnlyList<string> ReadAppLogTail(string? appLogPath, int maxLines)
    {
        if (maxLines <= 0 || string.IsNullOrEmpty(appLogPath)) return [];

        var active = ReadLastLines(appLogPath, maxLines);
        if (active.Count >= maxLines) return active;

        var rollPath = RolledPath(appLogPath, 1);
        var roll = ReadLastLines(rollPath, maxLines - active.Count);
        if (roll.Count == 0) return active;

        var combined = new List<string>(roll.Count + active.Count);
        combined.AddRange(roll);
        combined.AddRange(active);
        return combined;
    }

    private static string RolledPath(string path, int n)
    {
        var ext = Path.GetExtension(path);
        var stem = path[..(path.Length - ext.Length)];
        return $"{stem}.{n}{ext}";
    }
}
