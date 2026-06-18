// SPDX-License-Identifier: GPL-2.0-or-later
using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Persists named RX Audio Suite profiles separately from TX profiles.
/// </summary>
public sealed class RxAudioProfileStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<RxAudioProfileEntry> _profiles;
    private readonly ILogger<RxAudioProfileStore> _log;
    private readonly object _sync = new();

    public RxAudioProfileStore(ILogger<RxAudioProfileStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _profiles = _db.GetCollection<RxAudioProfileEntry>("rx_audio_profiles");
        _profiles.EnsureIndex(x => x.Name, unique: true);

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
        lock (_sync) return _profiles.DeleteMany(p => p.Name == name) > 0;
    }

    public void Dispose() => _db.Dispose();
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
