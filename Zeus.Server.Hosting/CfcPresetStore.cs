// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using LiteDB;
using Zeus.Contracts;

using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Zeus.Server;

/// <summary>
/// Persists named TX Audio Tools CFC presets. The live CFC state still lives in
/// <see cref="DspSettingsStore"/>; this store is only the operator's recallable
/// preset library.
/// </summary>
public sealed class CfcPresetStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<CfcPresetEntry> _presets;
    private readonly ILogger<CfcPresetStore> _log;
    private readonly object _sync = new();

    public CfcPresetStore(ILogger<CfcPresetStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _presets = _db.GetCollection<CfcPresetEntry>("cfc_presets");
        _presets.EnsureIndex(x => x.NormalizedName, unique: true);

        _log.LogInformation("CfcPresetStore initialized at {Path}", dbPath);
    }

    public IReadOnlyList<CfcPreset> List()
    {
        lock (_sync)
            return _presets.FindAll()
                .Select(TryToPreset)
                .Where(p => p is not null)
                .Select(p => p!)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public CfcPreset? Get(string name)
    {
        var normalized = NormalizeName(name);
        lock (_sync)
        {
            var entry = _presets.FindOne(p => p.NormalizedName == normalized);
            return entry is null ? null : TryToPreset(entry);
        }
    }

    public CfcPreset Save(string name, CfcConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Bands is null || config.Bands.Length != 10)
            throw new ArgumentException($"Bands must have exactly 10 entries; got {config.Bands?.Length ?? 0}", nameof(config));

        var displayName = CleanName(name);
        var normalized = NormalizeName(displayName);
        var nowUtc = DateTime.UtcNow;
        var configJson = JsonSerializer.Serialize(config, JsonOptions);

        lock (_sync)
        {
            var existing = _presets.FindOne(p => p.NormalizedName == normalized);
            if (existing is null)
            {
                existing = new CfcPresetEntry
                {
                    Name = displayName,
                    NormalizedName = normalized,
                    ConfigJson = configJson,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc,
                };
                _presets.Insert(existing);
            }
            else
            {
                existing.Name = displayName;
                existing.ConfigJson = configJson;
                existing.UpdatedUtc = nowUtc;
                _presets.Update(existing);
            }

            return new CfcPreset(
                existing.Name,
                CloneConfig(config),
                ToUtc(existing.CreatedUtc),
                ToUtc(existing.UpdatedUtc));
        }
    }

    public void Dispose() => _db.Dispose();

    internal static string CleanName(string name)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
            throw new ArgumentException("Preset name required", nameof(name));
        if (clean.Length > 80)
            throw new ArgumentException("Preset name must be 80 characters or fewer", nameof(name));
        if (clean.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#'))
            throw new ArgumentException("Preset name contains an invalid character", nameof(name));
        return clean;
    }

    private static string NormalizeName(string name) =>
        CleanName(name).ToUpperInvariant();

    private CfcPreset? TryToPreset(CfcPresetEntry entry)
    {
        try
        {
            var config = JsonSerializer.Deserialize<CfcConfig>(entry.ConfigJson, JsonOptions);
            if (config?.Bands is null || config.Bands.Length != 10)
            {
                _log.LogWarning("Skipping malformed CFC preset {Name}: invalid band count", entry.Name);
                return null;
            }
            return new CfcPreset(
                entry.Name,
                CloneConfig(config),
                ToUtc(entry.CreatedUtc),
                ToUtc(entry.UpdatedUtc));
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Skipping malformed CFC preset {Name}", entry.Name);
            return null;
        }
    }

    private static CfcConfig CloneConfig(CfcConfig config) =>
        config with { Bands = config.Bands.Select(b => b with { }).ToArray() };

    private static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}

public sealed record CfcPreset(
    string Name,
    CfcConfig Config,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public sealed class CfcPresetEntry
{
    public int Id { get; set; }
    public string NormalizedName { get; set; } = "";
    public string Name { get; set; } = "";
    public string ConfigJson { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
