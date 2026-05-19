using LiteDB;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using JsonSerializer = System.Text.Json.JsonSerializer;
using JsonSerializerOptions = System.Text.Json.JsonSerializerOptions;
using JsonNamingPolicy = System.Text.Json.JsonNamingPolicy;
using JsonException = System.Text.Json.JsonException;

namespace Zeus.Plugins.Host;

/// <summary>
/// LiteDB-backed key/value store. One collection per plugin id; plugins
/// cannot read one another's settings. Backed by the same
/// <c>zeus-prefs.db</c> as the rest of Zeus when running under the
/// server; tests typically point ZEUS_PREFS_PATH at a temp file.
/// </summary>
public sealed class PluginSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly string _dbPath;
    private readonly ILogger<PluginSettingsStore>? _log;
    private readonly object _lock = new();

    public PluginSettingsStore(string dbPath, ILogger<PluginSettingsStore>? log = null)
    {
        _dbPath = dbPath;
        _log = log;

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _log?.LogInformation("PluginSettingsStore initialised at {Path}", dbPath);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Get an IPluginSettings view scoped to one plugin id.</summary>
    public IPluginSettings ForPlugin(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            throw new ArgumentException("pluginId required", nameof(pluginId));
        return new ScopedSettings(this, pluginId);
    }

    private ILiteCollection<SettingDoc> CollectionFor(string pluginId)
        => _db.GetCollection<SettingDoc>(CollectionName(pluginId));

    internal static string CollectionName(string pluginId)
        => "plugin_" + pluginId.Replace('.', '_').Replace('-', '_');

    public void Dispose() => _db.Dispose();

    private sealed class ScopedSettings : IPluginSettings
    {
        private readonly PluginSettingsStore _store;
        private readonly string _pluginId;

        public ScopedSettings(PluginSettingsStore store, string pluginId)
        {
            _store = store;
            _pluginId = pluginId;
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            lock (_store._lock)
            {
                // OrderByDescending on Id: defensive against duplicate rows
                // for the same Key that may exist from before the SetAsync
                // dedupe fix below. SetAsync clears them out on next write.
                var doc = _store.CollectionFor(_pluginId)
                    .Query()
                    .Where(x => x.Key == key)
                    .OrderByDescending(x => x.Id)
                    .FirstOrDefault();
                if (doc is null) return Task.FromResult<T?>(default);
                try
                {
                    var value = JsonSerializer.Deserialize<T>(doc.JsonValue, JsonOpts);
                    return Task.FromResult<T?>(value);
                }
                catch (JsonException)
                {
                    return Task.FromResult<T?>(default);
                }
            }
        }

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(value, JsonOpts);
            lock (_store._lock)
            {
                // Upsert by Key (not by LiteDB _id). The old code used
                // `coll.Upsert(new SettingDoc { ... })` which left Id at 0
                // and so LiteDB treated every Set as a new row, growing
                // the collection without bound and stranding the latest
                // value behind a wall of stale duplicates. DeleteMany +
                // Insert is atomic under _store._lock and idempotent on
                // the existing duplicate corpus.
                var coll = _store.CollectionFor(_pluginId);
                coll.DeleteMany(x => x.Key == key);
                coll.Insert(new SettingDoc { Key = key, JsonValue = json });
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            lock (_store._lock)
            {
                _store.CollectionFor(_pluginId).DeleteMany(x => x.Key == key);
            }
            return Task.CompletedTask;
        }
    }

    internal sealed class SettingDoc
    {
        public int Id { get; set; }
        public string Key { get; set; } = "";
        public string JsonValue { get; set; } = "";
    }
}
