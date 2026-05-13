using LiteDB;
using Zeus.Contracts;

namespace Zeus.Server;

// DSP settings persistence — stores NR/NB/ANF/SNB/NBP parameters so the
// operator's preferred noise reduction and blanker configuration survives
// server restarts. Shares zeus-prefs.db with BandMemoryStore and LayoutStore
// (DSP settings aren't sensitive).
//
// NR2 post2 + NR4 (Sbnr) tunables are persisted as nullable scalars on the
// existing entry — null means "use the engine's NrDefaults baseline" so the
// operator can reset a field by clearing it. No new POCO type is introduced
// because LiteDB's BsonMapper races on parallel construction (commit b57c12d).
public sealed class DspSettingsStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DspSettingsEntry> _entries;
    private readonly ILogger<DspSettingsStore> _log;

    public DspSettingsStore(ILogger<DspSettingsStore> log, string? dbPathOverride = null)
    {
        _log = log;
        var dbPath = dbPathOverride ?? PrefsDbPath.Get();

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _entries = _db.GetCollection<DspSettingsEntry>("dsp_settings");
        _entries.EnsureIndex(x => x.ProfileId, unique: true);

        _log.LogInformation("DspSettingsStore initialized at {Path}", dbPath);
    }

    public NrConfig? Get(string profileId = "default")
    {
        var e = _entries.FindOne(x => x.ProfileId == profileId);
        if (e is null)
            return null;

        // Legacy scale migration: pre-fix Zeus stored EmnrPost2Factor/Nlevel
        // on the WDSP post-divide 0..1 scale (default 0.15). The wire-correct
        // form is the Thetis NUD 0..100 scale (default 15.0) — WDSP /100s
        // internally at emnr.c:1035/1042. Old persisted values < 1.0 are
        // promoted in place so the operator's saved relative position is kept.
        // The new UI gauge has step=1 so legitimate post-fix values are always
        // >= 1.0; this check is unambiguous.
        bool migrated = false;
        if (e.EmnrPost2Factor is double oldFactor && oldFactor < 1.0)
        {
            e.EmnrPost2Factor = oldFactor * 100.0;
            migrated = true;
        }
        if (e.EmnrPost2Nlevel is double oldNlevel && oldNlevel < 1.0)
        {
            e.EmnrPost2Nlevel = oldNlevel * 100.0;
            migrated = true;
        }
        if (migrated)
        {
            e.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(e);
            _log.LogInformation(
                "Migrated legacy NR2 post2 scale (×100) for profile {ProfileId}: factor={Factor} nlevel={Nlevel}",
                profileId, e.EmnrPost2Factor, e.EmnrPost2Nlevel);
        }

        return new NrConfig(
            NrMode: e.NrMode,
            AnfEnabled: e.AnfEnabled,
            SnbEnabled: e.SnbEnabled,
            NbpNotchesEnabled: e.NbpNotchesEnabled,
            NbMode: e.NbMode,
            NbThreshold: e.NbThreshold,
            EmnrPost2Run: e.EmnrPost2Run,
            EmnrPost2Factor: e.EmnrPost2Factor,
            EmnrPost2Nlevel: e.EmnrPost2Nlevel,
            EmnrPost2Rate: e.EmnrPost2Rate,
            EmnrPost2Taper: e.EmnrPost2Taper,
            Nr4ReductionAmount: e.Nr4ReductionAmount,
            Nr4SmoothingFactor: e.Nr4SmoothingFactor,
            Nr4WhiteningFactor: e.Nr4WhiteningFactor,
            Nr4NoiseRescale: e.Nr4NoiseRescale,
            Nr4PostFilterThreshold: e.Nr4PostFilterThreshold,
            Nr4NoiseScalingType: e.Nr4NoiseScalingType,
            Nr4Position: e.Nr4Position,
            EmnrGainMethod: e.EmnrGainMethod,
            EmnrNpeMethod: e.EmnrNpeMethod,
            EmnrAeRun: e.EmnrAeRun,
            EmnrTrainT1: e.EmnrTrainT1,
            EmnrTrainT2: e.EmnrTrainT2);
    }

    // CFC (Continuous Frequency Compressor) — issue #123. Persisted globally
    // (one row per profileId, sharing the same DspSettingsEntry). Returns null
    // when the entry is missing OR when CFC has never been written, so the
    // caller can fall back to <see cref="CfcConfig.Default"/>. Old DB rows
    // (pre-CFC) load with all CfcEnabled/CfcBandN* fields at their nullable
    // / zero defaults, which means CfcEnabled is null on a legacy upsert; we
    // return null in that case to preserve the default-OFF promise.
    // AGC top (max gain). Persisted as a nullable scalar on the existing
    // DspSettingsEntry — null means "first run, RadioService applies its
    // baseline default" so the operator's saved AGC-T survives restarts but
    // a fresh install picks up the maintainer-chosen baseline (currently
    // 45 dB — see RadioService.cs).
    public double? GetAgcTopDb(string profileId = "default")
    {
        var e = _entries.FindOne(x => x.ProfileId == profileId);
        return e?.AgcTopDb;
    }

    public void SetAgcTopDb(double db, string profileId = "default")
    {
        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
        {
            // Seed a fresh entry with NR defaults so the row is valid for
            // future NR/CFC writes — same pattern the CFC Upsert uses.
            var nrSeed = new NrConfig();
            _entries.Insert(new DspSettingsEntry
            {
                ProfileId = profileId,
                NrMode = nrSeed.NrMode,
                AnfEnabled = nrSeed.AnfEnabled,
                SnbEnabled = nrSeed.SnbEnabled,
                NbpNotchesEnabled = nrSeed.NbpNotchesEnabled,
                NbMode = nrSeed.NbMode,
                NbThreshold = nrSeed.NbThreshold,
                AgcTopDb = db,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.AgcTopDb = db;
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

    public CfcConfig? GetCfc(string profileId = "default")
    {
        var e = _entries.FindOne(x => x.ProfileId == profileId);
        if (e is null) return null;
        if (e.CfcEnabled is null) return null;  // never written → caller uses Default

        // Reconstruct the 10-band array from the per-band scalars. Order of
        // the rows on disk is stable (band index 0..9), so the operator's
        // typed order survives the round-trip exactly.
        var bands = new CfcBand[10]
        {
            new(e.CfcBand1Freq, e.CfcBand1Comp, e.CfcBand1Post),
            new(e.CfcBand2Freq, e.CfcBand2Comp, e.CfcBand2Post),
            new(e.CfcBand3Freq, e.CfcBand3Comp, e.CfcBand3Post),
            new(e.CfcBand4Freq, e.CfcBand4Comp, e.CfcBand4Post),
            new(e.CfcBand5Freq, e.CfcBand5Comp, e.CfcBand5Post),
            new(e.CfcBand6Freq, e.CfcBand6Comp, e.CfcBand6Post),
            new(e.CfcBand7Freq, e.CfcBand7Comp, e.CfcBand7Post),
            new(e.CfcBand8Freq, e.CfcBand8Comp, e.CfcBand8Post),
            new(e.CfcBand9Freq, e.CfcBand9Comp, e.CfcBand9Post),
            new(e.CfcBand10Freq, e.CfcBand10Comp, e.CfcBand10Post),
        };
        return new CfcConfig(
            Enabled: e.CfcEnabled.Value,
            PostEqEnabled: e.CfcPostEqEnabled ?? false,
            PreCompDb: e.CfcPreCompDb ?? 0.0,
            PrePeqDb: e.CfcPrePeqDb ?? 0.0,
            Bands: bands);
    }

    public void Upsert(NrConfig config, string profileId = "default")
    {
        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
        {
            _entries.Insert(new DspSettingsEntry
            {
                ProfileId = profileId,
                NrMode = config.NrMode,
                AnfEnabled = config.AnfEnabled,
                SnbEnabled = config.SnbEnabled,
                NbpNotchesEnabled = config.NbpNotchesEnabled,
                NbMode = config.NbMode,
                NbThreshold = config.NbThreshold,
                EmnrPost2Run = config.EmnrPost2Run,
                EmnrPost2Factor = config.EmnrPost2Factor,
                EmnrPost2Nlevel = config.EmnrPost2Nlevel,
                EmnrPost2Rate = config.EmnrPost2Rate,
                EmnrPost2Taper = config.EmnrPost2Taper,
                EmnrGainMethod = config.EmnrGainMethod,
                EmnrNpeMethod = config.EmnrNpeMethod,
                EmnrAeRun = config.EmnrAeRun,
                EmnrTrainT1 = config.EmnrTrainT1,
                EmnrTrainT2 = config.EmnrTrainT2,
                Nr4ReductionAmount = config.Nr4ReductionAmount,
                Nr4SmoothingFactor = config.Nr4SmoothingFactor,
                Nr4WhiteningFactor = config.Nr4WhiteningFactor,
                Nr4NoiseRescale = config.Nr4NoiseRescale,
                Nr4PostFilterThreshold = config.Nr4PostFilterThreshold,
                Nr4NoiseScalingType = config.Nr4NoiseScalingType,
                Nr4Position = config.Nr4Position,
                UpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.NrMode = config.NrMode;
            existing.AnfEnabled = config.AnfEnabled;
            existing.SnbEnabled = config.SnbEnabled;
            existing.NbpNotchesEnabled = config.NbpNotchesEnabled;
            existing.NbMode = config.NbMode;
            existing.NbThreshold = config.NbThreshold;
            existing.EmnrPost2Run = config.EmnrPost2Run;
            existing.EmnrPost2Factor = config.EmnrPost2Factor;
            existing.EmnrPost2Nlevel = config.EmnrPost2Nlevel;
            existing.EmnrPost2Rate = config.EmnrPost2Rate;
            existing.EmnrPost2Taper = config.EmnrPost2Taper;
            existing.EmnrGainMethod = config.EmnrGainMethod;
            existing.EmnrNpeMethod = config.EmnrNpeMethod;
            existing.EmnrAeRun = config.EmnrAeRun;
            existing.EmnrTrainT1 = config.EmnrTrainT1;
            existing.EmnrTrainT2 = config.EmnrTrainT2;
            existing.Nr4ReductionAmount = config.Nr4ReductionAmount;
            existing.Nr4SmoothingFactor = config.Nr4SmoothingFactor;
            existing.Nr4WhiteningFactor = config.Nr4WhiteningFactor;
            existing.Nr4NoiseRescale = config.Nr4NoiseRescale;
            existing.Nr4PostFilterThreshold = config.Nr4PostFilterThreshold;
            existing.Nr4NoiseScalingType = config.Nr4NoiseScalingType;
            existing.Nr4Position = config.Nr4Position;
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

    // CFC upsert — extends the same row used for NR. Insert path needs all the
    // NR fields too because the row may not exist yet (a fresh install where
    // the operator opens TX Audio Tools before touching NR). NR fields are
    // seeded from a default NrConfig in that case so the legacy NR path
    // continues to round-trip on subsequent NR-only Upserts.
    public void Upsert(CfcConfig config, string profileId = "default")
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Bands is null || config.Bands.Length != 10)
            throw new ArgumentException($"Bands must have exactly 10 entries; got {config.Bands?.Length ?? 0}", nameof(config));

        var existing = _entries.FindOne(x => x.ProfileId == profileId);
        if (existing is null)
        {
            var nrSeed = new NrConfig();
            existing = new DspSettingsEntry
            {
                ProfileId = profileId,
                NrMode = nrSeed.NrMode,
                AnfEnabled = nrSeed.AnfEnabled,
                SnbEnabled = nrSeed.SnbEnabled,
                NbpNotchesEnabled = nrSeed.NbpNotchesEnabled,
                NbMode = nrSeed.NbMode,
                NbThreshold = nrSeed.NbThreshold,
            };
            ApplyCfcToEntry(existing, config);
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Insert(existing);
        }
        else
        {
            ApplyCfcToEntry(existing, config);
            existing.UpdatedUtc = DateTime.UtcNow;
            _entries.Update(existing);
        }
    }

    private static void ApplyCfcToEntry(DspSettingsEntry e, CfcConfig c)
    {
        e.CfcEnabled = c.Enabled;
        e.CfcPostEqEnabled = c.PostEqEnabled;
        e.CfcPreCompDb = c.PreCompDb;
        e.CfcPrePeqDb = c.PrePeqDb;
        e.CfcBand1Freq = c.Bands[0].FreqHz;  e.CfcBand1Comp = c.Bands[0].CompLevelDb;  e.CfcBand1Post = c.Bands[0].PostGainDb;
        e.CfcBand2Freq = c.Bands[1].FreqHz;  e.CfcBand2Comp = c.Bands[1].CompLevelDb;  e.CfcBand2Post = c.Bands[1].PostGainDb;
        e.CfcBand3Freq = c.Bands[2].FreqHz;  e.CfcBand3Comp = c.Bands[2].CompLevelDb;  e.CfcBand3Post = c.Bands[2].PostGainDb;
        e.CfcBand4Freq = c.Bands[3].FreqHz;  e.CfcBand4Comp = c.Bands[3].CompLevelDb;  e.CfcBand4Post = c.Bands[3].PostGainDb;
        e.CfcBand5Freq = c.Bands[4].FreqHz;  e.CfcBand5Comp = c.Bands[4].CompLevelDb;  e.CfcBand5Post = c.Bands[4].PostGainDb;
        e.CfcBand6Freq = c.Bands[5].FreqHz;  e.CfcBand6Comp = c.Bands[5].CompLevelDb;  e.CfcBand6Post = c.Bands[5].PostGainDb;
        e.CfcBand7Freq = c.Bands[6].FreqHz;  e.CfcBand7Comp = c.Bands[6].CompLevelDb;  e.CfcBand7Post = c.Bands[6].PostGainDb;
        e.CfcBand8Freq = c.Bands[7].FreqHz;  e.CfcBand8Comp = c.Bands[7].CompLevelDb;  e.CfcBand8Post = c.Bands[7].PostGainDb;
        e.CfcBand9Freq = c.Bands[8].FreqHz;  e.CfcBand9Comp = c.Bands[8].CompLevelDb;  e.CfcBand9Post = c.Bands[8].PostGainDb;
        e.CfcBand10Freq = c.Bands[9].FreqHz; e.CfcBand10Comp = c.Bands[9].CompLevelDb; e.CfcBand10Post = c.Bands[9].PostGainDb;
    }

    public void Dispose() => _db.Dispose();

}

public sealed class DspSettingsEntry
{
    public int Id { get; set; }
    public string ProfileId { get; set; } = string.Empty;
    public NrMode NrMode { get; set; }
    public bool AnfEnabled { get; set; }
    public bool SnbEnabled { get; set; }
    public bool NbpNotchesEnabled { get; set; }
    public NbMode NbMode { get; set; }
    public double NbThreshold { get; set; }
    // NR2 (EMNR) post2 comfort-noise tunables. Null means "engine default".
    public bool? EmnrPost2Run { get; set; }
    public double? EmnrPost2Factor { get; set; }
    public double? EmnrPost2Nlevel { get; set; }
    public double? EmnrPost2Rate { get; set; }
    public int? EmnrPost2Taper { get; set; }
    // NR2 (EMNR) core algorithm selectors + Trained-method T1/T2. Null means
    // "engine default" — engine falls back to NrDefaults at apply time so
    // clearing a field reverts to the Thetis-parity baseline.
    public int? EmnrGainMethod { get; set; }
    public int? EmnrNpeMethod { get; set; }
    public bool? EmnrAeRun { get; set; }
    public double? EmnrTrainT1 { get; set; }
    public double? EmnrTrainT2 { get; set; }
    // NR4 (SBNR) tunables. Null means "engine default".
    public double? Nr4ReductionAmount { get; set; }
    public double? Nr4SmoothingFactor { get; set; }
    public double? Nr4WhiteningFactor { get; set; }
    public double? Nr4NoiseRescale { get; set; }
    public double? Nr4PostFilterThreshold { get; set; }
    public int? Nr4NoiseScalingType { get; set; }
    public int? Nr4Position { get; set; }
    // CFC (Continuous Frequency Compressor) — issue #123. Master flags are
    // nullable so legacy rows (pre-CFC) load with CfcEnabled=null and
    // GetCfc() returns null → operator gets CfcConfig.Default. Per-band
    // scalars are non-nullable doubles and default to 0 on legacy rows; the
    // null Enabled flag prevents those zeros from being interpreted as a
    // valid CFC config.
    public bool? CfcEnabled { get; set; }
    public bool? CfcPostEqEnabled { get; set; }
    public double? CfcPreCompDb { get; set; }
    public double? CfcPrePeqDb { get; set; }
    public double CfcBand1Freq { get; set; }  public double CfcBand1Comp { get; set; }  public double CfcBand1Post { get; set; }
    public double CfcBand2Freq { get; set; }  public double CfcBand2Comp { get; set; }  public double CfcBand2Post { get; set; }
    public double CfcBand3Freq { get; set; }  public double CfcBand3Comp { get; set; }  public double CfcBand3Post { get; set; }
    public double CfcBand4Freq { get; set; }  public double CfcBand4Comp { get; set; }  public double CfcBand4Post { get; set; }
    public double CfcBand5Freq { get; set; }  public double CfcBand5Comp { get; set; }  public double CfcBand5Post { get; set; }
    public double CfcBand6Freq { get; set; }  public double CfcBand6Comp { get; set; }  public double CfcBand6Post { get; set; }
    public double CfcBand7Freq { get; set; }  public double CfcBand7Comp { get; set; }  public double CfcBand7Post { get; set; }
    public double CfcBand8Freq { get; set; }  public double CfcBand8Comp { get; set; }  public double CfcBand8Post { get; set; }
    public double CfcBand9Freq { get; set; }  public double CfcBand9Comp { get; set; }  public double CfcBand9Post { get; set; }
    public double CfcBand10Freq { get; set; } public double CfcBand10Comp { get; set; } public double CfcBand10Post { get; set; }
    // AGC top (max gain) in dB. Null on legacy rows (pre-AGC-persist) so
    // RadioService can fall back to the baseline default for first-run.
    public double? AgcTopDb { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
