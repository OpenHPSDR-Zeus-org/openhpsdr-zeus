// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class FrontendDspSceneDiagnosticsServiceTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
    public void SmartNrCondition_ReportsAlignedNr5Runtime()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, smartNrProfile: "NR5");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            service.SmartNrCondition(Runtime(requested: "Nr5", effective: "Nr5")),
            CamelCaseJson));
        var root = doc.RootElement;

        Assert.Equal("Nr5", root.GetProperty("expectedNrMode").GetString());
        Assert.True(root.GetProperty("runtimeAligned").GetBoolean());
        Assert.Equal("aligned", root.GetProperty("runtimeAlignmentStatus").GetString());
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

    private static void PublishScene(FrontendDspSceneDiagnosticsService service, string smartNrProfile) =>
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
            WdspNr5SpnrAvailable: true,
            Nr4Readiness: "available",
            Nr5Readiness: "available",
            RequestedNrMode: requested,
            EffectiveNrMode: effective,
            Nr5SpnrDiagnostics: null);
}
