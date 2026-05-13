// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// UI layout persistence — stores the operator's panel arrangement so it
// survives page reloads and reinstalls. Shares zeus-prefs.db with
// BandMemoryStore (layout isn't sensitive).
//
// Two collections live here:
//   - `ui_layout` (legacy, one row): single-workspace JSON. Kept for read
//     so old clients continue to work and so the multi-layout system can
//     migrate it on first read of a radio that has no v2 entry yet.
//   - `ui_layouts_v2` (issue #241): one row per radio, holding a list of
//     named layouts plus which one is currently active. RadioKey is the
//     stringified board kind (HermesLite2, AnanG2, …) or "default" while
//     no radio is connected.
public sealed class LayoutStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<LayoutEntry> _legacy;
    private readonly ILiteCollection<RadioLayoutsEntry> _v2;
    private readonly ILogger<LayoutStore> _log;
    private readonly object _lock = new();

    public LayoutStore(ILogger<LayoutStore> log)
    {
        _log = log;
        var dbPath = PrefsDbPath.Get();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _legacy = _db.GetCollection<LayoutEntry>("ui_layout");
        _legacy.EnsureIndex(x => x.ProfileId, unique: true);
        _v2 = _db.GetCollection<RadioLayoutsEntry>("ui_layouts_v2");
        _v2.EnsureIndex(x => x.RadioKey, unique: true);

        _log.LogInformation("LayoutStore initialized at {Path}", dbPath);
    }

    // -----------------------------------------------------------------
    // Legacy single-layout API (back-compat)
    // -----------------------------------------------------------------

    public UiLayoutDto? Get(string profileId = "default")
    {
        var e = _legacy.FindOne(x => x.ProfileId == profileId);
        return e is null
            ? null
            : new UiLayoutDto(e.LayoutJson, new DateTimeOffset(e.UpdatedUtc).ToUnixTimeMilliseconds());
    }

    public void Upsert(string layoutJson, string profileId = "default")
    {
        lock (_lock)
        {
            var existing = _legacy.FindOne(x => x.ProfileId == profileId);
            if (existing is null)
            {
                _legacy.Insert(new LayoutEntry
                {
                    ProfileId = profileId,
                    LayoutJson = layoutJson,
                    UpdatedUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.LayoutJson = layoutJson;
                existing.UpdatedUtc = DateTime.UtcNow;
                _legacy.Update(existing);
            }
        }
    }

    public void Delete(string profileId = "default")
        => _legacy.DeleteMany(x => x.ProfileId == profileId);

    // -----------------------------------------------------------------
    // Multi-layout per-radio API (issue #241)
    // -----------------------------------------------------------------

    /// <summary>
    /// Read all named layouts for the given radio. When the radio has no v2
    /// row yet AND a legacy single-layout exists, the legacy row is migrated
    /// in: it becomes a single "Default" layout under the radio key. When
    /// nothing exists, returns an empty list — the client seeds the Default.
    /// </summary>
    public RadioLayoutsDto GetForRadio(string radioKey)
    {
        radioKey = NormalizeRadioKey(radioKey);
        lock (_lock)
        {
            var entry = _v2.FindOne(x => x.RadioKey == radioKey);
            if (entry is null)
            {
                // First-read migration: bring the legacy row in (if any) under
                // the "default" radio key only, so we don't auto-attach a
                // single saved layout to every board the operator plugs in.
                if (radioKey == "default")
                {
                    var legacy = _legacy.FindOne(x => x.ProfileId == "default");
                    if (legacy is not null && !string.IsNullOrWhiteSpace(legacy.LayoutJson))
                    {
                        var seeded = new RadioLayoutsEntry
                        {
                            RadioKey = radioKey,
                            ActiveLayoutId = "default",
                            Layouts = new List<NamedLayoutEntry>
                            {
                                new()
                                {
                                    LayoutId = "default",
                                    Name = "Default",
                                    LayoutJson = legacy.LayoutJson,
                                    UpdatedUtc = legacy.UpdatedUtc,
                                },
                            },
                        };
                        _v2.Insert(seeded);
                        return ToDto(seeded);
                    }
                }
                return new RadioLayoutsDto(radioKey, Array.Empty<NamedLayoutDto>(), string.Empty);
            }
            return ToDto(entry);
        }
    }

    /// <summary>
    /// Upsert one named layout. Creates the radio's row on first call.
    /// If there is no active layout yet, the new layout becomes active.
    /// `icon` and `description` are optional; null means "leave as-is on
    /// existing rows / empty on insert".
    /// </summary>
    public RadioLayoutsDto UpsertNamed(
        string radioKey,
        string layoutId,
        string name,
        string layoutJson,
        string? icon = null,
        string? description = null)
    {
        radioKey = NormalizeRadioKey(radioKey);
        layoutId = NormalizeLayoutId(layoutId);
        if (string.IsNullOrWhiteSpace(name)) name = layoutId;
        var normalisedIcon = NormalizeIcon(icon);
        var normalisedDescription = NormalizeDescription(description);

        lock (_lock)
        {
            var entry = _v2.FindOne(x => x.RadioKey == radioKey) ?? new RadioLayoutsEntry
            {
                RadioKey = radioKey,
                ActiveLayoutId = layoutId,
                Layouts = new List<NamedLayoutEntry>(),
            };

            var existing = entry.Layouts.FirstOrDefault(l => l.LayoutId == layoutId);
            if (existing is null)
            {
                entry.Layouts.Add(new NamedLayoutEntry
                {
                    LayoutId = layoutId,
                    Name = name,
                    LayoutJson = layoutJson,
                    UpdatedUtc = DateTime.UtcNow,
                    Icon = normalisedIcon ?? string.Empty,
                    Description = normalisedDescription ?? string.Empty,
                });
            }
            else
            {
                existing.Name = name;
                existing.LayoutJson = layoutJson;
                existing.UpdatedUtc = DateTime.UtcNow;
                if (icon is not null) existing.Icon = normalisedIcon ?? string.Empty;
                if (description is not null) existing.Description = normalisedDescription ?? string.Empty;
            }

            if (string.IsNullOrEmpty(entry.ActiveLayoutId))
                entry.ActiveLayoutId = layoutId;

            if (entry.Id == 0) _v2.Insert(entry);
            else _v2.Update(entry);

            return ToDto(entry);
        }
    }

    /// <summary>
    /// Mark the given layoutId as active for this radio. No-op if the id is
    /// not among the radio's saved layouts.
    /// </summary>
    public RadioLayoutsDto SetActive(string radioKey, string layoutId)
    {
        radioKey = NormalizeRadioKey(radioKey);
        layoutId = NormalizeLayoutId(layoutId);
        lock (_lock)
        {
            var entry = _v2.FindOne(x => x.RadioKey == radioKey);
            if (entry is null)
                return new RadioLayoutsDto(radioKey, Array.Empty<NamedLayoutDto>(), string.Empty);
            if (!entry.Layouts.Any(l => l.LayoutId == layoutId))
                return ToDto(entry);
            entry.ActiveLayoutId = layoutId;
            _v2.Update(entry);
            return ToDto(entry);
        }
    }

    /// <summary>
    /// Delete the given layout. If it was active, the first remaining layout
    /// becomes active. If it was the last one, the row's ActiveLayoutId
    /// resets to empty (the client re-seeds Default).
    /// </summary>
    public RadioLayoutsDto DeleteNamed(string radioKey, string layoutId)
    {
        radioKey = NormalizeRadioKey(radioKey);
        layoutId = NormalizeLayoutId(layoutId);
        lock (_lock)
        {
            var entry = _v2.FindOne(x => x.RadioKey == radioKey);
            if (entry is null)
                return new RadioLayoutsDto(radioKey, Array.Empty<NamedLayoutDto>(), string.Empty);
            entry.Layouts.RemoveAll(l => l.LayoutId == layoutId);
            if (entry.ActiveLayoutId == layoutId)
                entry.ActiveLayoutId = entry.Layouts.FirstOrDefault()?.LayoutId ?? string.Empty;
            _v2.Update(entry);
            return ToDto(entry);
        }
    }

    private static RadioLayoutsDto ToDto(RadioLayoutsEntry e) => new(
        e.RadioKey,
        e.Layouts
            .Select(l => new NamedLayoutDto(
                l.LayoutId,
                l.Name,
                l.LayoutJson,
                new DateTimeOffset(l.UpdatedUtc).ToUnixTimeMilliseconds(),
                string.IsNullOrEmpty(l.Icon) ? null : l.Icon,
                string.IsNullOrEmpty(l.Description) ? null : l.Description))
            .ToList(),
        e.ActiveLayoutId);

    // The radio key keeps to a small alphabet so it survives JSON round-trips
    // and URL query params without escaping. BoardKind values already match
    // (e.g. "HermesLite2", "AnanG2"), but operators sending freeform strings
    // get sanitised here.
    private static string NormalizeRadioKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "default";
        var trimmed = raw.Trim();
        var safe = new string(trimmed.Where(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray());
        return string.IsNullOrEmpty(safe) ? "default" : safe;
    }

    private static string NormalizeLayoutId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "default";
        var trimmed = raw.Trim();
        var safe = new string(trimmed.Where(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return string.IsNullOrEmpty(safe) ? "default" : safe;
    }

    // Icons are typically a single emoji. We don't restrict the codepoint set
    // (emoji + variation selectors + ZWJ sequences would be hostile to enumerate)
    // but we do cap the length so a misbehaving client can't park an essay in
    // the icon field.
    private static string? NormalizeIcon(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return string.Empty;
        return trimmed.Length > 16 ? trimmed[..16] : trimmed;
    }

    private static string? NormalizeDescription(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return string.Empty;
        return trimmed.Length > 256 ? trimmed[..256] : trimmed;
    }

    public void Dispose() => _db.Dispose();

}

public sealed class LayoutEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
}

public sealed class RadioLayoutsEntry
{
    public int Id { get; set; }
    public string RadioKey { get; set; } = string.Empty;
    public string ActiveLayoutId { get; set; } = string.Empty;
    public List<NamedLayoutEntry> Layouts { get; set; } = new();
}

public sealed class NamedLayoutEntry
{
    public string LayoutId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    // Optional presentation fields (issue #241 follow-up). Older rows that
    // predate these columns deserialise as empty strings under LiteDB.
    public string Icon { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
