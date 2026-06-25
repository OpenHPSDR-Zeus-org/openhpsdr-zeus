// SPDX-License-Identifier: GPL-2.0-or-later
//
// Persists operator-edited TX station profile overrides. The built-in profile
// catalog lives in the frontend so default curves can evolve without a server
// migration; this store only records explicit edits.

using System.Text.Json;
using LiteDB;
using Zeus.Contracts;

// LiteDB also exposes a JsonSerializer type; this store serializes with
// System.Text.Json, so resolve the bare name to it.
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Zeus.Server;

public sealed class TxStationProfileStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<TxStationProfileEntry> _profiles;
    private readonly ILogger<TxStationProfileStore> _log;
    private readonly object _sync = new();

    public TxStationProfileStore(ILogger<TxStationProfileStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _profiles = _db.GetCollection<TxStationProfileEntry>("tx_station_profiles");
        _profiles.EnsureIndex(x => x.ProfileId, unique: true);

        _log.LogInformation("TxStationProfileStore initialized at {Path}", dbPath);
    }

    public IReadOnlyList<TxStationProfileDto> GetAll()
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

    public TxStationProfileDto Upsert(TxStationProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = profile with { Id = profile.Id.Trim().ToLowerInvariant() };
        var nowUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(normalized, JsonOptions);

        lock (_sync)
        {
            var existing = _profiles.FindOne(x => x.ProfileId == normalized.Id);
            if (existing is null)
            {
                _profiles.Insert(new TxStationProfileEntry
                {
                    ProfileId = normalized.Id,
                    ProfileJson = json,
                    UpdatedUtc = nowUtc,
                });
            }
            else
            {
                existing.ProfileJson = json;
                existing.UpdatedUtc = nowUtc;
                _profiles.Update(existing);
            }
        }

        return normalized;
    }

    public bool Delete(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return false;
        var id = profileId.Trim().ToLowerInvariant();
        lock (_sync)
        {
            return _profiles.DeleteMany(x => x.ProfileId == id) > 0;
        }
    }

    private TxStationProfileDto? TryDeserialize(TxStationProfileEntry entry)
    {
        try
        {
            return JsonSerializer.Deserialize<TxStationProfileDto>(entry.ProfileJson, JsonOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ignoring invalid TX station profile override {ProfileId}", entry.ProfileId);
            return null;
        }
    }

    public void Dispose() => _dbLease.Dispose();
}

public sealed class TxStationProfileEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = "";
    public string ProfileJson { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}
