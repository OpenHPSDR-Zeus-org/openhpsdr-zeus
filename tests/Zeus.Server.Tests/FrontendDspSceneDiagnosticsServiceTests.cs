// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Zeus.Server;

namespace Zeus.Server.Tests;

[Trait("Category", "DspModernization")]
public sealed class FrontendDspSceneDiagnosticsServiceTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static FrontendDspSceneDiagnosticsRequest AdjacentNoiseRequest(DateTimeOffset sourceAt) => new(
        SourceClientId: "client",
        Mode: "USB",
        SignalProfile: "dx",
        SignalReason: "weak signal with profiled adjacent noise",
        SmartNrProfile: "NR4",
        SmartNrReason: "adjacent noise profile",
        SmartNrRecommendation: "Use NR4 profiled weak-signal recovery",
        SmartNrHeldByRxChain: false,
        SmartNrRxChainLabel: "RX chain optimized",
        SmartNrRxChainRecommendation: "Hold front-end settings",
        SmartNrRxChainTone: "neutral",
        SmartNrRxChainScore: 97,
        MaxSnrDb: 6.8,
        CoherentMaxSnrDb: 6.2,
        OccupiedPct: 1.5,
        CoherentOccupiedPct: 0.9,
        ImpulsivePct: 0.0,
        PeakCount: 0,
        CoherentPeakCount: 0,
        CoherentSubthresholdSignal: true,
        SourceAtUtc: sourceAt,
        AdjacentNoiseUsable: true,
        AdjacentNoiseBins: 92,
        AdjacentNoiseLeftBins: 43,
        AdjacentNoiseRightBins: 49,
        AdjacentNoiseFloorDb: -103.5,
        AdjacentNoiseP10Db: -104.0,
        AdjacentNoiseP50Db: -103.5,
        AdjacentNoiseP90Db: -102.9,
        AdjacentNoiseLeftFloorDb: -103.7,
        AdjacentNoiseRightFloorDb: -103.4,
        AdjacentNoiseSlopeDbPerKhz: 0.0,
        AdjacentNoiseRejectedPct: 21.5);

    private static FrontendDspSceneDiagnosticsRequest SceneTopPeaksRequest(
        params FrontendDspScenePeakDto[] topPeaks) => new(
            SourceClientId: "client",
            Mode: "USB",
            SignalProfile: "dx",
            SignalReason: "scene top peaks",
            SmartNrProfile: "NR4",
            SmartNrReason: "leveler evidence",
            SmartNrRecommendation: "Use NR4 leveler evidence",
            SmartNrHeldByRxChain: false,
            SmartNrRxChainLabel: "RX chain optimized",
            SmartNrRxChainRecommendation: "Hold front-end settings",
            SmartNrRxChainTone: "neutral",
            SmartNrRxChainScore: 100,
            MaxSnrDb: null,
            CoherentMaxSnrDb: null,
            OccupiedPct: null,
            CoherentOccupiedPct: null,
            ImpulsivePct: null,
            PeakCount: topPeaks.Length,
            CoherentPeakCount: topPeaks.Count(static peak => peak.Coherent),
            CoherentSubthresholdSignal: null,
            SourceAtUtc: DateTimeOffset.UtcNow,
            TopPeaks: topPeaks);

    [Fact]
    public void Update_StoresSanitizedFrontendSceneForDiagnosticsSnapshot()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        var sourceAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        var stored = service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "  client   one  ",
            Mode: "USB",
            SignalProfile: "dx",
            SignalReason: "sparse weak signal",
            SmartNrProfile: "NR2",
            SmartNrReason: "SSB noise profile",
            SmartNrRecommendation: "Hold levels; use Smart NR/filtering",
            SmartNrHeldByRxChain: false,
            SmartNrRxChainLabel: "RX chain optimized",
            SmartNrRxChainRecommendation: "Hold front-end settings",
            SmartNrRxChainTone: "neutral",
            SmartNrRxChainScore: 91,
            MaxSnrDb: 18.24,
            CoherentMaxSnrDb: 17.86,
            OccupiedPct: 4.42,
            CoherentOccupiedPct: 2.36,
            ImpulsivePct: 0.12,
            PeakCount: 3,
            CoherentPeakCount: 2,
            CoherentSubthresholdSignal: true,
            SourceAtUtc: sourceAt,
            TopPeaks: new[]
            {
                new FrontendDspScenePeakDto(
                    FrequencyHz: 14_269_234,
                    OffsetHz: 2_234,
                    SnrDb: 24.16,
                    Dbfs: -86.24,
                    Confidence: 0.7423,
                    Coherent: true),
                new FrontendDspScenePeakDto(
                    FrequencyHz: 99_000_000,
                    OffsetHz: 0,
                    SnrDb: 12.0,
                    Dbfs: -90.0,
                    Confidence: 0.5,
                    Coherent: true),
            },
            AdjacentNoiseUsable: true,
            AdjacentNoiseBins: 84,
            AdjacentNoiseLeftBins: 40,
            AdjacentNoiseRightBins: 44,
            AdjacentNoiseFloorDb: -111.24,
            AdjacentNoiseP10Db: -113.18,
            AdjacentNoiseP50Db: -111.24,
            AdjacentNoiseP90Db: -108.73,
            AdjacentNoiseLeftFloorDb: -112.04,
            AdjacentNoiseRightFloorDb: -110.58,
            AdjacentNoiseSlopeDbPerKhz: 0.23,
            AdjacentNoiseRejectedPct: 4.84));

        Assert.Equal("client one", stored.SourceClientId);
        Assert.Equal(sourceAt, stored.SourceAtUtc);
        Assert.Equal(18.2, stored.MaxSnrDb);
        Assert.Equal(17.9, stored.CoherentMaxSnrDb);
        Assert.True(stored.AdjacentNoiseUsable);
        Assert.Equal(84, stored.AdjacentNoiseBins);
        Assert.Equal(-111.2, stored.AdjacentNoiseFloorDb);
        Assert.Equal(4.8, stored.AdjacentNoiseRejectedPct);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("fresh", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Contains("fresh", root.GetProperty("diagnosticRecommendation").GetString());
        Assert.Contains("subthreshold weak-signal", root.GetProperty("diagnosticRecommendation").GetString());
        Assert.Equal("dx", root.GetProperty("signalProfile").GetString());
        Assert.Equal("NR2", root.GetProperty("smartNrProfile").GetString());
        Assert.Equal("Hold front-end settings", root.GetProperty("smartNrRxChainRecommendation").GetString());
        Assert.Equal("neutral", root.GetProperty("smartNrRxChainTone").GetString());
        Assert.Equal(91, root.GetProperty("smartNrRxChainScore").GetInt32());
        Assert.Equal(3, root.GetProperty("peakCount").GetInt32());
        Assert.True(root.GetProperty("coherentSubthresholdSignal").GetBoolean());
        var topPeaks = root.GetProperty("topPeaks").EnumerateArray().ToArray();
        Assert.Single(topPeaks);
        Assert.Equal(14_269_234, topPeaks[0].GetProperty("frequencyHz").GetInt64());
        Assert.Equal(2_234, topPeaks[0].GetProperty("offsetHz").GetInt32());
        Assert.Equal(24.2, topPeaks[0].GetProperty("snrDb").GetDouble());
        Assert.Equal(-86.2, topPeaks[0].GetProperty("dbfs").GetDouble());
        Assert.Equal(0.742, topPeaks[0].GetProperty("confidence").GetDouble());
        Assert.True(topPeaks[0].GetProperty("coherent").GetBoolean());
        Assert.True(root.GetProperty("adjacentNoiseUsable").GetBoolean());
        Assert.Equal(84, root.GetProperty("adjacentNoiseBins").GetInt32());
        Assert.Equal(40, root.GetProperty("adjacentNoiseLeftBins").GetInt32());
        Assert.Equal(44, root.GetProperty("adjacentNoiseRightBins").GetInt32());
        Assert.Equal(-111.2, root.GetProperty("adjacentNoiseFloorDb").GetDouble());
        Assert.Equal(-113.2, root.GetProperty("adjacentNoiseP10Db").GetDouble());
        Assert.Equal(-108.7, root.GetProperty("adjacentNoiseP90Db").GetDouble());
        Assert.Equal(-112.0, root.GetProperty("adjacentNoiseLeftFloorDb").GetDouble());
        Assert.Equal(-110.6, root.GetProperty("adjacentNoiseRightFloorDb").GetDouble());
        Assert.Equal(0.2, root.GetProperty("adjacentNoiseSlopeDbPerKhz").GetDouble());
        Assert.Equal(4.8, root.GetProperty("adjacentNoiseRejectedPct").GetDouble());
        Assert.True(root.GetProperty("ageMs").GetInt64() >= 0);
        Assert.True(root.GetProperty("sourceAgeMs").GetInt64() >= 0);
        Assert.Equal(sourceAt, root.GetProperty("sourceAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public void TryGetFreshLevelerTopPeak_PrefersNearestPassbandPeak()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        service.Update(SceneTopPeaksRequest(
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_342_000,
                OffsetHz: 52_000,
                SnrDb: 31.0,
                Dbfs: -84.0,
                Confidence: 0.94,
                Coherent: true),
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_291_200,
                OffsetHz: 1_200,
                SnrDb: 13.0,
                Dbfs: -92.0,
                Confidence: 0.72,
                Coherent: true),
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_292_100,
                OffsetHz: 2_100,
                SnrDb: 24.0,
                Dbfs: -86.0,
                Confidence: 0.91,
                Coherent: true)));

        var peak = service.TryGetFreshLevelerTopPeak();

        Assert.NotNull(peak);
        Assert.Equal(1_200, peak.OffsetHz);
    }

    [Fact]
    public void TryGetFreshLevelerTopPeak_UsesSignedFilterPassbandForUsb()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        service.Update(SceneTopPeaksRequest(
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_276_441,
                OffsetHz: -559,
                SnrDb: 23.1,
                Dbfs: -81.5,
                Confidence: 0.914,
                Coherent: true),
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_278_200,
                OffsetHz: 1_200,
                SnrDb: 13.0,
                Dbfs: -92.0,
                Confidence: 0.72,
                Coherent: true)));

        var peak = service.TryGetFreshLevelerTopPeak(filterLowHz: 100, filterHighHz: 3_100);

        Assert.NotNull(peak);
        Assert.Equal(1_200, peak.OffsetHz);
    }

    [Fact]
    public void TryGetFreshLevelerTopPeak_TreatsWrongSidebandPeakAsOutOfPassband()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        service.Update(SceneTopPeaksRequest(
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_276_441,
                OffsetHz: -559,
                SnrDb: 23.1,
                Dbfs: -81.5,
                Confidence: 0.914,
                Coherent: true),
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_249_628,
                OffsetHz: -27_372,
                SnrDb: 9.9,
                Dbfs: -88.6,
                Confidence: 0.734,
                Coherent: true)));

        var peak = service.TryGetFreshLevelerTopPeak(filterLowHz: 100, filterHighHz: 3_100);

        Assert.NotNull(peak);
        Assert.Equal(-559, peak.OffsetHz);
    }

    [Fact]
    public void TryGetFreshLevelerTopPeak_UsesDominantOffPassbandPeakWhenPassbandIsEmpty()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        service.Update(SceneTopPeaksRequest(
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_294_200,
                OffsetHz: 4_200,
                SnrDb: 34.0,
                Dbfs: -79.0,
                Confidence: 0.97,
                Coherent: false),
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_293_700,
                OffsetHz: 3_700,
                SnrDb: 11.2,
                Dbfs: -94.0,
                Confidence: 0.70,
                Coherent: true),
            new FrontendDspScenePeakDto(
                FrequencyHz: 14_240_400,
                OffsetHz: -49_600,
                SnrDb: 22.9,
                Dbfs: -84.4,
                Confidence: 0.912,
                Coherent: true)));

        var peak = service.TryGetFreshLevelerTopPeak();

        Assert.NotNull(peak);
        Assert.Equal(-49_600, peak.OffsetHz);
    }

    [Fact]
    public void TryGetFreshAdjacentNoiseProfile_KeepsLatestUsableProfileAcrossRejectedFrames()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "client",
            Mode: null,
            SignalProfile: null,
            SignalReason: null,
            SmartNrProfile: null,
            SmartNrReason: null,
            SmartNrRecommendation: null,
            SmartNrHeldByRxChain: null,
            SmartNrRxChainLabel: null,
            SmartNrRxChainRecommendation: null,
            SmartNrRxChainTone: null,
            SmartNrRxChainScore: null,
            MaxSnrDb: null,
            CoherentMaxSnrDb: null,
            OccupiedPct: null,
            CoherentOccupiedPct: null,
            ImpulsivePct: null,
            PeakCount: null,
            CoherentPeakCount: null,
            CoherentSubthresholdSignal: null,
            SourceAtUtc: DateTimeOffset.UtcNow,
            AdjacentNoiseUsable: true,
            AdjacentNoiseBins: 88,
            AdjacentNoiseLeftBins: 42,
            AdjacentNoiseRightBins: 46,
            AdjacentNoiseFloorDb: -105.2,
            AdjacentNoiseP10Db: -105.8,
            AdjacentNoiseP50Db: -105.2,
            AdjacentNoiseP90Db: -104.1,
            AdjacentNoiseLeftFloorDb: -105.4,
            AdjacentNoiseRightFloorDb: -105.1,
            AdjacentNoiseSlopeDbPerKhz: 0.1,
            AdjacentNoiseRejectedPct: 7.3));

        service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "client",
            Mode: null,
            SignalProfile: null,
            SignalReason: null,
            SmartNrProfile: null,
            SmartNrReason: null,
            SmartNrRecommendation: null,
            SmartNrHeldByRxChain: null,
            SmartNrRxChainLabel: null,
            SmartNrRxChainRecommendation: null,
            SmartNrRxChainTone: null,
            SmartNrRxChainScore: null,
            MaxSnrDb: null,
            CoherentMaxSnrDb: null,
            OccupiedPct: null,
            CoherentOccupiedPct: null,
            ImpulsivePct: null,
            PeakCount: null,
            CoherentPeakCount: null,
            CoherentSubthresholdSignal: null,
            SourceAtUtc: DateTimeOffset.UtcNow,
            AdjacentNoiseUsable: false,
            AdjacentNoiseBins: 6,
            AdjacentNoiseRejectedPct: 96.0));

        var profile = service.TryGetFreshAdjacentNoiseProfile();

        Assert.NotNull(profile);
        Assert.Equal(88, profile.Bins);
        Assert.Equal(42, profile.LeftBins);
        Assert.Equal(46, profile.RightBins);
        Assert.Equal(-105.2, profile.FloorDb);
        Assert.Equal(-105.4, profile.LeftFloorDb);
        Assert.Equal(-105.1, profile.RightFloorDb);
        Assert.Equal(7.3, profile.RejectedPct);
    }

    [Fact]
    public void TryGetFreshAdjacentNoiseProfile_CoastsAgingSourceEvidenceForLeveler()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        service.Update(AdjacentNoiseRequest(DateTimeOffset.UtcNow.AddSeconds(-25)));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var root = doc.RootElement;
        Assert.Equal("aging", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.True(root.GetProperty("sourceAgeMs").GetInt64() >= 15_000);

        var profile = service.TryGetFreshAdjacentNoiseProfile();

        Assert.NotNull(profile);
        Assert.Equal(92, profile.Bins);
        Assert.Equal(-103.5, profile.FloorDb);
        Assert.True(profile.SourceAgeMs >= 15_000);
        Assert.True(profile.SourceAgeMs <= 45_000);
    }

    [Fact]
    public void TryGetFreshAdjacentNoiseProfile_RejectsStaleSourceEvidencePastCoastWindow()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        service.Update(AdjacentNoiseRequest(DateTimeOffset.UtcNow.AddSeconds(-60)));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var root = doc.RootElement;
        Assert.Equal("stale", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("fresh").GetBoolean());
        Assert.True(root.GetProperty("stale").GetBoolean());
        Assert.True(root.GetProperty("sourceAgeMs").GetInt64() > 45_000);

        Assert.Null(service.TryGetFreshAdjacentNoiseProfile());
    }

    [Fact]
    public void Snapshot_ReportsMissingBeforeFrontendPublishesScene()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal("missing", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Contains("No frontend DSP scene", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public void Snapshot_ReportsWeakSignalWhenRxChainHoldsSmartNr()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "client",
            Mode: "USB",
            SignalProfile: "dx",
            SignalReason: "coherent ridge below normal gate",
            SmartNrProfile: "NR2",
            SmartNrReason: "weak signal",
            SmartNrRecommendation: "Wait for RX chain",
            SmartNrHeldByRxChain: true,
            SmartNrRxChainLabel: "ADC headroom limited",
            SmartNrRxChainRecommendation: "Add 3-6 dB attenuation",
            SmartNrRxChainTone: "protect",
            SmartNrRxChainScore: 62,
            MaxSnrDb: 7.1,
            CoherentMaxSnrDb: 6.8,
            OccupiedPct: 1.2,
            CoherentOccupiedPct: 0.8,
            ImpulsivePct: 0,
            PeakCount: 0,
            CoherentPeakCount: 0,
            CoherentSubthresholdSignal: true));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var recommendation = doc.RootElement.GetProperty("diagnosticRecommendation").GetString();

        Assert.Contains("coherent subthreshold weak-signal", recommendation);
        Assert.Contains("constrained by RX-chain health", recommendation);
        Assert.Contains("ADC headroom limited", recommendation);
        Assert.Contains("Add 3-6 dB attenuation", recommendation);
    }

    [Fact]
    public void Snapshot_ReportsStaleWhenSourceEvidenceIsOldDespiteFreshPublish()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "client",
            Mode: "USB",
            SignalProfile: "dx",
            SignalReason: "old scene",
            SmartNrProfile: "NR2",
            SmartNrReason: "old weak signal",
            SmartNrRecommendation: "Wait for live evidence",
            SmartNrHeldByRxChain: false,
            SmartNrRxChainLabel: "RX chain optimized",
            SmartNrRxChainRecommendation: "Hold front-end settings",
            SmartNrRxChainTone: "neutral",
            SmartNrRxChainScore: 96,
            MaxSnrDb: 7.1,
            CoherentMaxSnrDb: 6.8,
            OccupiedPct: 1.2,
            CoherentOccupiedPct: 0.8,
            ImpulsivePct: 0,
            PeakCount: 0,
            CoherentPeakCount: 0,
            CoherentSubthresholdSignal: true,
            SourceAtUtc: DateTimeOffset.UtcNow.AddSeconds(-60)));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var root = doc.RootElement;

        Assert.Equal("stale", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("fresh").GetBoolean());
        Assert.True(root.GetProperty("stale").GetBoolean());
        Assert.True(root.GetProperty("ageMs").GetInt64() < 45_000);
        Assert.True(root.GetProperty("sourceAgeMs").GetInt64() >= 45_000);
        Assert.Contains("source evidence is stale", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public void Snapshot_ReportsFreshHeartbeatWhenClientHasNoSceneEvidenceYet()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "client",
            Mode: "USB",
            SignalProfile: null,
            SignalReason: null,
            SmartNrProfile: null,
            SmartNrReason: null,
            SmartNrRecommendation: null,
            SmartNrHeldByRxChain: null,
            SmartNrRxChainLabel: null,
            SmartNrRxChainRecommendation: null,
            SmartNrRxChainTone: null,
            SmartNrRxChainScore: null,
            MaxSnrDb: null,
            CoherentMaxSnrDb: null,
            OccupiedPct: null,
            CoherentOccupiedPct: null,
            ImpulsivePct: null,
            PeakCount: null,
            CoherentPeakCount: null,
            CoherentSubthresholdSignal: null,
            SourceAtUtc: null));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var root = doc.RootElement;

        Assert.Equal("fresh", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Contains("heartbeat is fresh", root.GetProperty("diagnosticRecommendation").GetString());
        Assert.Contains("no Signal Intelligence or Smart NR", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public void Snapshot_ReportsClockSkewWhenFrontendSourceTimeIsInTheFuture()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "client",
            Mode: "USB",
            SignalProfile: "dx",
            SignalReason: "future scene",
            SmartNrProfile: "NR2",
            SmartNrReason: "future weak signal",
            SmartNrRecommendation: "Wait for valid clock",
            SmartNrHeldByRxChain: false,
            SmartNrRxChainLabel: "RX chain optimized",
            SmartNrRxChainRecommendation: "Hold front-end settings",
            SmartNrRxChainTone: "neutral",
            SmartNrRxChainScore: 94,
            MaxSnrDb: 7.1,
            CoherentMaxSnrDb: 6.8,
            OccupiedPct: 1.2,
            CoherentOccupiedPct: 0.8,
            ImpulsivePct: 0,
            PeakCount: 0,
            CoherentPeakCount: 0,
            CoherentSubthresholdSignal: true,
            SourceAtUtc: DateTimeOffset.UtcNow.AddSeconds(30)));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(service.Snapshot()));
        var root = doc.RootElement;

        Assert.Equal("clock-skew", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal(0, root.GetProperty("sourceAgeMs").GetInt64());
        Assert.True(root.GetProperty("sourceClockSkewMs").GetInt64() > 25_000);
        Assert.Contains("future", root.GetProperty("diagnosticRecommendation").GetString());
        Assert.Contains("clocks", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public void SmartNrCondition_ReportsAlignedNr2Runtime()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, smartNrProfile: "NR2");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Emnr", effective: "Emnr")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.Equal("Emnr", root.GetProperty("expectedNrMode").GetString());
        Assert.True(root.GetProperty("runtimeAligned").GetBoolean());
        Assert.Equal("aligned", root.GetProperty("runtimeAlignmentStatus").GetString());
        Assert.Contains("aligned", root.GetProperty("runtimeAlignmentRecommendation").GetString());
    }

    [Fact]
    public void SmartNrCondition_ReportsAlignedSbnrRuntime()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, smartNrProfile: "NR4");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Sbnr", effective: "Sbnr")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.Equal("Sbnr", root.GetProperty("expectedNrMode").GetString());
        Assert.True(root.GetProperty("runtimeAligned").GetBoolean());
        Assert.Equal("aligned", root.GetProperty("runtimeAlignmentStatus").GetString());
    }

    [Fact]
    public void SmartNrCondition_ReportsAlignedRnnrRuntime()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, smartNrProfile: "NR3");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Rnnr", effective: "Rnnr")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.Equal("Rnnr", root.GetProperty("expectedNrMode").GetString());
        Assert.True(root.GetProperty("runtimeAligned").GetBoolean());
        Assert.Equal("aligned", root.GetProperty("runtimeAlignmentStatus").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void SmartNrCondition_UsesRuntimeOnlyAlignmentWhenSceneHasNoProfileButSbnrIsStable(string? smartNrProfile)
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, smartNrProfile);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Sbnr", effective: "Sbnr")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("profile").ValueKind is JsonValueKind.Null);
        Assert.Equal("Sbnr", root.GetProperty("expectedNrMode").GetString());
        Assert.True(root.GetProperty("runtimeAligned").GetBoolean());
        Assert.Equal("runtime-only-aligned", root.GetProperty("runtimeAlignmentStatus").GetString());
        Assert.Contains("backend runtime evidence only", root.GetProperty("runtimeAlignmentRecommendation").GetString());
    }

    [Fact]
    public void SmartNrCondition_DoesNotInferRuntimeOnlyAlignmentWhenBlankProfileRuntimeIsPending()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, smartNrProfile: null);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Sbnr", effective: "Emnr")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("expectedNrMode").ValueKind is JsonValueKind.Null);
        Assert.True(root.GetProperty("runtimeAligned").ValueKind is JsonValueKind.Null);
        Assert.Equal("no-profile", root.GetProperty("runtimeAlignmentStatus").GetString());
    }

    [Fact]
    public void SmartNrCondition_ReportsPendingWhenRequestedModeHasNotApplied()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, smartNrProfile: "NR4");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Sbnr", effective: "Emnr")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.Equal("Sbnr", root.GetProperty("expectedNrMode").GetString());
        Assert.False(root.GetProperty("runtimeAligned").GetBoolean());
        Assert.Equal("apply-pending", root.GetProperty("runtimeAlignmentStatus").GetString());
        Assert.Contains("effective runtime is still Emnr", root.GetProperty("runtimeAlignmentRecommendation").GetString());
    }

    [Theory]
    [InlineData("Notch")]
    [InlineData("Light")]
    public void SmartNrCondition_MapsNonSpectralProfilesToOff(string profile)
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, smartNrProfile: profile);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Emnr", effective: "Emnr")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.Equal("Off", root.GetProperty("expectedNrMode").GetString());
        Assert.False(root.GetProperty("runtimeAligned").GetBoolean());
        Assert.Equal("mismatched", root.GetProperty("runtimeAlignmentStatus").GetString());
        Assert.Contains("maps to WDSP Off", root.GetProperty("runtimeAlignmentRecommendation").GetString());
    }

    [Fact]
    public void SmartNrCondition_ReportsNoProfileBeforeSceneEvidence()
    {
        var service = new FrontendDspSceneDiagnosticsService();

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Off", effective: "Off")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("expectedNrMode").ValueKind is JsonValueKind.Null);
        Assert.True(root.GetProperty("runtimeAligned").ValueKind is JsonValueKind.Null);
        Assert.Equal("no-profile", root.GetProperty("runtimeAlignmentStatus").GetString());
        Assert.Contains("cannot be evaluated", root.GetProperty("runtimeAlignmentRecommendation").GetString());
    }

    private static void PublishScene(FrontendDspSceneDiagnosticsService service, string? smartNrProfile) =>
        service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "client",
            Mode: "USB",
            SignalProfile: "dx",
            SignalReason: "coherent ridge",
            SmartNrProfile: smartNrProfile,
            SmartNrReason: "test recommendation",
            SmartNrRecommendation: "test",
            SmartNrHeldByRxChain: false,
            SmartNrRxChainLabel: "RX chain optimized",
            SmartNrRxChainRecommendation: "Hold front-end settings",
            SmartNrRxChainTone: "neutral",
            SmartNrRxChainScore: 91,
            MaxSnrDb: 12.3,
            CoherentMaxSnrDb: 11.8,
            OccupiedPct: 2.1,
            CoherentOccupiedPct: 1.9,
            ImpulsivePct: 0,
            PeakCount: 2,
            CoherentPeakCount: 2,
            CoherentSubthresholdSignal: false));

    private static DspNrRuntimeSnapshot Runtime(string requested, string effective, bool active = true) =>
        new(
            WdspActive: active,
            WdspNativeLoadable: true,
            WdspEmnrPost2Available: true,
            WdspNr4SbnrAvailable: true,
            Nr4Readiness: "available",
            RequestedNrMode: requested,
            EffectiveNrMode: effective);
}
