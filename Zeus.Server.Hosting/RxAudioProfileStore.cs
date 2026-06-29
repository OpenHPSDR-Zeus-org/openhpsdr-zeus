// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists named RX Audio Suite profiles separately from TX profiles.
/// </summary>
public sealed class RxAudioProfileStore : IDisposable
{
    private readonly Zeus.Data.SharedLiteDatabase.Lease _dbLease;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RxAudioProfileEntry> _profiles;
    private readonly ILiteCollection<RxAudioProfileSelectionEntry> _selection;
    private readonly ILogger<RxAudioProfileStore> _log;
    private readonly object _sync = new();

    public RxAudioProfileStore(ILogger<RxAudioProfileStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _dbLease = Zeus.Data.SharedLiteDatabase.Acquire(dbPath);
        _db = _dbLease.Database;
        _profiles = _db.GetCollection<RxAudioProfileEntry>("rx_audio_profiles");
        _profiles.EnsureIndex(x => x.Name, unique: true);
        _selection = _db.GetCollection<RxAudioProfileSelectionEntry>("rx_audio_profile_selection");

        _log.LogInformation("RxAudioProfileStore initialized at {Path}", dbPath);
    }

    public IReadOnlyList<RxAudioProfileEntry> List()
    {
        lock (_sync)
            return _profiles.FindAll()
                .OrderByDescending(p => p.UpdatedUtc)
                .ToList();
    }

    public RxAudioProfileEntry? Get(string name)
    {
        lock (_sync) return _profiles.FindOne(p => p.Name == name);
    }

    public string? GetSelectedProfile()
    {
        lock (_sync) return GetSelectedProfileUnderLock();
    }

    public void SetSelectedProfile(string? name)
    {
        lock (_sync)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                ClearSelectedProfileUnderLock();
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var existing = _selection.FindAll().FirstOrDefault();
            if (existing is null)
            {
                _selection.Insert(new RxAudioProfileSelectionEntry
                {
                    Name = trimmed,
                    UpdatedUtc = nowUtc,
                });
                return;
            }

            existing.Name = trimmed;
            existing.UpdatedUtc = nowUtc;
            _selection.Update(existing);
        }
    }

    public RxAudioProfileEntry Save(
        string name,
        IReadOnlyList<string> order,
        IReadOnlyList<string> parked,
        bool masterBypass,
        IReadOnlyDictionary<string, string>? pluginStates = null)
    {
        lock (_sync)
        {
            var nowUtc = DateTime.UtcNow;
            var states = pluginStates is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(pluginStates);
            var existing = _profiles.FindOne(p => p.Name == name);
            if (existing is null)
            {
                var entry = new RxAudioProfileEntry
                {
                    Name = name,
                    Order = order.ToList(),
                    Parked = parked.ToList(),
                    MasterBypass = masterBypass,
                    PluginStates = states,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc,
                };
                _profiles.Insert(entry);
                return entry;
            }

            existing.Order = order.ToList();
            existing.Parked = parked.ToList();
            existing.MasterBypass = masterBypass;
            existing.PluginStates = states;
            existing.UpdatedUtc = nowUtc;
            _profiles.Update(existing);
            return existing;
        }
    }

    public bool Delete(string name)
    {
        lock (_sync)
        {
            var deleted = _profiles.DeleteMany(p => p.Name == name) > 0;
            if (deleted && string.Equals(GetSelectedProfileUnderLock(), name, StringComparison.Ordinal))
                ClearSelectedProfileUnderLock();
            return deleted;
        }
    }

    public void Dispose() => _dbLease.Dispose();

    private string? GetSelectedProfileUnderLock() =>
        _selection.FindAll().FirstOrDefault()?.Name;

    private void ClearSelectedProfileUnderLock()
    {
        _selection.DeleteMany(_ => true);
    }
}

public sealed class RxAudioProfileEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Order { get; set; } = new();
    public List<string> Parked { get; set; } = new();
    public bool MasterBypass { get; set; }
    public Dictionary<string, string> PluginStates { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public sealed class RxAudioProfileSelectionEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
}
