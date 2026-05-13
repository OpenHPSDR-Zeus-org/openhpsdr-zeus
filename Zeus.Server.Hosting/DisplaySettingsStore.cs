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

// Persists the panadapter background mode (basic / beam-map / image), the
// image-fit variant, and (optionally) a single user-supplied background
// image stored as raw bytes. Lives in the same zeus-prefs.db as PA / band-
// memory / layout — none of these values are sensitive.
//
// Why server-side: the previous implementation stored panBackground +
// backgroundImage in browser localStorage, which is per-origin and per-
// device. Operators connecting from a phone (different origin / device than
// the desktop where the picture was set) saw an empty panel. Moving it to
// LiteDB lets a single setting follow the operator across every browser
// pointed at the Zeus instance.
public sealed class DisplaySettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DisplaySettingsEntry> _docs;
    private readonly ILogger<DisplaySettingsStore> _log;
    private readonly object _sync = new();

    public DisplaySettingsStore(ILogger<DisplaySettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<DisplaySettingsEntry>("display_settings");

        _log.LogInformation("DisplaySettingsStore initialized at {Path}", dbPath);
    }

    public DisplaySettingsDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null)
            {
                return new DisplaySettingsDto(Mode: "basic", Fit: "fill", HasImage: false, ImageMime: null);
            }
            return new DisplaySettingsDto(
                Mode: NormalizeMode(e.Mode),
                Fit: NormalizeFit(e.Fit),
                HasImage: e.ImageBytes is { Length: > 0 },
                ImageMime: string.IsNullOrEmpty(e.ImageMime) ? null : e.ImageMime);
        }
    }

    public void SaveMode(string mode, string fit)
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new DisplaySettingsEntry();
            e.Mode = NormalizeMode(mode);
            e.Fit = NormalizeFit(fit);
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    // Returns (bytes, mime) or null when no image is stored.
    public (byte[] Bytes, string Mime)? GetImage()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null || e.ImageBytes is null || e.ImageBytes.Length == 0) return null;
            var mime = string.IsNullOrEmpty(e.ImageMime) ? "application/octet-stream" : e.ImageMime;
            return (e.ImageBytes, mime);
        }
    }

    public void SaveImage(byte[] bytes, string mime)
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new DisplaySettingsEntry();
            e.ImageBytes = bytes;
            e.ImageMime = string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
    }

    public void DeleteImage()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            if (e is null) return;
            e.ImageBytes = null;
            e.ImageMime = null;
            e.UpdatedUtc = DateTime.UtcNow;
            _docs.Update(e);
        }
    }

    public void Dispose() => _db.Dispose();

    private static string NormalizeMode(string? raw) =>
        raw switch
        {
            "basic" or "beam-map" or "image" => raw,
            _ => "basic",
        };

    private static string NormalizeFit(string? raw) =>
        raw switch
        {
            "fit" or "fill" or "stretch" => raw,
            _ => "fill",
        };

}

public sealed class DisplaySettingsEntry
{
    public int Id { get; set; }
    public string Mode { get; set; } = "basic";
    public string Fit { get; set; } = "fill";
    // Inline byte[] keeps the doc self-contained; LiteDB handles BSON blobs
    // up to 16 MB per document, which is well over any realistic background
    // image. If we ever need bigger, swap to LiteFileStorage and store an
    // id here instead.
    public byte[]? ImageBytes { get; set; }
    public string? ImageMime { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
