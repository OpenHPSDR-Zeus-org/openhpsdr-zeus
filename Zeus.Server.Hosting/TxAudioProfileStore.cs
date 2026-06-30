// SPDX-License-Identifier: GPL-2.0-or-later
//
// Persists unified "TX Audio Profiles" — a single operator-named macro that
// captures the entire TX-audio shaping state (mic/leveler scalars, the whole
// TxLeveling + CFC configs, TX bandpass, processing route, suite chain shape,
// every plugin's settings, and the fidelity target). This store REPLACES both
// the named audio-suite plugin profiles (AudioProfileStore, for the TX route)
// and the fixed 3-up TX station profiles (TxStationProfileStore).
//
// Mirrors the TxStationProfileStore / FilterPresetStore pattern: a JSON-blob
// row per profile in a single LiteDB collection ("tx_audio_profiles") sharing
// zeus-prefs.db, plus a single-row "tx_audio_profile_last_loaded" pointer table
// (the TxFidelityPolicyStore single-row pattern). Lock-guarded; no schema
// migrations (LiteDB tolerates rows from older builds with missing fields).

using System.Text.Json;
using LiteDB;
using Zeus.Contracts;

// LiteDB also exposes a JsonSerializer type; this store serializes with
// System.Text.Json, so resolve the bare name to it.
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Zeus.Server;

