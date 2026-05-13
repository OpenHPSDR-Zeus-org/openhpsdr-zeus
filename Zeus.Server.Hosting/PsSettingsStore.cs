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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// PureSignal settings persistence. Stores the operator's calibration tuning
// (timing delays, ints/spi preset, ptol mode, auto-attenuate) so it survives
// server restarts. Shares zeus-prefs.db with DspSettingsStore.
//
// Deliberately does NOT persist `PsEnabled` (the master arm) or `PsAuto` /
// `PsSingle` (cal mode) — these reset to safe defaults each session. Same
// pattern as MOX / TUN: arming PS is an operator action, never an automatic
// "resume what we did last time" behaviour. PsHwPeak isn't persisted either
// because RadioService re-derives it per-radio at connect time.
public sealed class PsSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PsSettingsEntry> _entries;
    private readonly ILogger<PsSettingsStore> _log;

    public PsSettingsStore(ILogger<PsSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<PsSettingsEntry>("ps_settings");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);

        _log.LogInformation("PsSettingsStore initialized at {Path}", dbPath);
    }

    public PsSettingsEntry? Get(string profileId = "default")
        => _entries.FindOne(x => x.ProfileId == profileId);

    public void Upsert(PsSettingsEntry entry, string profileId = "default")
    {
        entry.ProfileId = profileId;
        entry.UpdatedUtc = DateTime.UtcNow;
        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
        {
            _entries.Insert(entry);
        }
        else
        {
            entry.Id = existing.Id;
            _entries.Update(entry);
        }
    }

    public void Dispose() => _db.Dispose();

}

public sealed class PsSettingsEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    // Cal-mode default — Auto = continuous adapt. Persisted because operators
    // who prefer single-shot calibration (and run TwoTone manually) want that
    // selection to stick across sessions.
    public bool Auto { get; set; } = true;
    public bool Ptol { get; set; }
    public bool AutoAttenuate { get; set; } = true;
    public double MoxDelaySec { get; set; } = 0.2;
    public double LoopDelaySec { get; set; } = 0.0;
    public double AmpDelayNs { get; set; } = 150.0;
    public string IntsSpiPreset { get; set; } = "16/256";
    // Feedback antenna source — Internal coupler (default) or External
    // (Bypass). Persisted so an operator who runs an external sniffer
    // doesn't have to re-pick it every session.
    public PsFeedbackSource Source { get; set; } = PsFeedbackSource.Internal;
    // Two-tone test generator settings. Persisted so an operator who has
    // dialled in custom IMD test tones (e.g. for a specific filter response
    // or PA test) doesn't have to re-enter them every session. PsEnabled and
    // TwoToneEnabled are intentionally NOT persisted — same operator-action
    // discipline as MOX/TUN — but the dialled-in freqs/mag are.
    // Defaults match tx-store.ts (700/1900/0.49) and pihpsdr.
    public double TwoToneFreq1 { get; set; } = 700.0;
    public double TwoToneFreq2 { get; set; } = 1900.0;
    public double TwoToneMag { get; set; } = 0.49;
    public DateTime UpdatedUtc { get; set; }
}
