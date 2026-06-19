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

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<TxAudioProfileEntry> _profiles;
    private readonly ILiteCollection<TxAudioProfileLastLoadedEntry> _lastLoaded;
    private readonly ILogger<TxAudioProfileStore> _log;
    private readonly object _sync = new();

    public TxAudioProfileStore(ILogger<TxAudioProfileStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _profiles = _db.GetCollection<TxAudioProfileEntry>("tx_audio_profiles");
        // Race-safe unique index seed (FilterPresetStore pattern): parallel
        // WebApplicationFactory hosts on CI can both reach EnsureIndex; swallow
        // the duplicate-key throw so the second host doesn't fault.
        try { _profiles.EnsureIndex(x => x.ProfileId, unique: true); }
        catch (LiteException ex) when (ex.Message.Contains("INDEX_DUPLICATE_KEY", StringComparison.OrdinalIgnoreCase)) { }
        _lastLoaded = _db.GetCollection<TxAudioProfileLastLoadedEntry>("tx_audio_profile_last_loaded");

        _log.LogInformation("TxAudioProfileStore initialized at {Path}", dbPath);
    }

    public static string NormalizeId(string id) => (id ?? "").Trim().ToLowerInvariant();

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
        var id = NormalizeId(profile.Id);
        var nowUtc = DateTime.UtcNow;

        lock (_sync)
        {
            var existing = _profiles.FindOne(x => x.ProfileId == id);
            // Preserve CreatedUtc from the existing JSON blob (full precision)
            // rather than the LiteDB column (truncated to ms / local kind).
            var created = existing is null
                ? nowUtc
                : (TryDeserialize(existing)?.CreatedUtc ?? existing.CreatedUtc);
            var normalized = profile with { Id = id, CreatedUtc = created, UpdatedUtc = nowUtc };
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

    private TxAudioProfileDto? TryDeserialize(TxAudioProfileEntry entry)
    {
        try
        {
            return JsonSerializer.Deserialize<TxAudioProfileDto>(entry.ProfileJson, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ignoring invalid TX audio profile {ProfileId}", entry.ProfileId);
            return null;
        }
    }

    public void Dispose() => _db.Dispose();
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
