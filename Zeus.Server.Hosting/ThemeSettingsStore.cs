// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// Persists the operator's chosen theme ("dark" | "light") and the per-CSS-
// variable colour overrides driven from the Theme Settings panel. Previously
// kept in browser localStorage, which is per-origin and per-device — the
// operator's tablet kept reverting to dark while their desktop sat in light.
// Moving it to LiteDB lets a single look-and-feel follow the operator across
// every browser pointed at the Zeus instance, same pattern as
// DisplaySettingsStore + NrUiPrefsStore.
public sealed class ThemeSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<ThemeSettingsEntry> _docs;
    private readonly ILogger<ThemeSettingsStore> _log;
    private readonly object _sync = new();

    public ThemeSettingsStore(ILogger<ThemeSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<ThemeSettingsEntry>("theme_settings");

        _log.LogInformation("ThemeSettingsStore initialized at {Path}", dbPath);
    }

    public ThemeSettingsDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null)
            {
                return new ThemeSettingsDto(
                    Theme: "dark",
                    Overrides: new Dictionary<string, string>());
            }
            return new ThemeSettingsDto(
                Theme: NormalizeTheme(e.Theme),
                Overrides: SanitiseOverrides(e.Overrides));
        }
    }

    public void Set(string theme, IReadOnlyDictionary<string, string>? overrides)
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new ThemeSettingsEntry();
            e.Theme = NormalizeTheme(theme);
            // Persist a defensive copy as a plain Dictionary<string,string> so
            // LiteDB's BSON mapper has a concrete type to serialise. Sanitise
            // here too — clients can't poison the DB with non-hex blobs.
            e.Overrides = SanitiseOverrides(overrides)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    public void Dispose() => _db.Dispose();

    private static string NormalizeTheme(string? raw) =>
        raw switch
        {
            "dark" or "light" => raw,
            _ => "dark",
        };

    // Accept only CSS custom-property keys ("--xxx") with 6-digit hex values.
    // Anything else is silently dropped — keeps malformed override blobs from
    // making it into the stylesheet.
    private static Dictionary<string, string> SanitiseOverrides(IReadOnlyDictionary<string, string>? raw)
    {
        var clean = new Dictionary<string, string>();
        if (raw is null) return clean;
        foreach (var kv in raw)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || !kv.Key.StartsWith("--", StringComparison.Ordinal)) continue;
            if (kv.Value is null) continue;
            var v = kv.Value.Trim().ToUpperInvariant();
            if (v.Length != 7 || v[0] != '#') continue;
            var hexOk = true;
            for (var i = 1; i < 7; i++)
            {
                var c = v[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) { hexOk = false; break; }
            }
            if (!hexOk) continue;
            clean[kv.Key] = v;
        }
        return clean;
    }
}

public sealed class ThemeSettingsEntry
{
    public int Id { get; set; }
    public string Theme { get; set; } = "dark";
    public Dictionary<string, string> Overrides { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}
