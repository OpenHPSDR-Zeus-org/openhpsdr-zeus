// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Persists the CW operator's preferences (WPM, Farnsworth split, 6 macro
/// slots, sidetone gain/pitch) so the macro pad and slider state survive
/// server restarts. Shares <c>zeus-prefs.db</c> with the other Stores —
/// CW settings aren't sensitive.
///
/// Single-row schema (one operator per backend). Macros is stored as a
/// fixed-length string[6]; absent / shorter persisted arrays are padded
/// to 6 with the seeded defaults on read, so legacy DBs hydrate cleanly.
/// </summary>
public sealed class CwSettingsStore : IDisposable
{
    /// <summary>Default macros — match the previously-hardcoded list in
    /// <c>zeus-web/src/components/design/CwKeyer.tsx</c> so a fresh install
    /// shows the same six buttons the UI used pre-persistence.</summary>
    public static readonly string[] DefaultMacros =
        { "CQ CQ CQ", "TU 73", "QRZ?", "AGN?", "5NN TU", "UR RST" };

    public const int DefaultWpm = 22;
    public const double DefaultSidetoneGainDb = -10.0;
    public const int DefaultSidetoneHz = 600;
    /// <summary>Hard cap on the total number of macros — keeps the UI
    /// list manageable and bounds the broadcast frame size. Operators can
    /// add up to this many slots; <see cref="DefaultMacros"/> seeds the
    /// first six on a fresh install.</summary>
    public const int MaxMacros = 32;
    /// <summary>Hard cap on a single macro to keep wire frames small and
    /// reject runaway paste / accidental long strings at the API edge.</summary>
    public const int MaxMacroChars = 200;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<CwSettingsEntry> _docs;
    private readonly ILogger<CwSettingsStore> _log;
    private readonly object _sync = new();

    public CwSettingsStore(ILogger<CwSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<CwSettingsEntry>("cw_settings");

        _log.LogInformation("CwSettingsStore initialized at {Path}", dbPath);
    }

    public CwSettingsDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null)
                return new CwSettingsDto(
                    Wpm: DefaultWpm,
                    FarnsworthWpm: null,
                    Macros: (string[])DefaultMacros.Clone(),
                    SidetoneGainDb: DefaultSidetoneGainDb,
                    SidetoneHz: DefaultSidetoneHz);

            return new CwSettingsDto(
                Wpm: e.Wpm,
                FarnsworthWpm: e.FarnsworthWpm,
                Macros: SanitizeStored(e.Macros),
                SidetoneGainDb: e.SidetoneGainDb,
                SidetoneHz: e.SidetoneHz);
        }
    }

    /// <summary>
    /// Merge a PATCH-shaped request on top of the persisted row. Null
    /// fields preserve their current value; non-null fields are validated
    /// (Wpm clamped 5..50, sidetone clamped to sane ranges, macros padded /
    /// truncated to <see cref="MacroSlotCount"/> with per-string length cap).
    /// Returns the post-merge snapshot.
    /// </summary>
    public CwSettingsDto Save(CwSettingsSetRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        lock (_sync)
        {
            var existing = _docs.FindAll().FirstOrDefault();
            var entry = existing ?? SeedDefaultsEntry();

            if (req.Wpm is int w)
                entry.Wpm = Math.Clamp(w, 5, 50);
            if (req.FarnsworthWpm is int fw)
                entry.FarnsworthWpm = fw <= 0 ? null : Math.Clamp(fw, 5, 50);
            else if (req.FarnsworthWpm is null && existing is not null)
            {
                // Null in the request keeps the existing value (PATCH semantics).
                // No-op here, just being explicit about the contract.
            }
            if (req.Macros is { } macros)
                entry.Macros = NormalizeMacros(macros);
            if (req.SidetoneGainDb is double g)
                // Operator-safe sidetone band: -60..+0 dB. Above 0 risks
                // hard-clipping; below -60 is effectively silent.
                entry.SidetoneGainDb = Math.Clamp(g, -60.0, 0.0);
            if (req.SidetoneHz is int hz)
                // CW pitch operator preference range. Below 200 is sub-bass
                // and below the WDSP RX bandpass; above 1200 is uncomfortable.
                entry.SidetoneHz = Math.Clamp(hz, 200, 1200);

            entry.UpdatedUtc = DateTime.UtcNow;
            if (existing is null) _docs.Insert(entry);
            else _docs.Update(entry);

            return new CwSettingsDto(
                Wpm: entry.Wpm,
                FarnsworthWpm: entry.FarnsworthWpm,
                Macros: SanitizeStored(entry.Macros),
                SidetoneGainDb: entry.SidetoneGainDb,
                SidetoneHz: entry.SidetoneHz);
        }
    }

    public void Dispose() => _db.Dispose();

    private static CwSettingsEntry SeedDefaultsEntry() => new()
    {
        Wpm = DefaultWpm,
        FarnsworthWpm = null,
        Macros = (string[])DefaultMacros.Clone(),
        SidetoneGainDb = DefaultSidetoneGainDb,
        SidetoneHz = DefaultSidetoneHz,
    };

    /// <summary>Normalise a persisted Macros array on read. LiteDB
    /// serialises empty strings as null on the round-trip — translate
    /// back to empty so the wire shape is consistent. Variable length:
    /// honours whatever the operator saved, up to <see cref="MaxMacros"/>.</summary>
    private static string[] SanitizeStored(string[]? persisted)
    {
        if (persisted is null || persisted.Length == 0) return Array.Empty<string>();
        int n = Math.Min(persisted.Length, MaxMacros);
        var result = new string[n];
        for (int i = 0; i < n; i++) result[i] = persisted[i] ?? string.Empty;
        return result;
    }

    /// <summary>Validate + length-cap a user-supplied Macros array. The
    /// length is preserved (operator chooses how many macros to keep —
    /// add and delete are first-class operations) but capped at
    /// <see cref="MaxMacros"/>; per-string content is capped at
    /// <see cref="MaxMacroChars"/>. Null entries become empty strings.</summary>
    private static string[] NormalizeMacros(string[] macros)
    {
        int n = Math.Min(macros.Length, MaxMacros);
        var result = new string[n];
        for (int i = 0; i < n; i++)
        {
            string raw = macros[i] ?? string.Empty;
            result[i] = raw.Length > MaxMacroChars ? raw[..MaxMacroChars] : raw;
        }
        return result;
    }
}

public sealed class CwSettingsEntry
{
    public int Id { get; set; }
    public int Wpm { get; set; } = CwSettingsStore.DefaultWpm;
    public int? FarnsworthWpm { get; set; }
    public string[] Macros { get; set; } = (string[])CwSettingsStore.DefaultMacros.Clone();
    public double SidetoneGainDb { get; set; } = CwSettingsStore.DefaultSidetoneGainDb;
    public int SidetoneHz { get; set; } = CwSettingsStore.DefaultSidetoneHz;
    public DateTime UpdatedUtc { get; set; }
}
