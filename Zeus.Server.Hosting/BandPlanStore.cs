// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using System.Text.Json;
using System.Text.Json.Serialization;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Loads shipped band plan JSON files from the BandPlans/ folder alongside the
/// binary, and persists per-region operator overrides in LiteDB.
///
/// Segment override records replace the shipped JSON wholesale when present
/// (whole-region granularity keeps diffing simple; the biggest plan is ~30 rows).
/// "Reset to defaults" deletes the record.
/// </summary>
public sealed class BandPlanStore : IDisposable
{
    private readonly ILogger<BandPlanStore> _log;
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<BandPlanOverrideRecord> _overrides;

    private List<BandRegion> _regions = [];
    // Shipped segments per regionId (not merged; resolver layering happens in BandPlanService)
    private readonly Dictionary<string, List<BandSegment>> _shipped = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public BandPlanStore(ILogger<BandPlanStore> log)
    {
        _log = log;

        var dbPath = PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _overrides = _db.GetCollection<BandPlanOverrideRecord>("band_plan_overrides");
        _overrides.EnsureIndex(x => x.RegionId, unique: true);

        LoadShippedData();
    }

    public IReadOnlyList<BandRegion> Regions => _regions;

    /// <summary>
    /// Returns segments for <paramref name="regionId"/> — override if present,
    /// otherwise shipped defaults. Does NOT resolve parent chain; BandPlanService
    /// handles that.
    /// </summary>
    public IReadOnlyList<BandSegment> GetSegmentsForRegion(string regionId)
    {
        var rec = _overrides.FindOne(x => x.RegionId == regionId);
        if (rec is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<List<BandSegment>>(rec.SegmentsJson, _jsonOpts) ?? [];
            }
            catch
            {
                _log.LogWarning("band.override.deserialize.failed regionId={R}", regionId);
            }
        }

        return _shipped.TryGetValue(regionId, out var shipped) ? shipped : [];
    }

    public void SaveOverride(string regionId, IReadOnlyList<BandSegment> segments)
    {
        var json = JsonSerializer.Serialize(segments, _jsonOpts);
        var existing = _overrides.FindOne(x => x.RegionId == regionId);
        if (existing is null)
        {
            _overrides.Insert(new BandPlanOverrideRecord
            {
                RegionId = regionId,
                SegmentsJson = json,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.SegmentsJson = json;
            existing.UpdatedUtc = DateTime.UtcNow;
            _overrides.Update(existing);
        }
        _log.LogInformation("band.override.saved regionId={R} segments={N}", regionId, segments.Count);
    }

    public void DeleteOverride(string regionId)
    {
        _overrides.DeleteMany(x => x.RegionId == regionId);
        _log.LogInformation("band.override.deleted regionId={R}", regionId);
    }

    public bool HasOverride(string regionId) =>
        _overrides.Exists(x => x.RegionId == regionId);

    public void Dispose() => _db.Dispose();

    private void LoadShippedData()
    {
        var baseDir = AppContext.BaseDirectory;
        var plansDir = Path.Combine(baseDir, "BandPlans");

        if (!Directory.Exists(plansDir))
        {
            _log.LogWarning("band.plans.dir.missing path={Path}", plansDir);
            return;
        }

        // Load region catalog
        var regionsFile = Path.Combine(plansDir, "regions.json");
        if (File.Exists(regionsFile))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(regionsFile));
                var regionArray = doc.RootElement.GetProperty("regions");
                _regions = JsonSerializer.Deserialize<List<BandRegion>>(regionArray.GetRawText(), _jsonOpts) ?? [];
                _log.LogInformation("band.regions.loaded count={N}", _regions.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "band.regions.load.failed file={File}", regionsFile);
            }
        }

        // Load per-region segment files
        foreach (var file in Directory.EnumerateFiles(plansDir, "*.segments.json"))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(file));
                var regionId = doc.RootElement.GetProperty("regionId").GetString() ?? "";
                var segsElem = doc.RootElement.GetProperty("segments");
                var segs = JsonSerializer.Deserialize<List<SegmentJson>>(segsElem.GetRawText(), _jsonOpts) ?? [];

                _shipped[regionId] = segs.Select(s => new BandSegment(
                    RegionId: regionId,
                    LowHz: s.LowHz,
                    HighHz: s.HighHz,
                    Label: s.Label ?? "",
                    Allocation: s.Allocation,
                    ModeRestriction: s.ModeRestriction,
                    MaxPowerW: s.MaxPowerW,
                    Notes: s.Notes
                )).ToList();

                _log.LogDebug("band.segments.loaded regionId={R} count={N}", regionId, segs.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "band.segments.load.failed file={File}", file);
            }
        }
    }


    // JSON deserialisation shape — camelCase keys in file, nullable-friendly
    private sealed class SegmentJson
    {
        public long LowHz { get; set; }
        public long HighHz { get; set; }
        public string? Label { get; set; }
        public BandAllocation Allocation { get; set; }
        public ModeRestriction ModeRestriction { get; set; }
        public int? MaxPowerW { get; set; }
        public string? Notes { get; set; }
    }
}

public sealed class BandPlanOverrideRecord
{
    public int Id { get; set; }
    public string RegionId { get; set; } = string.Empty;
    // Stored as JSON string to avoid LiteDB record-type serialization issues
    public string SegmentsJson { get; set; } = "[]";
    public DateTime UpdatedUtc { get; set; }
}
