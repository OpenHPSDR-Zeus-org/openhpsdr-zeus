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

        var diag = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence());

        Assert.Equal(1, diag.SchemaVersion);
        Assert.Equal("frontend-scene-missing", diag.Status);
        Assert.Equal("standby", diag.QualityTone);
        Assert.False(diag.ReadyForLiveBenchmark);
        Assert.False(diag.ReadyForNr5Tuning);
        Assert.Equal("nr5-not-active", diag.Nr5TuningStatus);
        Assert.Contains("frontend-dsp-scene-missing", diag.Constraints);
        Assert.Contains("frontend-dsp-scene-publisher", diag.CandidateTools);
        Assert.Contains("offline-dsp-benchmark-harness", diag.CandidateTools);
        Assert.Contains("dsp-benchmark-acceptance-plan", diag.CandidateTools);
        Assert.Contains("dsp-benchmark-metric-catalog", diag.CandidateTools);
        Assert.Contains("g2-rx-peak-hunt", diag.CandidateTools);
        Assert.Empty(diag.FrontendTopPeaks);
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

        var diag = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence());

        Assert.Equal("ready-for-live-benchmark", diag.Status);
        Assert.Equal("ready", diag.QualityTone);
        Assert.True(diag.ReadinessScore >= 85);
        Assert.True(diag.ReadyForLiveBenchmark);
        Assert.True(diag.ReadyForNr5Tuning);
        Assert.Equal("ready-for-nr5-live-tuning", diag.Nr5TuningStatus);
        Assert.Empty(diag.Nr5TuningConstraints);
        Assert.True(diag.RuntimeAligned);
        Assert.Equal("Nr5", diag.EffectiveNrMode);
        Assert.Equal(0.72, diag.Nr5SignalConfidence);
        Assert.Equal(0.66, diag.Nr5SignalProbability);
        Assert.Equal(0.12, diag.Nr5TextureFill);
        Assert.Equal(0.18, diag.Nr5MaskSmoothing);
        Assert.Equal(0.41, diag.Nr5WeakSignalMemory);
        Assert.True(diag.FrontendAdjacentNoiseUsable);
        Assert.Equal(72, diag.FrontendAdjacentNoiseBins);
        Assert.Equal(-111.4, diag.FrontendAdjacentNoiseFloorDb);
        Assert.Equal(5.3, diag.FrontendAdjacentNoiseRejectedPct);
        var peak = Assert.Single(diag.FrontendTopPeaks);
        Assert.Equal(14_268_750, peak.FrequencyHz);
        Assert.Equal(1_750, peak.OffsetHz);
        Assert.Equal(21.4, peak.SnrDb);
        Assert.Equal(-82.5, peak.Dbfs);
        Assert.Equal(0.812, peak.Confidence);
        Assert.True(peak.Coherent);
        Assert.Contains("adjacent-noise-profile-usable", diag.Evidence);
        Assert.Contains("nr5-adjacent-noise-trust-0.680", diag.Evidence);
        Assert.Contains("nr5-adjacent-noise-side-balance-0.895", diag.Evidence);
        Assert.Contains("nr5-adjacent-noise-asymmetry-1.4db", diag.Evidence);
        Assert.Contains("nr5-adjacent-noise-drive-0.210", diag.Evidence);
        Assert.Contains("nr5-adjacent-noise-profile", diag.CandidateTools);
        Assert.Contains("nr5-spnr-diagnostics", diag.CandidateTools);
        Assert.Contains("weak-cw-carrier", diag.NextBenchmarkScenarios);
        Assert.Contains("agc-level-step", diag.NextBenchmarkScenarios);
        Assert.Contains(diag.BenchmarkAcceptanceGates, gate => gate.Contains("No weak-signal loss", StringComparison.Ordinal));
        Assert.Contains("external-post-demod-bakeoff:deepfilternet", diag.CandidateTools);
        var deepFilter = Assert.Single(diag.ExternalEngineCandidates, c => c.Id == "deepfilternet");
        Assert.Contains("ssb-like-speech", deepFilter.RequiredBenchmarks);
        Assert.Contains("Model artifact", string.Join(" ", deepFilter.Blockers));
        Assert.Contains("ready-for-g2-live-benchmark", diag.Evidence);
        Assert.Contains("ready-for-nr5-live-tuning", diag.Evidence);
        Assert.Contains("nr5-live-tuning-watch", diag.CandidateTools);
        Assert.Contains("g2-rx-peak-hunt", diag.CandidateTools);
        Assert.Equal("opt-in-only-until-benchmark-and-g2-on-air-acceptance", diag.RolloutGate);
    }

    [Fact]
    public void Build_RuntimeOnlyAlignedNr5SceneDoesNotEmitProfileUnmappedConstraint()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: null, held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Nr5", nr5Available: true, nr5: Nr5(learnedFrames: 80, confidence: 0.72, agcGate: 0.66)),
            RxChain(score: 94));

        var diag = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence());

        Assert.Equal("ready-for-live-benchmark", diag.Status);
        Assert.True(diag.ReadyForLiveBenchmark);
        Assert.True(diag.ReadyForNr5Tuning);
        Assert.True(diag.RuntimeAligned);
        Assert.Equal("runtime-only-aligned", diag.RuntimeAlignmentStatus);
        Assert.Equal("Nr5", diag.ExpectedNrMode);
        Assert.Contains("smart-nr-runtime-aligned", diag.Evidence);
        Assert.DoesNotContain("smart-nr-profile-unmapped", diag.Constraints);
    }

    [Fact]
    public void Build_CarriesRadioStateForStableLiveTraceEvidence()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR5", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Nr5", nr5Available: true, nr5: Nr5(learnedFrames: 80, confidence: 0.72, agcGate: 0.66)),
            RxChain(score: 94));
        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.25:1024",
            VfoHz: 14_260_000,
            Mode: RxMode.USB,
            FilterLowHz: 100,
            FilterHighHz: 3100,
            SampleRate: 384_000,
            RadioLoHz: 14_250_000,
            CtunEnabled: true);

        var diag = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence(), state);

        Assert.Equal(14_260_000, diag.RadioVfoHz);
        Assert.Equal(14_250_000, diag.RadioLoHz);
        Assert.Equal("USB", diag.RadioMode);
        Assert.True(diag.RadioCtunEnabled);
        Assert.Equal(384_000, diag.RadioSampleRate);
    }

    [Fact]
    public void Build_MissingFrontendSceneCanStillBeReadyForNr5LiveTuning()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Nr5", nr5Available: true, nr5: Nr5(learnedFrames: 80, confidence: 0.72, agcGate: 0.66)),
            RxChain(score: 94));

        var diag = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence());

        Assert.Equal("frontend-scene-missing", diag.Status);
        Assert.False(diag.ReadyForLiveBenchmark);
        Assert.True(diag.ReadyForNr5Tuning);
        Assert.Equal("ready-for-nr5-live-tuning", diag.Nr5TuningStatus);
        Assert.Empty(diag.Nr5TuningConstraints);
        Assert.Contains("frontend-dsp-scene-missing", diag.Constraints);
        Assert.Contains("ready-for-nr5-live-tuning", diag.Evidence);
    }

    [Fact]
    public void Build_RuntimeEvidenceBlocksBenchmarkWhenFinalAudioClips()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR5", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Nr5", nr5Available: true, nr5: Nr5(learnedFrames: 80, confidence: 0.72, agcGate: 0.66)),
            RxChain(score: 94));

        var diag = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence(
            status: "audio-clipping-risk",
            audioStatus: "clipping-risk",
            audioPeakDbfs: -0.1,
            adcHeadroomDb: 12.0));

        Assert.Equal("final-audio-clipping-risk", diag.Status);
        Assert.Equal("protect", diag.QualityTone);
        Assert.False(diag.ReadyForLiveBenchmark);
        Assert.False(diag.ReadyForNr5Tuning);
        Assert.Contains("final-audio-clipping-risk", diag.Nr5TuningConstraints);
        Assert.Contains("final-audio-clipping-risk", diag.Constraints);
        Assert.Contains("Reduce RX leveler boost", string.Join(" ", diag.RecommendedActions));
        Assert.NotNull(diag.RuntimeEvidence);
        Assert.Equal(-0.1, diag.RuntimeEvidence.AudioPeakDbfs);
        Assert.Equal(42, diag.RuntimeEvidence.AudioFramesBroadcast);
        Assert.Equal(1664, diag.RuntimeEvidence.AudioSampleCount);
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
        Assert.Contains("nr5-spnr", plan.RequiredComparisons);
        Assert.DoesNotContain("candidate-external-engine-opt-in", plan.RequiredComparisons);
        Assert.Contains(plan.GlobalAcceptanceGates, gate => gate.Contains("No weak-signal loss", StringComparison.Ordinal));
        Assert.Contains(plan.GlobalAcceptanceGates, gate => gate.Contains("PureSignal", StringComparison.Ordinal));

        var ids = plan.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("weak-cw-carrier", ids);
        Assert.Contains("ssb-like-speech", ids);
        Assert.Contains("agc-level-step", ids);
        Assert.Contains("tx-two-tone", ids);
        Assert.Contains("tx-puresignal-safe-bypass", ids);
        Assert.Contains("wdsp-channel-lifecycle", ids);

        var weakCarrier = Assert.Single(plan.Scenarios, s => s.Id == "weak-cw-carrier");
        Assert.Contains("signal SINAD", weakCarrier.RequiredMetrics);
        Assert.Contains("processing elapsed ms", weakCarrier.RequiredMetrics);
        Assert.Contains("throughput ratio", weakCarrier.RequiredMetrics);

        var pureSignal = Assert.Single(plan.Scenarios, s => s.Id == "tx-puresignal-safe-bypass");
        Assert.Equal("hardware-capture-required", pureSignal.FixtureStatus);
        Assert.Contains(pureSignal.AcceptanceGates, gate => gate.Contains("PureSignal default", StringComparison.Ordinal));

        var txTwoTone = Assert.Single(plan.Scenarios, s => s.Id == "tx-two-tone");
        Assert.Contains("TX leveler gain reduction", txTwoTone.RequiredMetrics);
        Assert.Contains("TX CFC gain reduction", txTwoTone.RequiredMetrics);
        Assert.Contains("TX ALC gain reduction", txTwoTone.RequiredMetrics);
        Assert.Contains("TX output peak", txTwoTone.RequiredMetrics);
        Assert.Contains("processing elapsed ms", txTwoTone.RequiredMetrics);
        Assert.Contains("throughput ratio", txTwoTone.RequiredMetrics);

        var txVoiceLike = Assert.Single(plan.Scenarios, s => s.Id == "tx-voice-like");
        Assert.Contains("TX compressor peak", txVoiceLike.RequiredMetrics);
        Assert.Contains("TX output average", txVoiceLike.RequiredMetrics);

        var lifecycle = Assert.Single(plan.Scenarios, s => s.Id == "wdsp-channel-lifecycle");
        Assert.Contains("native exception count", lifecycle.RequiredMetrics);
    }

    [Fact]
    public void BenchmarkPlanCatalog_CoversRequiredAcceptanceScenarioFamilies()
    {
        var plan = DspBenchmarkPlanCatalog.Build();
        var scenarios = plan.Scenarios;
        (string Family, string[] AcceptedIds)[] requiredFamilies =
        [
            ("weak-cw-carrier", ["weak-cw-carrier", "weak-carrier", "weak-cw", "weak-signal-carrier", "cw-carrier"]),
            ("ssb-like-speech", ["ssb-like-speech", "ssb-speech", "voice-like-speech", "speech-post-demod"]),
            ("fading", ["fading-carrier", "fading", "qsb", "fading-weak-signal", "weak-fading-carrier"]),
            ("impulse-noise", ["impulse-noise", "impulse-noise-burst", "periodic-impulse-noise", "nb-impulse-noise"]),
            ("strong-adjacent", ["strong-adjacent", "adjacent-strong-signal", "adjacent-signal", "strong-adjacent-signal"]),
            ("noise-only-gating", ["noise-only-gating", "noise-only", "squelch-noise-only", "false-open-noise"]),
            ("agc-pumping", ["agc-level-step", "agc-pumping", "agc-pump", "agc-step", "level-step"]),
            ("squelch-transition", ["squelch-transition", "squelch-open-close", "squelch-threshold-transition", "ssql-transition"]),
            ("tx-two-tone", ["tx-two-tone", "two-tone-tx", "tx-linearity-two-tone"]),
            ("tx-voice-like", ["tx-voice-like", "tx-voice", "tx-speech", "tx-ssb-voice"]),
            ("puresignal-safe-bypass", ["puresignal-safe-bypass", "puresignal-bypass", "pure-signal-safe-bypass", "pure-signal-bypass", "tx-puresignal-safe-bypass"]),
            ("channel-lifecycle", ["channel-lifecycle", "openchannel-setchannelstate-lifecycle", "open-channel-set-channel-state", "open-channel-set-channel-state-lifecycle", "wdsp-channel-lifecycle"]),
        ];

        foreach (var (family, acceptedIds) in requiredFamilies)
        {
            Assert.True(
                scenarios.Any(scenario => acceptedIds.Contains(scenario.Id, StringComparer.Ordinal)),
                $"Missing required benchmark scenario family: {family}");
        }

        foreach (var scenario in scenarios)
        {
            Assert.NotEmpty(scenario.RequiredComparisons);
            Assert.NotEmpty(scenario.RequiredMetrics);
            Assert.NotEmpty(scenario.AcceptanceGates);
        }
    }

    [Fact]
    public void BenchmarkPlanCatalog_RequiresNr5ComparisonForRxAcceptanceScenariosOnly()
    {
        var plan = DspBenchmarkPlanCatalog.Build();

        string[] rxScenarioIds =
        [
            "weak-cw-carrier",
            "ssb-like-speech",
            "fading-carrier",
            "impulse-noise",
            "strong-adjacent",
            "noise-only-gating",
            "agc-level-step",
            "squelch-transition",
        ];

        foreach (var scenarioId in rxScenarioIds)
        {
            var scenario = Assert.Single(plan.Scenarios, s => s.Id == scenarioId);
            Assert.Contains("nr5-spnr", scenario.RequiredComparisons);
        }

        string[] nonRxNrScenarioIds =
        [
            "frontend-scene-freshness",
            "tx-two-tone",
            "tx-voice-like",
            "tx-puresignal-safe-bypass",
            "wdsp-channel-lifecycle",
        ];

        foreach (var scenarioId in nonRxNrScenarioIds)
        {
            var scenario = Assert.Single(plan.Scenarios, s => s.Id == scenarioId);
            Assert.DoesNotContain("nr5-spnr", scenario.RequiredComparisons);
        }
    }

    [Fact]
    public void BenchmarkMetricCatalog_CoversEveryRequiredPlanMetric()
    {
        var plan = DspBenchmarkPlanCatalog.Build();
        var catalog = DspBenchmarkPlanCatalog.BuildMetricCatalog();

        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Contains("higher", catalog.DirectionValues);
        Assert.Contains("lower", catalog.DirectionValues);
        Assert.Contains("informational", catalog.DirectionValues);
        Assert.Contains("no-regression", catalog.ComparatorValues);
        Assert.Contains("at-or-below", catalog.ComparatorValues);

        var catalogById = catalog.Metrics.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var requiredMetricIds = plan.Scenarios
            .SelectMany(s => s.RequiredMetrics)
            .Select(NormalizeMetricId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var metricId in requiredMetricIds)
        {
            Assert.True(catalogById.TryGetValue(metricId, out var metric), $"Missing benchmark metric catalog entry: {metricId}");
            Assert.False(string.IsNullOrWhiteSpace(metric.AcceptanceThreshold), $"Missing acceptance threshold: {metricId}");
            Assert.False(string.IsNullOrWhiteSpace(metric.AcceptanceComparator), $"Missing acceptance comparator: {metricId}");
            Assert.False(string.IsNullOrWhiteSpace(metric.Unit), $"Missing unit: {metricId}");
            Assert.False(string.IsNullOrWhiteSpace(metric.SafetyClass), $"Missing safety class: {metricId}");
            Assert.NotEmpty(metric.AcceptanceScopes);
        }

        Assert.Equal("higher", catalogById["wantedsnr"].Direction);
        Assert.Equal("no-regression", catalogById["wantedsnr"].AcceptanceComparator);
        Assert.Equal("higher", catalogById["signalsinad"].Direction);
        Assert.Equal("no-regression", catalogById["signalsinad"].AcceptanceComparator);
        Assert.Equal("lower", catalogById["latency"].Direction);
        Assert.Equal("lower", catalogById["processingelapsedms"].Direction);
        Assert.Equal("higher", catalogById["throughputratio"].Direction);
        Assert.Equal("lower", catalogById["agcgainmovement"].Direction);
        Assert.Equal("informational", catalogById["outputrms"].Direction);
        Assert.Equal("informational", catalogById["outputrms"].AcceptanceComparator);
        Assert.Equal("0", catalogById["clippingcount"].AcceptanceThreshold);
        Assert.Equal("lower", catalogById["txlevelergainreduction"].Direction);
        Assert.Equal("lower", catalogById["txalcgainreduction"].Direction);
        Assert.Equal("lower", catalogById["txoutputpeak"].Direction);
        Assert.Equal("informational", catalogById["txcfcgainreduction"].Direction);
        Assert.Equal("informational", catalogById["txcompressorpeak"].Direction);
        Assert.Contains("tx-two-tone", catalogById["txlevelergainreduction"].AcceptanceScopes);
        Assert.Contains("tx-voice-like", catalogById["txcompressorpeak"].AcceptanceScopes);
        Assert.Equal("at-or-below", catalogById["clippingcount"].AcceptanceComparator);
        Assert.Equal("lower", catalogById["failedsamplecount"].Direction);
        Assert.Equal("0.0", catalogById["failedsamplecount"].AcceptanceThreshold);
        Assert.Equal("no-regression", catalogById["failedsamplecount"].AcceptanceComparator);
        Assert.Equal("hard-gate", catalogById["failedsamplecount"].SafetyClass);
        Assert.Contains("live-diagnostics-trace-comparison", catalogById["failedsamplecount"].AcceptanceScopes);
        Assert.Equal("higher", catalogById["readysamplepct"].Direction);
        Assert.Equal("1.0", catalogById["readysamplepct"].AcceptanceThreshold);
        Assert.Equal("readiness", catalogById["readysamplepct"].SafetyClass);
        Assert.Equal("lower", catalogById["nr5outputmovementdb"].Direction);
        Assert.Equal("1.0", catalogById["nr5outputmovementdb"].AcceptanceThreshold);
        Assert.Equal("pumping", catalogById["nr5outputmovementdb"].SafetyClass);
        Assert.Equal("lower", catalogById["nr5artifactriskscore"].Direction);
        Assert.Equal("0.0", catalogById["nr5artifactriskscore"].AcceptanceThreshold);
        Assert.Equal("artifact-control", catalogById["nr5artifactriskscore"].SafetyClass);
        Assert.Contains("live-diagnostics-trace-comparison", catalogById["nr5artifactriskscore"].AcceptanceScopes);
        Assert.Contains("weak-cw-carrier", catalogById["wantedsnr"].RelatedScenarios);
        Assert.Contains("weak-cw-carrier", catalogById["signalsinad"].RelatedScenarios);
        Assert.Contains("tx-two-tone", catalogById["processingelapsedms"].AcceptanceScopes);
        Assert.Contains("weak-cw-carrier", catalogById["throughputratio"].AcceptanceScopes);
        Assert.Contains("agc-level-step", catalogById["agcgainmovement"].RelatedScenarios);
        Assert.Contains("weak-cw-carrier", catalogById["wantedsnr"].AcceptanceScopes);
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
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "wdsp-native-symbol-audit" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "wdsp-runtime-artifact-audit" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "offline-fixture-metrics" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "native-stage-timing-report" && artifact.Required);
        Assert.DoesNotContain(manifest.RequiredArtifacts, artifact => artifact.Id == "external-engine-bakeoff-report");
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
        var traceArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "live-diagnostics-trace");
        Assert.False(traceArtifact.Required);
        Assert.Equal("diagnostics-jsonl", traceArtifact.Kind);
        Assert.Contains("watch-dsp-live-diagnostics.ps1", traceArtifact.Source);
        var traceComparisonArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "live-diagnostics-trace-comparison");
        Assert.False(traceComparisonArtifact.Required);
        Assert.Equal("diagnostics-comparison-json", traceComparisonArtifact.Kind);
        Assert.Contains("compare-dsp-live-diagnostics-traces.ps1", traceComparisonArtifact.Source);
        var traceIndexArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "live-diagnostics-trace-index");
        Assert.False(traceIndexArtifact.Required);
        Assert.Equal("trace", traceIndexArtifact.Kind);
        Assert.Contains("run-dsp-live-diagnostics-matrix.ps1", traceIndexArtifact.Source);
        var traceHistoryArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "live-diagnostics-history");
        Assert.False(traceHistoryArtifact.Required);
        Assert.Equal("diagnostics-history-json", traceHistoryArtifact.Kind);
        Assert.Contains("summarize-dsp-live-diagnostics-history.ps1", traceHistoryArtifact.Source);
        var pureSignalReportArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "puresignal-safe-bypass-report");
        Assert.True(pureSignalReportArtifact.Required);
        Assert.Equal("puresignal-safe-bypass-report-json", pureSignalReportArtifact.Kind);
        Assert.Contains("summarize-dsp-puresignal-bench.ps1", pureSignalReportArtifact.Source);
        Assert.Equal(["tx-puresignal-safe-bypass"], pureSignalReportArtifact.ScenarioIds);
        Assert.DoesNotContain(manifest.RequiredArtifacts, artifact => artifact.Id == "external-engine-bakeoff-report");
        var nativeAuditArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "wdsp-native-symbol-audit");
        Assert.True(nativeAuditArtifact.Required);
        Assert.Equal("native-audit-json", nativeAuditArtifact.Kind);
        Assert.Contains("audit-wdsp-native-symbols.ps1", nativeAuditArtifact.Source);
        var runtimeAuditArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "wdsp-runtime-artifact-audit");
        Assert.True(runtimeAuditArtifact.Required);
        Assert.Equal("runtime-audit-json", runtimeAuditArtifact.Kind);
        Assert.Contains("audit-wdsp-runtime-artifacts.ps1", runtimeAuditArtifact.Source);
        var nativeStageTimingArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "native-stage-timing-report");
        Assert.True(nativeStageTimingArtifact.Required);
        Assert.Equal("native-stage-timing-report-json", nativeStageTimingArtifact.Kind);
        Assert.Contains("summarize-dsp-native-stage-timing.ps1", nativeStageTimingArtifact.Source);
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
        Assert.Contains("/api/dsp/benchmark-metric-catalog", snapshot.IncludedEndpoints);
        Assert.Contains("live-diagnostics-json", snapshot.IncludedArtifacts);
        Assert.Contains("wdsp-native-symbol-audit", snapshot.IncludedArtifacts);
        Assert.Contains("wdsp-runtime-artifact-audit", snapshot.IncludedArtifacts);
        Assert.DoesNotContain("external-engine-bakeoff-report", snapshot.IncludedArtifacts);
        Assert.DoesNotContain("live-diagnostics-history", snapshot.IncludedArtifacts);
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
        Assert.Contains("wdsp-native-symbol-audit", snapshot.IncludedArtifacts);
        Assert.Contains("wdsp-runtime-artifact-audit", snapshot.IncludedArtifacts);
        Assert.DoesNotContain("external-engine-bakeoff-report", snapshot.IncludedArtifacts);
        Assert.DoesNotContain("live-diagnostics-history", snapshot.IncludedArtifacts);
        Assert.Contains(snapshot.NextActions, action => action.Contains("Save this modernization snapshot", StringComparison.Ordinal));
        Assert.Contains(snapshot.ExternalEngineCandidates, candidate => candidate.Id == "rnnoise");
    }

    [Fact]
    public void CaptureManifest_RequiresExternalBakeoffOnlyWhenExternalComparisonIsScoped()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR5", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Nr5", "Nr5", nr5Available: true, nr5: Nr5(learnedFrames: 80, confidence: 0.72, agcGate: 0.66)),
            RxChain(score: 94));
        var live = DspLiveDiagnosticsService.Build(condition);
        var plan = DspBenchmarkPlanCatalog.Build() with
        {
            RequiredComparisons =
            [
                "off-baseline",
                "thetis-parity",
                "current-zeus",
                "nr5-spnr",
                "candidate-external-engine-opt-in",
            ],
        };

        var manifest = DspBenchmarkCaptureManifestService.Build(live, plan);

        var externalBakeoffArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "external-engine-bakeoff-report");
        Assert.True(externalBakeoffArtifact.Required);
        Assert.Equal("external-candidate-report-json", externalBakeoffArtifact.Kind);
        Assert.Contains("summarize-dsp-external-engine-candidates.ps1", externalBakeoffArtifact.Source);
        Assert.Contains("candidate readiness", externalBakeoffArtifact.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalCandidateCatalog_TracksOptInPostDemodBakeoffGates()
    {
        var candidates = DspExternalEngineCandidateCatalog.All();

        Assert.Equal(
            new[] { "rnnoise", "rmnoise", "dpdfnet", "deepfilternet", "clearervoice-studio", "speexdsp", "webrtc-apm" },
            candidates.Select(c => c.Id).ToArray());
        Assert.All(candidates, c =>
        {
            Assert.Equal(1, c.SchemaVersion);
            Assert.Equal("off", c.DefaultState);
            Assert.Equal("candidate-only-opt-in-bakeoff", c.RolloutPolicy);
            Assert.Contains("post-demod", c.IntegrationPoint);
            Assert.Equal("catalog-only-not-integrated", c.EvaluationStage);
            Assert.Contains(c.AllowedSignalPaths, path => path.Contains("post-demod", StringComparison.Ordinal));
            Assert.Contains("raw-wdsp-iq", c.ForbiddenSignalPaths);
            Assert.Contains(c.ForbiddenSignalPaths, path => path.Contains("puresignal", StringComparison.Ordinal));
            Assert.Contains("operator-visible-opt-in", c.RequiredControls);
            Assert.Contains("clean-bypass-fallback", c.RequiredControls);
            Assert.Contains("no-raw-wdsp-iq-replacement", c.RequiredControls);
            Assert.Contains("no-tx-or-puresignal-coupling", c.RequiredControls);
            Assert.True(
                c.FallbackPolicy.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                || c.FallbackPolicy.Contains("fall back", StringComparison.OrdinalIgnoreCase)
                || c.FallbackPolicy.Contains("bypass", StringComparison.OrdinalIgnoreCase),
                $"Missing fallback or bypass policy for {c.Id}");
            Assert.NotEmpty(c.RequiredBenchmarks);
            Assert.NotEmpty(c.RequiredEvidence);
            Assert.NotEmpty(c.Blockers);
            Assert.NotEmpty(c.ReferenceUrls);
        });

        var webrtc = Assert.Single(candidates, c => c.Id == "webrtc-apm");
        Assert.Contains("AGC", webrtc.RadioSafetyRisk);
        Assert.Contains("AGC", string.Join(" ", webrtc.RequiredEvidence));
        Assert.Contains("webrtc-aec-disabled", webrtc.RequiredControls);
        Assert.Contains("webrtc-agc-disabled", webrtc.RequiredControls);

        var speex = Assert.Single(candidates, c => c.Id == "speexdsp");
        Assert.Contains("baseline", speex.IntegrationPoint);
        Assert.Contains("no pumping", string.Join(" ", speex.RequiredEvidence));
        Assert.Contains("speex-agc-disabled", speex.RequiredControls);

        var rnnoise = Assert.Single(candidates, c => c.Id == "rnnoise");
        Assert.Contains("NR5-AI Assist", rnnoise.IntegrationPoint);
        Assert.Contains("nr5-ai-assist-mode", rnnoise.RequiredControls);
        Assert.Contains("official-xiph-runtime-only", rnnoise.RequiredControls);
        Assert.Contains("le9endary-training-reference-only", rnnoise.RequiredControls);
        Assert.Contains("werman-plugin-reference-only", rnnoise.RequiredControls);
        Assert.Contains("weak-ssb-volume-parity", rnnoise.RequiredBenchmarks);
        Assert.Contains("Xiph", rnnoise.License);
        Assert.Contains("le9endary/RNNoise has no repo license", rnnoise.License);
        Assert.Contains("werman/noise-suppression-for-voice is GPL-3.0", rnnoise.License);
        Assert.Contains("https://github.com/le9endary/RNNoise", rnnoise.ReferenceUrls);
        Assert.Contains("https://github.com/werman/noise-suppression-for-voice", rnnoise.ReferenceUrls);
        Assert.Contains(
            rnnoise.Blockers,
            blocker => blocker.Contains("do not vendor", StringComparison.OrdinalIgnoreCase));

        var rmnoise = Assert.Single(candidates, c => c.Id == "rmnoise");
        Assert.Contains("NR5-AI Assist", rmnoise.IntegrationPoint);
        Assert.Contains("recording-consent-gate", rmnoise.RequiredControls);
        Assert.Contains("service-availability-fallback", rmnoise.RequiredControls);
        Assert.Contains("no-live-cloud-stream-by-default", rmnoise.RequiredControls);
        Assert.Contains("service-unavailable-bypass", rmnoise.RequiredBenchmarks);
        Assert.Contains("service terms", rmnoise.License);
        Assert.Contains("https://ournetplace.com/rm-noise/", rmnoise.ReferenceUrls);
        Assert.Contains(
            rmnoise.Blockers,
            blocker => blocker.Contains("consent/privacy", StringComparison.OrdinalIgnoreCase));

        var dpdfnet = Assert.Single(candidates, c => c.Id == "dpdfnet");
        Assert.Contains("NR5-AI Assist", dpdfnet.IntegrationPoint);
        Assert.Contains("onnx-or-tflite-runtime-package-review", dpdfnet.RequiredControls);
        Assert.Contains("48khz-frame-adapter", dpdfnet.RequiredControls);
        Assert.Contains("weak-ssb-volume-parity", dpdfnet.RequiredBenchmarks);
        Assert.Contains("realtime-latency-g2", dpdfnet.RequiredBenchmarks);
        Assert.Contains("Apache-2.0", dpdfnet.License);
        Assert.Contains("https://github.com/ceva-ip/DPDFNet", dpdfnet.ReferenceUrls);

        var clearerVoice = Assert.Single(candidates, c => c.Id == "clearervoice-studio");
        Assert.Contains("offline", clearerVoice.IntegrationPoint);
        Assert.Contains("offline-only-until-runtime-approved", clearerVoice.RequiredControls);
        Assert.Contains("recording-consent-gate", clearerVoice.RequiredControls);
        Assert.Contains("offline-bypass", clearerVoice.RequiredBenchmarks);
        Assert.Contains("Apache-2.0", clearerVoice.License);
        Assert.Contains("https://github.com/modelscope/ClearerVoice-Studio", clearerVoice.ReferenceUrls);
    }

    private static void PublishScene(
        FrontendDspSceneDiagnosticsService service,
        string? profile,
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
            SourceAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(-250),
            TopPeaks:
            [
                new FrontendDspScenePeakDto(
                    FrequencyHz: 14_268_750,
                    OffsetHz: 1_750,
                    SnrDb: 21.36,
                    Dbfs: -82.47,
                    Confidence: 0.8123,
                    Coherent: true)
            ],
            AdjacentNoiseUsable: true,
            AdjacentNoiseBins: 72,
            AdjacentNoiseLeftBins: 34,
            AdjacentNoiseRightBins: 38,
            AdjacentNoiseFloorDb: -111.4,
            AdjacentNoiseP10Db: -113.2,
            AdjacentNoiseP50Db: -111.4,
            AdjacentNoiseP90Db: -108.7,
            AdjacentNoiseLeftFloorDb: -112.0,
            AdjacentNoiseRightFloorDb: -110.6,
            AdjacentNoiseSlopeDbPerKhz: 0.2,
            AdjacentNoiseRejectedPct: 5.3));

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
            SchemaVersion: 2,
            Source: "test",
            FilterLowHz: 300,
            FilterHighHz: 2600,
            FilterWidthHz: 2300,
            FilterPresetName: "NR5-WEAK",
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
            SchemaVersion: 9,
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
            SignalProbability: 0.66,
            TextureFill: 0.12,
            MaskSmoothing: 0.18,
            SignalConfidence: confidence,
            AgcGate: agcGate,
            LevelDrive: 0.82,
            RecoveryDrive: 0.64,
            WeakSignalMemory: 0.41,
            MakeupGain: 1.35,
            MakeupGainDb: 2.6,
            InputRms: 0.031,
            InputDbfs: -30.2,
            OutputRms: 0.068,
            OutputDbfs: -23.4,
            OutputPeak: 0.12,
            OutputPeakDbfs: -18.4,
            PeakEvidence: 0.72,
            PeakLimit: 0.69,
            PeakLimitDbfs: -3.2,
            PeakReductionDb: 1.4,
            AdjacentNoiseUsable: true,
            AdjacentNoiseBins: 72,
            AdjacentNoiseFloorDb: -111.4,
            AdjacentNoiseTrust: 0.68,
            AdjacentNoiseDrive: 0.21,
            AdjacentNoiseRejectedPct: 5.3,
            AdjacentNoiseLeftBins: 34,
            AdjacentNoiseRightBins: 38,
            AdjacentNoiseLeftFloorDb: -112.0,
            AdjacentNoiseRightFloorDb: -110.6,
            AdjacentNoiseSideBalance: 0.895,
            AdjacentNoiseAsymmetryDb: 1.4);

    private static DspLiveRuntimeEvidenceDto RuntimeEvidence(
        string status = "fresh",
        string audioStatus = "fresh",
        double? audioPeakDbfs = -12.0,
        double? adcHeadroomDb = 24.0) =>
        new(
            SchemaVersion: 4,
            GeneratedUtc: DateTimeOffset.UtcNow,
            Status: status,
            RxMetersFresh: true,
            RxMetersStale: false,
            RxMetersAgeMs: 20,
            RxDbm: -93.0,
            AdcHeadroomDb: adcHeadroomDb,
            AgcGainDb: 8.5,
            AudioFresh: true,
            AudioStale: false,
            AudioAgeMs: 12,
            AudioStatus: audioStatus,
            AudioSource: "rx",
            AudioFramesBroadcast: 42,
            AudioLastSeq: 42,
            AudioSampleRateHz: 48000,
            AudioSampleCount: 1664,
            AudioRmsDbfs: -28.5,
            AudioPeakDbfs: audioPeakDbfs,
            TxMonitorRequested: false,
            SquelchEnabled: false,
            SquelchOpen: true,
            SquelchTailActive: false,
            SquelchGateGain: 1.0,
            RxAudioLevelerInputRmsDbfs: -24.5,
            RxAudioLevelerOutputRmsDbfs: -18.5,
            RxAudioLevelerInputPeakDbfs: -11.2,
            RxAudioLevelerOutputPeakDbfs: audioPeakDbfs,
            RxAudioLevelerDesiredGainDb: 6.5,
            RxAudioLevelerAppliedGainDb: 6.0,
            RxAudioLevelerGainDeltaDb: 0.0,
            RxAudioLevelerPeakHeadroomDb: 9.0,
            RxAudioLevelerPreLimitPeakDbfs: -10.8,
            RxAudioLevelerOutputLimitReductionDb: 0.0,
            RxAudioLevelerOutputLimitSampleCount: 0,
            RxAudioLevelerPauseHoldBlocks: 0,
            RxAudioLevelerNr5SpeechHoldBlocks: 0,
            RxAudioLevelerBoostSlewLimited: false,
            RxAudioLevelerPeakLimited: false,
            RxAudioLevelerOutputLimited: false,
            MonitorBacklogSamples: 0,
            AudioSinkCount: 1,
            DiagnosticRecommendation: "test evidence");

    private static string NormalizeMetricId(string value) =>
        new(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
}