public sealed class TxAudioProfileStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Folder-mirror output: human-readable, so a profile dropped in the folder
    // (or shared between operators) is easy to eyeball and hand-edit.
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // Each profile is ALSO mirrored to <prefs-dir>/tx-audio-profiles/<id>.json so
    // the whole catalog lives as plain files on disk (portable, shareable,
    // importable). LiteDB stays the source of truth; the folder is a write-through
    // mirror — best-effort, never allowed to break a DB write.
    private const string ProfileDirName = "tx-audio-profiles";
    private const int DefaultLowCutHz = 150;
    private const int DefaultHighCutHz = 2900;
    private const int DefaultTargetSpectralDensity = 55;

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<TxAudioProfileEntry> _profiles;
    private readonly ILiteCollection<TxAudioProfileLastLoadedEntry> _lastLoaded;
    private readonly ILogger<TxAudioProfileStore> _log;
    private readonly object _sync = new();
    private readonly string _profileDir;

    public TxAudioProfileStore(ILogger<TxAudioProfileStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Mirror folder sits next to the active prefs DB, so a ZEUS_PREFS_PATH
        // override (dev `/run fresh`, CI, tests) isolates the folder too.
        var prefsDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        _profileDir = Path.Combine(string.IsNullOrEmpty(prefsDir) ? "." : prefsDir, ProfileDirName);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _profiles = _db.GetCollection<TxAudioProfileEntry>("tx_audio_profiles");
        // Race-safe unique index seed (FilterPresetStore pattern): parallel
        // WebApplicationFactory hosts on CI can both reach EnsureIndex; swallow
        // the duplicate-key throw so the second host doesn't fault.
        try { _profiles.EnsureIndex(x => x.ProfileId, unique: true); }
        catch (LiteException ex) when (ex.Message.Contains("INDEX_DUPLICATE_KEY", StringComparison.OrdinalIgnoreCase)) { }
        _lastLoaded = _db.GetCollection<TxAudioProfileLastLoadedEntry>("tx_audio_profile_last_loaded");

        // Make sure every profile already in the DB is present in the folder.
        try { SyncFolder(); }
        catch (Exception ex) { _log.LogWarning(ex, "TX audio profile folder sync at startup failed"); }

        _log.LogInformation("TxAudioProfileStore initialized at {Path} (folder {Folder})", dbPath, _profileDir);
    }

    /// <summary>The on-disk folder every profile is mirrored into as JSON.</summary>
    public string ProfileFolder => _profileDir;

    public static string NormalizeId(string id) => (id ?? "").Trim().ToLowerInvariant();

    public static TxAudioProfileDto Sanitize(TxAudioProfileDto profile, string? fallbackId = null, string? fallbackName = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var id = NormalizeId(profile.Id);
        if (string.IsNullOrWhiteSpace(id))
            id = NormalizeId(fallbackId ?? "");
        if (string.IsNullOrWhiteSpace(id))
            id = "profile";

        var name = !string.IsNullOrWhiteSpace(profile.Name)
            ? profile.Name.Trim()
            : !string.IsNullOrWhiteSpace(fallbackName)
                ? fallbackName.Trim()
                : id;

        var low = Math.Clamp(Math.Abs(profile.LowCutHz), 0, 10_000);
        var high = Math.Clamp(Math.Abs(profile.HighCutHz), 0, 10_000);
        if (high < low) (low, high) = (high, low);
        if (low == 0 && high == 0)
        {
            low = DefaultLowCutHz;
            high = DefaultHighCutHz;
        }

        var now = DateTime.UtcNow;
        var created = profile.CreatedUtc == default ? now : profile.CreatedUtc.ToUniversalTime();
        var updated = profile.UpdatedUtc == default ? created : profile.UpdatedUtc.ToUniversalTime();

        return profile with
        {
            Id = id,
            Name = name,
            MicGainDb = Math.Clamp(profile.MicGainDb, -40, 10),
            LevelerMaxGainDb = ClampFinite(profile.LevelerMaxGainDb, 0.0, 20.0, 8.0),
            TxLeveling = SanitizeTxLeveling(profile.TxLeveling),
            CfcConfig = SanitizeCfc(profile.CfcConfig),
            TxPhaseRotator = SanitizeTxPhaseRotator(profile.TxPhaseRotator),
            LowCutHz = low,
            HighCutHz = high,
            ProcessingMode = string.Equals(profile.ProcessingMode, "vst", StringComparison.OrdinalIgnoreCase)
                ? "vst"
                : "native",
            ChainOrder = CleanStringList(profile.ChainOrder),
            ChainParked = CleanStringList(profile.ChainParked),
            VstPluginStates = CleanStringDictionary(profile.VstPluginStates),
            NativePluginStates = CleanNestedStringDictionary(profile.NativePluginStates),
            TargetSpectralDensity = Math.Clamp(profile.TargetSpectralDensity, 0, 100) == 0 && profile.TargetSpectralDensity != 0
                ? DefaultTargetSpectralDensity
                : Math.Clamp(profile.TargetSpectralDensity, 0, 100),
            CreatedUtc = created,
            UpdatedUtc = updated,
        };
    }

    public IReadOnlyList<TxAudioProfileDto> GetAll()
    {
        lock (_sync)
        {
            return _profiles.FindAll()
                .OrderBy(x => x.ProfileId, StringComparer.OrdinalIgnoreCase)
                .Select(TryDeserialize)
                .Where(x => x is not null)
                .Select(x => x!)
                .ToArray();
        }
    }

    public TxAudioProfileDto? Get(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        var id = NormalizeId(profileId);
        lock (_sync)
        {
            var entry = _profiles.FindOne(x => x.ProfileId == id);
            return entry is null ? null : TryDeserialize(entry);
        }
    }

    public bool Any()
    {
        lock (_sync) return _profiles.Count() > 0;
    }

    /// <summary>Upsert a profile by Id. Preserves CreatedUtc on overwrite,
    /// bumps UpdatedUtc.</summary>
    public TxAudioProfileDto Upsert(TxAudioProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var clean = Sanitize(profile);
        var id = clean.Id;
        var nowUtc = DateTime.UtcNow;

        lock (_sync)
        {
            var existing = _profiles.FindOne(x => x.ProfileId == id);
            // Preserve CreatedUtc from the existing JSON blob (full precision)
            // rather than the LiteDB column (truncated to ms / local kind).
            var created = existing is null
                ? nowUtc
                : (TryDeserialize(existing)?.CreatedUtc ?? existing.CreatedUtc);
            var normalized = clean with { Id = id, CreatedUtc = created, UpdatedUtc = nowUtc };
            var json = JsonSerializer.Serialize(normalized, JsonOptions);

            if (existing is null)
            {
                _profiles.Insert(new TxAudioProfileEntry
                {
                    ProfileId = id,
                    ProfileJson = json,
                    CreatedUtc = created,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.ProfileJson = json;
                existing.UpdatedUtc = nowUtc;
                _profiles.Update(existing);
            }
            MirrorToFolder(normalized);
            return normalized;
        }
    }

    public bool Delete(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return false;
        var id = NormalizeId(profileId);
        lock (_sync)
        {
            var removed = _profiles.DeleteMany(x => x.ProfileId == id) > 0;
            // If the deleted profile was the last-loaded pointer, clear it so a
            // restart doesn't try to apply a vanished profile.
            if (removed)
            {
                var ptr = _lastLoaded.FindAll().FirstOrDefault();
                if (ptr is not null && string.Equals(ptr.ProfileId, id, StringComparison.Ordinal))
                {
                    ptr.ProfileId = null;
                    _lastLoaded.Update(ptr);
                }
                RemoveFromFolder(id);
            }
            return removed;
        }
    }

    /// <summary>The persisted "last loaded" profile id, or null when none.</summary>
    public string? GetLastLoadedId()
    {
        lock (_sync)
        {
            var ptr = _lastLoaded.FindAll().FirstOrDefault();
            return string.IsNullOrWhiteSpace(ptr?.ProfileId) ? null : ptr!.ProfileId;
        }
    }

    /// <summary>Persist the "last loaded" pointer (null clears it).</summary>
    public void SetLastLoadedId(string? profileId)
    {
        var id = string.IsNullOrWhiteSpace(profileId) ? null : NormalizeId(profileId);
        lock (_sync)
        {
            var ptr = _lastLoaded.FindAll().FirstOrDefault() ?? new TxAudioProfileLastLoadedEntry();
            ptr.ProfileId = id;
            ptr.UpdatedUtc = DateTime.UtcNow;
            if (ptr.Id == 0) _lastLoaded.Insert(ptr);
            else _lastLoaded.Update(ptr);
        }
    }

    /// <summary>Parse a profile from a JSON document (e.g. an imported file).
    /// Returns null on blank/invalid input rather than throwing.</summary>
    public static TxAudioProfileDto? ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TxAudioProfileDto>(json, JsonOptions); }
        catch { return null; }
    }

    // Write-through mirror of one profile to <folder>/<id>.json. Best-effort:
    // the DB row is authoritative, so a folder IO failure is logged, not thrown.
    private void MirrorToFolder(TxAudioProfileDto profile)
    {
        try
        {
            Directory.CreateDirectory(_profileDir);
            var path = Path.Combine(_profileDir, profile.Id + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(profile, FileJsonOptions));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Mirroring TX audio profile '{Id}' to folder failed", profile.Id);
        }
    }

    private void RemoveFromFolder(string profileId)
    {
        try
        {
            var path = Path.Combine(_profileDir, profileId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Removing TX audio profile file '{Id}' failed", profileId);
        }
    }

    // Ensure every profile in the DB has a file in the folder (idempotent).
    private void SyncFolder()
    {
        Directory.CreateDirectory(_profileDir);
        foreach (var profile in GetAll())
            MirrorToFolder(profile);
    }

    private TxAudioProfileDto? TryDeserialize(TxAudioProfileEntry entry)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<TxAudioProfileDto>(entry.ProfileJson, JsonOptions);
            return parsed is null ? null : Sanitize(parsed, entry.ProfileId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ignoring invalid TX audio profile {ProfileId}", entry.ProfileId);
            return null;
        }
    }

    private static TxLevelingConfig SanitizeTxLeveling(TxLevelingConfig? cfg)
    {
        if (cfg is null) return new TxLevelingConfig();
        return cfg with
        {
            AlcMaxGainDb = ClampFinite(cfg.AlcMaxGainDb, 0.0, 120.0, 3.0),
            AlcDecayMs = Math.Clamp(cfg.AlcDecayMs, 1, 50),
            LevelerDecayMs = Math.Clamp(cfg.LevelerDecayMs, 1, 5000),
            CompressorGainDb = ClampFinite(cfg.CompressorGainDb, 0.0, 20.0, 0.0),
        };
    }

    private static TxPhaseRotatorConfig SanitizeTxPhaseRotator(TxPhaseRotatorConfig? cfg)
    {
        if (cfg is null) return new TxPhaseRotatorConfig();
        return cfg with
        {
            CornerHz = Math.Clamp(
                cfg.CornerHz,
                TxPhaseRotatorConfig.MinCornerHz,
                TxPhaseRotatorConfig.MaxCornerHz),
            Stages = Math.Clamp(
                cfg.Stages,
                TxPhaseRotatorConfig.MinStages,
                TxPhaseRotatorConfig.MaxStages),
        };
    }

    private static CfcConfig SanitizeCfc(CfcConfig? cfg)
    {
        if (cfg?.Bands is not { Length: 10 })
            return CfcConfig.Default;

        var defaults = CfcConfig.Default.Bands;
        var bands = new CfcBand[10];
        for (var i = 0; i < bands.Length; i++)
        {
            var band = cfg.Bands[i] ?? defaults[i];
            var fallback = defaults[i];
            bands[i] = new CfcBand(
                FreqHz: ClampFinite(band.FreqHz, 0.0, 20_000.0, fallback.FreqHz),
                CompLevelDb: ClampFinite(band.CompLevelDb, -60.0, 60.0, fallback.CompLevelDb),
                PostGainDb: ClampFinite(band.PostGainDb, -60.0, 60.0, fallback.PostGainDb));
        }

        return cfg with
        {
            PreCompDb = ClampFinite(cfg.PreCompDb, -60.0, 60.0, 0.0),
            PrePeqDb = ClampFinite(cfg.PrePeqDb, -60.0, 60.0, 0.0),
            Bands = bands,
        };
    }

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static List<string> CleanStringList(IEnumerable<string>? source)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in source ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var value = raw.Trim();
            if (seen.Add(value)) result.Add(value);
        }
        return result;
    }

    private static Dictionary<string, string> CleanStringDictionary(IDictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source is null) return result;
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null) continue;
            result[key.Trim()] = value;
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> CleanNestedStringDictionary(
        IDictionary<string, Dictionary<string, string>>? source)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (source is null) return result;
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var cleaned = CleanStringDictionary(value);
            if (cleaned.Count > 0) result[key.Trim()] = cleaned;
        }
        return result;
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class TxAudioProfileEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = "";
    public string ProfileJson { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class TxAudioProfileLastLoadedEntry
{
    public int Id { get; set; }
    public string? ProfileId { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
