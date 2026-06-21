// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

internal sealed record AdifRecord(IReadOnlyDictionary<string, string> Fields);

internal static class AdifParser
{
    public static IReadOnlyList<AdifRecord> Parse(string adif)
    {
        if (string.IsNullOrWhiteSpace(adif))
            return [];

        var records = new List<AdifRecord>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pos = FindDataStart(adif);

        while (pos < adif.Length)
        {
            var tagStart = adif.IndexOf('<', pos);
            if (tagStart < 0)
                break;

            var tagEnd = adif.IndexOf('>', tagStart + 1);
            if (tagEnd < 0)
                throw new FormatException("ADIF tag is missing its closing '>'.");

            var tag = adif.Substring(tagStart + 1, tagEnd - tagStart - 1).Trim();
            pos = tagEnd + 1;
            if (tag.Length == 0)
                continue;

            var parts = tag.Split(':', 3);
            var fieldName = parts[0].Trim().ToUpperInvariant();
            if (fieldName.Length == 0)
                continue;

            if (string.Equals(fieldName, "EOH", StringComparison.OrdinalIgnoreCase))
            {
                fields.Clear();
                continue;
            }

            if (string.Equals(fieldName, "EOR", StringComparison.OrdinalIgnoreCase))
            {
                if (fields.Count > 0)
                {
                    records.Add(new AdifRecord(new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase)));
                    fields.Clear();
                }
                continue;
            }

            if (parts.Length < 2)
                continue;

            var lengthText = parts[1].Trim();
            if (!int.TryParse(lengthText, out var length) || length < 0)
                throw new FormatException($"ADIF field '{fieldName}' has an invalid length specifier.");

            if (pos + length > adif.Length)
                throw new FormatException($"ADIF field '{fieldName}' length exceeds the available data.");

            fields[fieldName] = adif.Substring(pos, length);
            pos += length;
        }

        if (fields.Count > 0)
            records.Add(new AdifRecord(new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase)));

        return records;
    }

    private static int FindDataStart(string adif)
    {
        var pos = 0;
        while (pos < adif.Length)
        {
            var tagStart = adif.IndexOf('<', pos);
            if (tagStart < 0)
                return 0;

            var tagEnd = adif.IndexOf('>', tagStart + 1);
            if (tagEnd < 0)
                return 0;

            var tag = adif.Substring(tagStart + 1, tagEnd - tagStart - 1).Trim();
            var fieldName = tag.Split(':', 2)[0].Trim();
            if (string.Equals(fieldName, "EOH", StringComparison.OrdinalIgnoreCase))
                return tagEnd + 1;

            pos = tagEnd + 1;
        }

        return 0;
    }
}
