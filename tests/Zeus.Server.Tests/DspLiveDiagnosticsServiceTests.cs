// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DspLiveDiagnosticsServiceTests
{
    [Fact]
    public void Build_ReportsMissingFrontendSceneAsStandbyConstraint()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        var condition = service.SmartNrCondition(Runtime("Off", "Off"));

        var diag = DspLiveDiagnosticsService.Build(condition);

        Assert.Equal(1, diag.SchemaVersion);
        Assert.Equal("frontend-scene-missing", diag.Status);
        Assert.Equal("standby", diag.QualityTone);
        Assert.False(diag.ReadyForLiveBenchmark);
        Assert.Contains("frontend-dsp-scene-missing", diag.Constraints);
        Assert.Contains("frontend-dsp-scene-publisher", diag.CandidateTools);
        Assert.Contains("offline-dsp-benchmark-harness", diag.CandidateTools);
        Assert.Contains("dsp-benchmark-acceptance-plan", diag.CandidateTools);
        Assert.Contains("opt-in", diag.RolloutGate);
        Assert.Equal("/api/dsp/benchmark-plan", diag.BenchmarkPlanEndpoint);
        Assert.True(diag.BenchmarkScenarioCount >= 12);
        Assert.Contains("frontend-scene-freshness", diag.NextBenchmarkScenarios);
        Assert.Contains(diag.ExternalEngineCandidates, c => c.Id == "rnnoise");
        Assert.All(diag.ExternalEngineCandidates, c => Assert.Equal("off", c.DefaultState));
    }

    [Fact]
    public void Build_AlignedFreshNr5SceneIsReadyForLiveBenchmarkButStillOptIn()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR5", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Nr5", nr5Available: true, nr5: Nr5(learnedFrames: 80, confidence: 0.72, agcGate: 0.66)),
            RxChain(score: 94));

        var diag = DspLiveDiagnosticsService.Build(condition);

        Assert.Equal("ready-for-live-benchmark", diag.Status);
        Assert.Equal("ready", diag.QualityTone);
        Assert.True(diag.ReadinessScore >= 85);
        Assert.True(diag.ReadyForLiveBenchmark);
        Assert.True(diag.RuntimeAligned);
        Assert.Equal("Nr5", diag.EffectiveNrMode);
        Assert.Equal(0.72, diag.Nr5SignalConfidence);
        Assert.Contains("nr5-spnr-diagnostics", diag.CandidateTools);
        Assert.Contains("weak-cw-carrier", diag.NextBenchmarkScenarios);
        Assert.Contains("agc-level-step", diag.NextBenchmarkScenarios);
        Assert.Contains(diag.BenchmarkAcceptanceGates, gate => gate.Contains("No weak-signal loss", StringComparison.Ordinal));
        Assert.Contains("external-post-demod-bakeoff:deepfilternet", diag.CandidateTools);
        var deepFilter = Assert.Single(diag.ExternalEngineCandidates, c => c.Id == "deepfilternet");
        Assert.Contains("ssb-like-speech", deepFilter.RequiredBenchmarks);
        Assert.Contains("Model artifact", string.Join(" ", deepFilter.Blockers));
        Assert.Contains("ready-for-g2-live-benchmark", diag.Evidence);
        Assert.Equal("opt-in-only-until-benchmark-and-g2-on-air-acceptance", diag.RolloutGate);
    }

    [Fact]
    public void Build_RxChainProtectBecomesProtectStatus()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR2", held: true, rxScore: 55, rxTone: "protect", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Emnr", "Emnr"),
            RxChain(score: 55, adcOverload: true));

        var diag = DspLiveDiagnosticsService.Build(condition);

        Assert.Equal("rx-chain-protect", diag.Status);
        Assert.Equal("protect", diag.QualityTone);
        Assert.False(diag.ReadyForLiveBenchmark);
        Assert.Contains("smart-nr-held-by-rx-chain", diag.Constraints);
        Assert.Contains("rx-chain-protect", diag.Constraints);
        Assert.Contains("Resolve RX-chain health", string.Join(" ", diag.RecommendedActions));
    }

    [Fact]
    public void Build_Nr5RecommendationWithoutExportsReportsCapabilityLimit()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR5", held: false, rxScore: 90, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Off", nr5Available: false),
            RxChain(score: 90));

        var diag = DspLiveDiagnosticsService.Build(condition);

        Assert.Equal("nr-capability-limited", diag.Status);
        Assert.Equal("protect", diag.QualityTone);
        Assert.False(diag.ReadyForLiveBenchmark);
        Assert.Contains("nr5-spnr-exports-missing", diag.Constraints);
        Assert.Contains("Rebuild or install WDSP with NR5/SPNR exports", string.Join(" ", diag.RecommendedActions));
        Assert.Contains("WDSP exports", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void BenchmarkPlanCatalog_CoversRxTxPureSignalAndLifecycleGates()
    {
        var plan = DspBenchmarkPlanCatalog.Build();

        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal("G2", plan.FirstHardwareTarget);
        Assert.Contains("off-baseline", plan.RequiredComparisons);
        Assert.Contains("thetis-parity", plan.RequiredComparisons);
        Assert.Contains(plan.GlobalAcceptanceGates, gate => gate.Contains("No weak-signal loss", StringComparison.Ordinal));
        Assert.Contains(plan.GlobalAcceptanceGates, gate => gate.Contains("PureSignal", StringComparison.Ordinal));

        var ids = plan.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("weak-cw-carrier", ids);
        Assert.Contains("ssb-like-speech", ids);
        Assert.Contains("agc-level-step", ids);
        Assert.Contains("tx-two-tone", ids);
        Assert.Contains("tx-puresignal-safe-bypass", ids);
        Assert.Contains("wdsp-channel-lifecycle", ids);

        var pureSignal = Assert.Single(plan.Scenarios, s => s.Id == "tx-puresignal-safe-bypass");
        Assert.Equal("hardware-capture-required", pureSignal.FixtureStatus);
        Assert.Contains(pureSignal.AcceptanceGates, gate => gate.Contains("PureSignal default", StringComparison.Ordinal));

        var lifecycle = Assert.Single(plan.Scenarios, s => s.Id == "wdsp-channel-lifecycle");
        Assert.Contains("native exception count", lifecycle.RequiredMetrics);
    }

    [Fact]
    public void CaptureManifest_BlocksWhenLiveDiagnosticsNeedPreflight()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        var condition = service.SmartNrCondition(Runtime("Off", "Off"));
        var live = DspLiveDiagnosticsService.Build(condition);

        var manifest = DspBenchmarkCaptureManifestService.Build(live, DspBenchmarkPlanCatalog.Build());

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.StartsWith("dsp-capture-", manifest.ManifestId, StringComparison.Ordinal);
        Assert.Equal("blocked-frontend-scene-missing", manifest.Status);
        Assert.False(manifest.ReadyForCapture);
        Assert.Equal("/api/dsp/live-diagnostics", manifest.LiveDiagnosticsEndpoint);
        Assert.Equal("/api/dsp/benchmark-plan", manifest.BenchmarkPlanEndpoint);
        Assert.Contains("frontend-scene-freshness", manifest.ScenarioIds);
        Assert.Contains("wdsp-channel-lifecycle", manifest.ScenarioIds);
        Assert.Contains("capture-preflight-not-ready", manifest.Constraints);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "live-diagnostics-json" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "offline-fixture-metrics" && artifact.Required);
        Assert.Contains(manifest.StopConditions, item => item.Contains("weak-signal loss", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureManifest_ReadyNr5ListsG2EvidenceArtifacts()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR5", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Nr5", nr5Available: true, nr5: Nr5(learnedFrames: 80, confidence: 0.72, agcGate: 0.66)),
            RxChain(score: 94));
        var live = DspLiveDiagnosticsService.Build(condition);

        var manifest = DspBenchmarkCaptureManifestService.Build(live, DspBenchmarkPlanCatalog.Build());

        Assert.Equal("ready-for-g2-capture", manifest.Status);
        Assert.True(manifest.ReadyForCapture);
        Assert.Equal("G2", manifest.HardwareTarget);
        Assert.Contains("weak-cw-carrier", manifest.ScenarioIds);
        Assert.Contains("agc-level-step", manifest.ScenarioIds);
        Assert.Contains("wdsp-channel-lifecycle", manifest.ScenarioIds);
        Assert.Contains("thetis-parity", manifest.RequiredComparisons);
        Assert.Contains(manifest.GlobalAcceptanceGates, gate => gate.Contains("No weak-signal loss", StringComparison.Ordinal));
        Assert.Contains(manifest.PreflightChecks, item => item.Contains("G2", StringComparison.Ordinal));
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Source == "/api/radio/diagnostics/dsp-scene");
        Assert.Contains(manifest.OperatorNotes, item => item.Contains("Cross-radio validation", StringComparison.Ordinal));
    }

    [Fact]
    public void ModernizationSnapshot_BundlesBlockedEvidenceAndMissingInputs()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        var condition = service.SmartNrCondition(Runtime("Off", "Off"));
        var live = DspLiveDiagnosticsService.Build(condition);
        var plan = DspBenchmarkPlanCatalog.Build();
        var manifest = DspBenchmarkCaptureManifestService.Build(live, plan);

        var snapshot = DspModernizationEvidenceSnapshotService.Build(
            condition,
            live,
            plan,
            manifest,
            DspExternalEngineCandidateCatalog.All());

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.StartsWith("dsp-modernization-", snapshot.SnapshotId, StringComparison.Ordinal);
        Assert.False(snapshot.ReadyForCapture);
        Assert.True(snapshot.EvidenceCompletenessScore < 100);
        Assert.Contains("/api/dsp/modernization-snapshot", snapshot.IncludedEndpoints);
        Assert.Contains("live-diagnostics-json", snapshot.IncludedArtifacts);
        Assert.Contains("frontend-dsp-scene", snapshot.MissingEvidence);
        Assert.Same(condition, snapshot.SmartNrCondition);
        Assert.Same(live, snapshot.LiveDiagnostics);
        Assert.Same(plan, snapshot.BenchmarkPlan);
        Assert.Same(manifest, snapshot.CaptureManifest);
        Assert.Contains(snapshot.NextActions, action => action.Contains("Resolve missing evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void ModernizationSnapshot_ReadyNr5IsSingleCaptureBundle()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR5", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Nr5", nr5Available: true, nr5: Nr5(learnedFrames: 80, confidence: 0.72, agcGate: 0.66)),
            RxChain(score: 94));
        var live = DspLiveDiagnosticsService.Build(condition);
        var plan = DspBenchmarkPlanCatalog.Build();
        var manifest = DspBenchmarkCaptureManifestService.Build(live, plan);

        var snapshot = DspModernizationEvidenceSnapshotService.Build(
            condition,
            live,
            plan,
            manifest,
            DspExternalEngineCandidateCatalog.All());

        Assert.Equal("ready-for-g2-evidence-capture", snapshot.Status);
        Assert.True(snapshot.ReadyForLiveBenchmark);
        Assert.True(snapshot.ReadyForCapture);
        Assert.True(snapshot.EvidenceCompletenessScore >= 90);
        Assert.Empty(snapshot.MissingEvidence);
        Assert.Contains("offline-fixture-metrics", snapshot.IncludedArtifacts);
        Assert.Contains(snapshot.NextActions, action => action.Contains("Save this modernization snapshot", StringComparison.Ordinal));
        Assert.Contains(snapshot.ExternalEngineCandidates, candidate => candidate.Id == "rnnoise");
    }

    [Fact]
    public void ExternalCandidateCatalog_TracksOptInPostDemodBakeoffGates()
    {
        var candidates = DspExternalEngineCandidateCatalog.All();

        Assert.Equal(new[] { "rnnoise", "deepfilternet", "speexdsp", "webrtc-apm" }, candidates.Select(c => c.Id).ToArray());
        Assert.All(candidates, c =>
        {
            Assert.Equal(1, c.SchemaVersion);
            Assert.Equal("off", c.DefaultState);
            Assert.Equal("candidate-only-opt-in-bakeoff", c.RolloutPolicy);
            Assert.Contains("post-demod", c.IntegrationPoint);
            Assert.NotEmpty(c.RequiredBenchmarks);
            Assert.NotEmpty(c.RequiredEvidence);
            Assert.NotEmpty(c.Blockers);
            Assert.NotEmpty(c.ReferenceUrls);
        });

        var webrtc = Assert.Single(candidates, c => c.Id == "webrtc-apm");
        Assert.Contains("AGC", webrtc.RadioSafetyRisk);
        Assert.Contains("AGC", string.Join(" ", webrtc.RequiredEvidence));

        var speex = Assert.Single(candidates, c => c.Id == "speexdsp");
        Assert.Contains("baseline", speex.IntegrationPoint);
        Assert.Contains("no pumping", string.Join(" ", speex.RequiredEvidence));
    }

    private static void PublishScene(
        FrontendDspSceneDiagnosticsService service,
        string profile,
        bool held,
        int rxScore,
        string rxTone,
        bool coherent) =>
        service.Update(new FrontendDspSceneDiagnosticsRequest(
            SourceClientId: "live-test",
            Mode: "USB",
            SignalProfile: "dx",
            SignalReason: "coherent weak-signal ridge",
            SmartNrProfile: profile,
            SmartNrReason: "weak-signal modernization test",
            SmartNrRecommendation: "preserve coherent ridge",
            SmartNrHeldByRxChain: held,
            SmartNrRxChainLabel: held ? "ADC headroom limited" : "RX chain optimized",
            SmartNrRxChainRecommendation: held ? "Add attenuation before raising NR" : "Hold front-end settings",
            SmartNrRxChainTone: rxTone,
            SmartNrRxChainScore: rxScore,
            MaxSnrDb: 8.4,
            CoherentMaxSnrDb: 7.9,
            OccupiedPct: 2.1,
            CoherentOccupiedPct: 1.4,
            ImpulsivePct: 0.2,
            PeakCount: 1,
            CoherentPeakCount: 1,
            CoherentSubthresholdSignal: coherent,
            SourceAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(-250)));

    private static DspNrRuntimeSnapshot Runtime(
        string requested,
        string effective,
        bool wdspActive = true,
        bool nativeLoadable = true,
        bool nr4Available = true,
        bool nr5Available = true,
        Nr5SpnrDiagnosticsDto? nr5 = null) =>
        new(
            WdspActive: wdspActive,
            WdspNativeLoadable: nativeLoadable,
            WdspEmnrPost2Available: true,
            WdspNr4SbnrAvailable: nr4Available,
            WdspNr5SpnrAvailable: nr5Available,
            Nr4Readiness: nr4Available ? "available" : "missing-sbnr-exports",
            Nr5Readiness: nr5Available ? "available" : "missing-spnr-exports",
            RequestedNrMode: requested,
            EffectiveNrMode: effective,
            Nr5SpnrDiagnostics: nr5);

    private static SmartNrRxChainRuntimeDto RxChain(int score, bool adcOverload = false) =>
        new(
            SchemaVersion: 1,
            Source: "test",
            AutoAgcEnabled: true,
            AgcMode: "Med",
            AgcTopDb: 80,
            AgcOffsetDb: -4,
            EffectiveAgcTopDb: 76,
            AutoAttEnabled: true,
            AdcProtectionEnabled: true,
            AttenDb: 3,
            AttOffsetDb: adcOverload ? 6 : 0,
            EffectiveAttenDb: adcOverload ? 9 : 3,
            AdcOverloadWarning: adcOverload,
            AdcOverloadLevel: adcOverload ? 4 : 0,
            LastOverloadBits: adcOverload ? (byte)0x03 : (byte)0,
            Adc0MaxMagnitude: adcOverload ? (ushort)52_000 : (ushort)31_000,
            Adc1MaxMagnitude: null,
            Adc0MaxMagnitudeAtOverload: adcOverload ? (ushort)52_000 : (ushort)0,
            Adc1MaxMagnitudeAtOverload: 0,
            LastAdcTelemetryUtc: DateTimeOffset.UtcNow,
            SquelchEnabled: false,
            SquelchAdaptive: true,
            SquelchLevel: 0,
            PreampOn: false);

    private static Nr5SpnrDiagnosticsDto Nr5(int learnedFrames, double confidence, double agcGate) =>
        new(
            SchemaVersion: 3,
            ChannelId: 0,
            Run: true,
            Position: 1,
            LearnedFrames: learnedFrames,
            Aggressiveness: 0.62,
            AgcRun: true,
            TargetRms: 0.075,
            MaxGain: 12.0,
            AgcGain: 1.7,
            AgcGainDb: 4.6,
            PresencePeak: 0.8,
            SaliencePeak: 0.7,
            CoherencePeak: 0.65,
            RidgePeak: 0.61,
            MeanGain: 0.58,
            MinGain: 0.18,
            SuppressionDb: 9.1,
            NoiseFloorDb: -58.0,
            FloorReductionDb: 7.3,
            DynamicRangeDb: 18.4,
            SignalConfidence: confidence,
            AgcGate: agcGate,
            InputRms: 0.031,
            InputDbfs: -30.2,
            OutputRms: 0.068,
            OutputDbfs: -23.4);
}
