// SPDX-License-Identifier: GPL-2.0-or-later
//
// Persists the frontend Signal Intelligence weak-signal display policy. The
// actual CFAR floor, temporal confidence, Signal Pop, and snap logic stay in
// zeus-web; this store makes the operator's tuning durable across clients.

using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class DisplayIntelligenceSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DisplayIntelligenceSettingsEntry> _docs;
    private readonly ILogger<DisplayIntelligenceSettingsStore> _log;
    private readonly object _sync = new();

    public DisplayIntelligenceSettingsStore(
        ILogger<DisplayIntelligenceSettingsStore> log,
        string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _docs = _db.GetCollection<DisplayIntelligenceSettingsEntry>("display_intelligence_settings");

        _log.LogInformation("DisplayIntelligenceSettingsStore initialized at {Path}", dbPath);
    }

    public DisplayIntelligenceSettingsDto Get()
    {
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault();
            return e is null
                ? Defaults()
                : Normalize(new DisplayIntelligenceSettingsDto(
                    e.ProfileId,
                    e.PopEnabled,
                    e.SnapEnabled,
                    e.AutoNotchEnabled,
                    e.AutoProfileEnabled,
                    e.VisualAgcEnabled,
                    e.ImpulseRejectEnabled,
                    e.PopFloorDb,
                    e.PopSpanDb,
                    e.PopGamma,
                    e.PopRenderIntensity,
                    e.CoherenceHoldGate,
                    e.CoherenceBoostDb,
                    e.RidgeBoost,
                    e.RidgeMaxBoostDb,
                    e.VisualAgcStrength,
                    e.ImpulseRejectDb,
                    e.SnapRadiusHz,
                    e.SnapMinSnrDb,
                    e.PeakMinSnrDb));
        }
    }

    public DisplayIntelligenceSettingsDto Save(DisplayIntelligenceSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = Normalize(settings);
        lock (_sync)
        {
            var e = _docs.FindAll().FirstOrDefault() ?? new DisplayIntelligenceSettingsEntry();
            e.ProfileId = normalized.ProfileId;
            e.PopEnabled = normalized.PopEnabled;
            e.SnapEnabled = normalized.SnapEnabled;
            e.AutoNotchEnabled = normalized.AutoNotchEnabled;
            e.AutoProfileEnabled = normalized.AutoProfileEnabled;
            e.VisualAgcEnabled = normalized.VisualAgcEnabled;
            e.ImpulseRejectEnabled = normalized.ImpulseRejectEnabled;
            e.PopFloorDb = normalized.PopFloorDb;
            e.PopSpanDb = normalized.PopSpanDb;
            e.PopGamma = normalized.PopGamma;
            e.PopRenderIntensity = normalized.PopRenderIntensity;
            e.CoherenceHoldGate = normalized.CoherenceHoldGate;
            e.CoherenceBoostDb = normalized.CoherenceBoostDb;
            e.RidgeBoost = normalized.RidgeBoost;
            e.RidgeMaxBoostDb = normalized.RidgeMaxBoostDb;
            e.VisualAgcStrength = normalized.VisualAgcStrength;
            e.ImpulseRejectDb = normalized.ImpulseRejectDb;
            e.SnapRadiusHz = normalized.SnapRadiusHz;
            e.SnapMinSnrDb = normalized.SnapMinSnrDb;
            e.PeakMinSnrDb = normalized.PeakMinSnrDb;
            e.UpdatedUtc = DateTime.UtcNow;
            if (e.Id == 0) _docs.Insert(e);
            else _docs.Update(e);
        }
        return normalized;
    }

    private static DisplayIntelligenceSettingsDto Defaults() => new(
        ProfileId: "balanced",
        PopEnabled: false,
        SnapEnabled: false,
        AutoNotchEnabled: false,
        AutoProfileEnabled: false,
        VisualAgcEnabled: true,
        ImpulseRejectEnabled: true,
        PopFloorDb: 3.0,
        PopSpanDb: 30.0,
        PopGamma: 0.5,
        PopRenderIntensity: 72,
        CoherenceHoldGate: 0.45,
        CoherenceBoostDb: 4.0,
        RidgeBoost: 0.35,
        RidgeMaxBoostDb: 8.0,
        VisualAgcStrength: 45,
        ImpulseRejectDb: 18,
        SnapRadiusHz: 4000,
        SnapMinSnrDb: 6.0,
        PeakMinSnrDb: 8.0);

    public static DisplayIntelligenceSettingsDto Normalize(DisplayIntelligenceSettingsDto s)
    {
        var d = Defaults();
        var profileId = NormalizeProfileId(s.ProfileId, d.ProfileId);
        return s with
        {
            ProfileId = profileId,
            PopFloorDb = ClampFinite(s.PopFloorDb, 0.0, 12.0, d.PopFloorDb),
            PopSpanDb = ClampFinite(s.PopSpanDb, 12.0, 60.0, d.PopSpanDb),
            PopGamma = ClampFinite(s.PopGamma, 0.3, 1.2, d.PopGamma),
            PopRenderIntensity = Math.Clamp(s.PopRenderIntensity, 0, 100),
            CoherenceHoldGate = ClampFinite(s.CoherenceHoldGate, 0.2, 0.8, d.CoherenceHoldGate),
            CoherenceBoostDb = ClampFinite(s.CoherenceBoostDb, 0.0, 8.0, d.CoherenceBoostDb),
            RidgeBoost = ClampFinite(s.RidgeBoost, 0.0, 0.8, d.RidgeBoost),
            RidgeMaxBoostDb = ClampFinite(s.RidgeMaxBoostDb, 0.0, 12.0, d.RidgeMaxBoostDb),
            VisualAgcStrength = Math.Clamp(s.VisualAgcStrength, 0, 100),
            ImpulseRejectDb = Math.Clamp(s.ImpulseRejectDb, 8, 32),
            SnapRadiusHz = Math.Clamp(s.SnapRadiusHz, 500, 12_000),
            SnapMinSnrDb = ClampFinite(s.SnapMinSnrDb, 3.0, 16.0, d.SnapMinSnrDb),
            PeakMinSnrDb = ClampFinite(s.PeakMinSnrDb, 4.0, 20.0, d.PeakMinSnrDb),
        };
    }

    private static string NormalizeProfileId(string? profileId, string fallback)
    {
        var id = string.IsNullOrWhiteSpace(profileId)
            ? fallback
            : profileId.Trim().ToLowerInvariant();
        return IsProfileId(id) ? id : fallback;
    }

    private static bool IsProfileId(string id) =>
        id is "balanced" or "dx" or "cw" or "digital" or "voice" or "contest" or "custom";

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : Math.Clamp(value, min, max);

    public void Dispose() => _db.Dispose();
}

public sealed class DisplayIntelligenceSettingsEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = "balanced";
    public bool PopEnabled { get; set; }
    public bool SnapEnabled { get; set; }
    public bool AutoNotchEnabled { get; set; }
    public bool AutoProfileEnabled { get; set; }
    public bool VisualAgcEnabled { get; set; } = true;
    public bool ImpulseRejectEnabled { get; set; } = true;
    public double PopFloorDb { get; set; } = 3.0;
    public double PopSpanDb { get; set; } = 30.0;
    public double PopGamma { get; set; } = 0.5;
    public int PopRenderIntensity { get; set; } = 72;
    public double CoherenceHoldGate { get; set; } = 0.45;
    public double CoherenceBoostDb { get; set; } = 4.0;
    public double RidgeBoost { get; set; } = 0.35;
    public double RidgeMaxBoostDb { get; set; } = 8.0;
    public int VisualAgcStrength { get; set; } = 45;
    public int ImpulseRejectDb { get; set; } = 18;
    public int SnapRadiusHz { get; set; } = 4000;
    public double SnapMinSnrDb { get; set; } = 6.0;
    public double PeakMinSnrDb { get; set; } = 8.0;
    public DateTime UpdatedUtc { get; set; }
}
