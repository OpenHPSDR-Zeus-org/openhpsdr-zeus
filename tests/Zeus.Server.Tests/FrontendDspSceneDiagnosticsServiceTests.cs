// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class FrontendDspSceneDiagnosticsServiceTests
{
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
            MaxSnrDb: 18.24,
            CoherentMaxSnrDb: 17.86,
            OccupiedPct: 4.42,
            CoherentOccupiedPct: 2.36,
            ImpulsivePct: 0.12,
            PeakCount: 3,
            CoherentPeakCount: 2,
            CoherentSubthresholdSignal: true,
            SourceAtUtc: sourceAt));

        Assert.Equal("client one", stored.SourceClientId);
        Assert.Equal(sourceAt, stored.SourceAtUtc);
        Assert.Equal(18.2, stored.MaxSnrDb);
        Assert.Equal(17.9, stored.CoherentMaxSnrDb);

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
        Assert.Equal(3, root.GetProperty("peakCount").GetInt32());
        Assert.True(root.GetProperty("coherentSubthresholdSignal").GetBoolean());
        Assert.True(root.GetProperty("ageMs").GetInt64() >= 0);
        Assert.True(root.GetProperty("sourceAgeMs").GetInt64() >= 0);
        Assert.Equal(sourceAt, root.GetProperty("sourceAtUtc").GetDateTimeOffset());
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
}
