// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

// Persists the single "send RX audio to the radio's onboard speaker/headphone
// jacks" toggle for Protocol-1 codec radios (Hermes, ANAN-10/10E/100/100B,
// ANAN-100D/200D, Orion). The Saturn/G2 appliance speaker path (Protocol-2,
// SaturnSpeakerAudioSink) is independent and is NOT governed by this setting.
//
// Default is OFF (opt-in). Zeus already plays RX audio host-side (browser /
// native sink); auto-enabling the radio-side codec output would make every P1
// operator hear doubled audio on connect. Operators who want the radio's own
// speaker turn it on explicitly, and the choice survives a backend restart.
// Lives in the same zeus-prefs.db as the other settings stores.
public sealed class RadioSpeakerSettingsStore : IDisposable
{
    public const bool DefaultEnabled = false;

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RadioSpeakerSettingsEntry> _docs;
    private readonly ILogger<RadioSpeakerSettingsStore> _log;
    private readonly object _sync = new();
    private bool _cacheLoaded;
    private bool _cachedEnabled = DefaultEnabled;

    /// <summary>Raised after <see cref="Set"/> changes the stored value.</summary>
    public event Action? Changed;

    public RadioSpeakerSettingsStore(ILogger<RadioSpeakerSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<RadioSpeakerSettingsEntry>("radio_speaker_settings");

        _log.LogInformation("RadioSpeakerSettingsStore initialized at {Path}", dbPath);
    }

    /// <summary>Current toggle state. Cheap (in-memory cache after first read).</summary>
    public bool Enabled
    {
        get
        {
            lock (_sync)
            {
                EnsureCacheLocked();
                return _cachedEnabled;
            }
        }
    }

    public void Set(bool enabled)
    {
        bool changed;
        lock (_sync)
        {
            EnsureCacheLocked();
            changed = _cachedEnabled != enabled;

            var e = _docs.FindAll().FirstOrDefault() ?? new RadioSpeakerSettingsEntry();
            e.Enabled = enabled;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);

            _cachedEnabled = enabled;
            _cacheLoaded = true;
        }

        if (changed) Changed?.Invoke();
    }

    public void Dispose() => _db.Dispose();

    private void EnsureCacheLocked()
    {
        if (_cacheLoaded) return;
        // A fresh / pre-feature DB has no row — fall back to the safe default OFF.
        _cachedEnabled = _docs.FindAll().FirstOrDefault()?.Enabled ?? DefaultEnabled;
        _cacheLoaded = true;
    }
}

public sealed class RadioSpeakerSettingsEntry
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
