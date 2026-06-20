// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

[Trait("Category", "DspModernization")]
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
        Assert.False(diag.ReadyForExternalEngineBakeoff);
        Assert.Equal("external-engine-bakeoff-preflight-required", diag.ExternalEngineBakeoffStatus);
        Assert.Contains("frontend-dsp-scene-missing", diag.ExternalEngineBakeoffConstraints);
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
    public void Build_FreshNrOffSceneIsReadyForExternalEngineBakeoff()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "Light", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Off", "Off"),
            RxChain(score: 94));

        var diag = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence());

        Assert.True(diag.ReadyForExternalEngineBakeoff);
        Assert.Equal("ready-for-external-engine-bakeoff", diag.ExternalEngineBakeoffStatus);
        Assert.Empty(diag.ExternalEngineBakeoffConstraints);
        Assert.Contains("ready-for-external-engine-bakeoff", diag.Evidence);
        Assert.Contains("external-engine-live-bakeoff-watch", diag.CandidateTools);
        Assert.Contains(diag.ExternalEngineCandidates, c => c.RequiredControls.Contains("rx-audio-suite-route"));
    }

    [Fact]
    public void Build_ExternalEngineBakeoffBlocksTxMonitorAndStaleAudio()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "Light", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Off", "Off"),
            RxChain(score: 94));

        var runtime = RuntimeEvidence(status: "audio-tx-monitor");
        runtime = runtime with
        {
            AudioFresh = false,
            AudioStale = true,
            TxMonitorRequested = true,
        };

        var diag = DspLiveDiagnosticsService.Build(condition, runtime);

        Assert.False(diag.ReadyForExternalEngineBakeoff);
        Assert.Equal("external-engine-bakeoff-preflight-required", diag.ExternalEngineBakeoffStatus);
        Assert.Contains("tx-monitor-audio-active", diag.ExternalEngineBakeoffConstraints);
        Assert.Contains("final-audio-not-fresh", diag.ExternalEngineBakeoffConstraints);
    }

    [Fact]
    public void Build_CarriesRadioStateForStableLiveTraceEvidence()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR4", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Sbnr", "Sbnr"),
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
    public void Build_RuntimeEvidenceBlocksBenchmarkWhenFinalAudioClips()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR4", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Sbnr", "Sbnr"),
            RxChain(score: 94));

        var diag = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence(
            status: "audio-clipping-risk",
            audioStatus: "clipping-risk",
            audioPeakDbfs: -0.1,
            adcHeadroomDb: 12.0));
        Assert.Equal("protect", diag.QualityTone);
        Assert.False(diag.ReadyForLiveBenchmark);
        Assert.Contains("final-audio-clipping-risk", diag.Constraints);
        Assert.Contains("Reduce RX leveler boost", string.Join(" ", diag.RecommendedActions));
        Assert.NotNull(diag.RuntimeEvidence);
        Assert.Equal(-0.1, diag.RuntimeEvidence.AudioPeakDbfs);
        Assert.Equal(42, diag.RuntimeEvidence.AudioFramesBroadcast);
        Assert.Equal(1664, diag.RuntimeEvidence.AudioSampleCount);
    }

    [Fact]
    public void Build_RuntimeEvidenceCarriesRxLevelerCandidateProfile()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR4", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Sbnr", "Sbnr"),
            RxChain(score: 94));

        var runtime = RuntimeEvidence(
            levelerRequestedProfile: "stable-speech-candidate",
            levelerActiveProfile: "stable-speech-candidate",
            levelerExperimental: true,
            levelerControlRmsValid: true,
            levelerControlRmsDbfs: -31.2,
            levelerControlRmsHangDb: 8.4);

        var diag = DspLiveDiagnosticsService.Build(condition, runtime);

        Assert.NotNull(diag.RuntimeEvidence);
        Assert.Equal(5, diag.RuntimeEvidence.SchemaVersion);
        Assert.Equal("stable-speech-candidate", diag.RuntimeEvidence.RxAudioLevelerRequestedProfile);
        Assert.Equal("stable-speech-candidate", diag.RuntimeEvidence.RxAudioLevelerActiveProfile);
        Assert.True(diag.RuntimeEvidence.RxAudioLevelerExperimental);
        Assert.True(diag.RuntimeEvidence.RxAudioLevelerControlRmsValid);
        Assert.Equal(-31.2, diag.RuntimeEvidence.RxAudioLevelerControlRmsDbfs);
        Assert.Equal(8.4, diag.RuntimeEvidence.RxAudioLevelerControlRmsHangDb);
        Assert.Contains("rx-audio-leveler-stable-speech-candidate", diag.Evidence);
        Assert.Contains("rx-audio-leveler-experimental-profile", diag.Evidence);
        Assert.Contains("rx-audio-leveler-control-rms-evidence", diag.Evidence);
        Assert.Contains("current-profile and candidate-profile", string.Join(" ", diag.RecommendedActions));
    }

    [Fact]
    public void Build_RuntimeEvidenceCarriesTxOutputHeadroomCandidateProfile()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR4", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Sbnr", "Sbnr"),
            RxChain(score: 94));

        var runtime = RuntimeEvidence(
            txOutputHeadroomRequestedProfile: "headroom-trim-candidate",
            txOutputHeadroomActiveProfile: "headroom-trim-candidate",
            txOutputHeadroomExperimental: true,
            txOutputHeadroomTrimDb: -0.35);

        var diag = DspLiveDiagnosticsService.Build(condition, runtime);

        Assert.NotNull(diag.RuntimeEvidence);
        Assert.Equal("headroom-trim-candidate", diag.RuntimeEvidence.TxOutputHeadroomRequestedProfile);
        Assert.Equal("headroom-trim-candidate", diag.RuntimeEvidence.TxOutputHeadroomActiveProfile);
        Assert.True(diag.RuntimeEvidence.TxOutputHeadroomExperimental);
        Assert.Equal(-0.35, diag.RuntimeEvidence.TxOutputHeadroomTrimDb);
        Assert.Contains("tx-output-headroom-requested-headroom-trim-candidate", diag.Evidence);
        Assert.Contains("tx-output-headroom-active-headroom-trim-candidate", diag.Evidence);
        Assert.Contains("tx-output-headroom-experimental-profile", diag.Evidence);
        Assert.Contains("TX output headroom profile opt-in", string.Join(" ", diag.RecommendedActions));
    }

    [Fact]
    public void Build_RuntimeEvidenceCarriesTxOutputHeadroomPureSignalBypass()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR4", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Sbnr", "Sbnr"),
            RxChain(score: 94));

        var runtime = RuntimeEvidence(
            txOutputHeadroomRequestedProfile: "headroom-trim-candidate",
            txOutputHeadroomActiveProfile: "current",
            txOutputHeadroomExperimental: true,
            txOutputHeadroomPureSignalBypassed: true);

        var diag = DspLiveDiagnosticsService.Build(condition, runtime);

        Assert.NotNull(diag.RuntimeEvidence);
        Assert.Equal("current", diag.RuntimeEvidence.TxOutputHeadroomActiveProfile);
        Assert.True(diag.RuntimeEvidence.TxOutputHeadroomPureSignalBypassed);
        Assert.Contains("tx-output-headroom-requested-headroom-trim-candidate", diag.Evidence);
        Assert.Contains("tx-output-headroom-puresignal-bypassed", diag.Evidence);
        Assert.DoesNotContain("tx-output-headroom-active-headroom-trim-candidate", diag.Evidence);
        Assert.Contains("PureSignal is armed", string.Join(" ", diag.RecommendedActions));
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
    public void BenchmarkPlanCatalog_CoversRxTxPureSignalAndLifecycleGates()
    {
        var plan = DspBenchmarkPlanCatalog.Build();

        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal("G2", plan.FirstHardwareTarget);
        Assert.Contains("off-baseline", plan.RequiredComparisons);
        Assert.Contains("thetis-parity", plan.RequiredComparisons);
        Assert.Contains("candidate-under-test", plan.RequiredComparisons);
        Assert.DoesNotContain("candidate-external-engine-opt-in", plan.RequiredComparisons);
        Assert.Contains(plan.GlobalAcceptanceGates, gate => gate.Contains("No weak-signal loss", StringComparison.Ordinal));
        Assert.Contains(plan.GlobalAcceptanceGates, gate => gate.Contains("PureSignal", StringComparison.Ordinal));

        var ids = plan.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("weak-cw-carrier", ids);
        Assert.Contains("ssb-like-speech", ids);
        Assert.Contains("agc-level-step", ids);
        Assert.Contains("rx-audio-leveler-passband", ids);
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

        var rxLeveler = Assert.Single(plan.Scenarios, s => s.Id == "rx-audio-leveler-passband");
        Assert.Equal("opt-in-fixture-and-live-a-b-ready", rxLeveler.FixtureStatus);
        Assert.Contains("candidate-under-test", rxLeveler.RequiredComparisons);
        Assert.Contains("RMS movement", rxLeveler.RequiredMetrics);
        Assert.Contains("windowed RMS movement", rxLeveler.RequiredMetrics);
        Assert.Contains("rx audio leveler fixture benchmark", rxLeveler.RequiredArtifacts);
        Assert.Contains(rxLeveler.AcceptanceGates, gate => gate.Contains("passband loudness movement", StringComparison.Ordinal));

        var lifecycle = Assert.Single(plan.Scenarios, s => s.Id == "wdsp-channel-lifecycle");
        Assert.Contains("state transition success", lifecycle.RequiredMetrics);
        Assert.Contains("meter escape", lifecycle.RequiredMetrics);
        Assert.Contains("audio drain", lifecycle.RequiredMetrics);
        Assert.Contains("native exception count", lifecycle.RequiredMetrics);
        Assert.Contains("lifecycle test log", lifecycle.RequiredArtifacts);
        Assert.Contains("diagnostics JSON", lifecycle.RequiredArtifacts);
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
            ("rx-audio-leveler-passband", ["rx-audio-leveler-passband", "rx-leveler-passband", "audio-leveler-passband"]),
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
    public void BenchmarkPlanCatalog_RequiresUnderTestComparisonForRxAcceptanceScenariosOnly()
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
            "rx-audio-leveler-passband",
        ];

        foreach (var scenarioId in rxScenarioIds)
        {
            var scenario = Assert.Single(plan.Scenarios, s => s.Id == scenarioId);
            Assert.Contains("candidate-under-test", scenario.RequiredComparisons);
            Assert.DoesNotContain("candidate-external-engine-opt-in", scenario.RequiredComparisons);
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
            Assert.DoesNotContain("candidate-under-test", scenario.RequiredComparisons);
            Assert.DoesNotContain("candidate-external-engine-opt-in", scenario.RequiredComparisons);
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
        Assert.Contains("tx-two-tone", manifest.ScenarioIds);
        Assert.Contains("tx-voice-like", manifest.ScenarioIds);
        Assert.Contains("wdsp-channel-lifecycle", manifest.ScenarioIds);
        Assert.Contains("capture-preflight-not-ready", manifest.Constraints);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "live-diagnostics-json" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "wdsp-native-symbol-audit" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "wdsp-runtime-artifact-audit" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "offline-fixture-metrics" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "rx-audio-leveler-fixture-benchmark" && !artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "native-stage-timing-report" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "tx-fixture-safety-report" && artifact.Required);
        Assert.Contains(manifest.RequiredArtifacts, artifact => artifact.Id == "wdsp-channel-lifecycle-report" && artifact.Required);
        Assert.DoesNotContain(manifest.RequiredArtifacts, artifact => artifact.Id == "external-engine-bakeoff-report");
        Assert.Contains(manifest.StopConditions, item => item.Contains("weak-signal loss", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureManifest_ReadyDefaultPlanListsG2EvidenceArtifacts()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "Light", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Off", "Off"),
            RxChain(score: 94));
        var live = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence());

        var manifest = DspBenchmarkCaptureManifestService.Build(live, DspBenchmarkPlanCatalog.Build());

        Assert.Equal("ready-for-g2-capture", manifest.Status);
        Assert.True(manifest.ReadyForCapture);
        Assert.Equal("G2", manifest.HardwareTarget);
        Assert.Contains("weak-cw-carrier", manifest.ScenarioIds);
        Assert.Contains("agc-level-step", manifest.ScenarioIds);
        Assert.Contains("tx-two-tone", manifest.ScenarioIds);
        Assert.Contains("tx-voice-like", manifest.ScenarioIds);
        Assert.Contains("wdsp-channel-lifecycle", manifest.ScenarioIds);
        Assert.Contains("thetis-parity", manifest.RequiredComparisons);
        Assert.Contains("candidate-under-test", manifest.RequiredComparisons);
        Assert.DoesNotContain("candidate-external-engine-opt-in", manifest.RequiredComparisons);
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
        var txFixtureSafetyArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "tx-fixture-safety-report");
        Assert.True(txFixtureSafetyArtifact.Required);
        Assert.Equal("tx-fixture-safety-report-json", txFixtureSafetyArtifact.Kind);
        Assert.Contains("summarize-dsp-tx-fixture-safety.ps1", txFixtureSafetyArtifact.Source);
        Assert.Contains("TX stage meter", txFixtureSafetyArtifact.Purpose, StringComparison.Ordinal);
        Assert.Equal(["tx-two-tone", "tx-voice-like"], txFixtureSafetyArtifact.ScenarioIds);
        var txHeadroomAbArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "tx-output-headroom-ab-trace");
        Assert.False(txHeadroomAbArtifact.Required);
        Assert.Equal("diagnostics-ab-summary-json", txHeadroomAbArtifact.Kind);
        Assert.Contains("capture-tx-output-headroom-ab.ps1", txHeadroomAbArtifact.Source);
        Assert.Contains("without keying TX", txHeadroomAbArtifact.Purpose, StringComparison.Ordinal);
        Assert.Equal(["tx-two-tone", "tx-voice-like", "tx-puresignal-safe-bypass"], txHeadroomAbArtifact.ScenarioIds);
        var lifecycleArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "wdsp-channel-lifecycle-report");
        Assert.True(lifecycleArtifact.Required);
        Assert.Equal("wdsp-channel-lifecycle-json", lifecycleArtifact.Kind);
        Assert.Contains("run-dsp-wdsp-channel-lifecycle.ps1", lifecycleArtifact.Source);
        Assert.Contains("SetMox", lifecycleArtifact.Purpose, StringComparison.Ordinal);
        Assert.Equal(["wdsp-channel-lifecycle"], lifecycleArtifact.ScenarioIds);
        var rxLevelerFixtureArtifact = Assert.Single(manifest.RequiredArtifacts, artifact => artifact.Id == "rx-audio-leveler-fixture-benchmark");
        Assert.False(rxLevelerFixtureArtifact.Required);
        Assert.Equal("metrics-json", rxLevelerFixtureArtifact.Kind);
        Assert.Contains("run-dsp-rx-leveler-fixture-benchmark.ps1", rxLevelerFixtureArtifact.Source);
        Assert.Contains("strong-after-weak", rxLevelerFixtureArtifact.Purpose, StringComparison.Ordinal);
        Assert.Contains("live crest headroom", rxLevelerFixtureArtifact.Purpose, StringComparison.Ordinal);
        Assert.Equal(
            ["ssb-syllable-step", "near-target-speech", "live-crest-headroom", "sustained-weak-speech", "strong-after-weak"],
            rxLevelerFixtureArtifact.ScenarioIds);
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
        Assert.DoesNotContain("rx-audio-leveler-fixture-benchmark", snapshot.IncludedArtifacts);
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
    public void ModernizationSnapshot_ReadyDefaultCaptureKeepsBakeoffOutOfBundle()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "Light", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Off", "Off"),
            RxChain(score: 94));
        var live = DspLiveDiagnosticsService.Build(condition, RuntimeEvidence());
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
        Assert.DoesNotContain("rx-audio-leveler-fixture-benchmark", snapshot.IncludedArtifacts);
        Assert.DoesNotContain("external-engine-bakeoff-report", snapshot.IncludedArtifacts);
        Assert.DoesNotContain("live-diagnostics-history", snapshot.IncludedArtifacts);
        Assert.Contains(snapshot.NextActions, action => action.Contains("Save this modernization snapshot", StringComparison.Ordinal));
        Assert.Contains(snapshot.ExternalEngineCandidates, candidate => candidate.Id == "rnnoise");
    }

    [Fact]
    public void CaptureManifest_RequiresExternalBakeoffOnlyWhenExternalComparisonIsScoped()
    {
        var service = new FrontendDspSceneDiagnosticsService();
        PublishScene(service, profile: "NR4", held: false, rxScore: 94, rxTone: "neutral", coherent: true);
        var condition = service.SmartNrCondition(
            Runtime("Sbnr", "Sbnr"),
            RxChain(score: 94));
        var live = DspLiveDiagnosticsService.Build(condition);
        var plan = DspBenchmarkPlanCatalog.Build() with
        {
            RequiredComparisons =
            [
                "off-baseline",
                "thetis-parity",
                "current-zeus",
                "candidate-external-engine-opt-in",
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
            Assert.Equal(
                c.Id == "rnnoise"
                    ? "rx-vst-plugin-path-supported-not-bundled"
                    : "catalog-only-not-integrated",
                c.EvaluationStage);
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
        Assert.Contains("RX Audio Suite", rnnoise.IntegrationPoint);
        Assert.Contains("rx-audio-suite-route", rnnoise.RequiredControls);
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
        Assert.Contains("RX Audio Suite", rmnoise.IntegrationPoint);
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
        Assert.Contains("RX Audio Suite", dpdfnet.IntegrationPoint);
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
        bool nr4Available = true) =>
        new(
            WdspActive: wdspActive,
            WdspNativeLoadable: nativeLoadable,
            WdspEmnrPost2Available: true,
            WdspNr4SbnrAvailable: nr4Available,
            Nr4Readiness: nr4Available ? "available" : "missing-sbnr-exports",
            RequestedNrMode: requested,
            EffectiveNrMode: effective);

    private static SmartNrRxChainRuntimeDto RxChain(int score, bool adcOverload = false) =>
        new(
            SchemaVersion: 2,
            Source: "test",
            FilterLowHz: 300,
            FilterHighHz: 2600,
            FilterWidthHz: 2300,
            FilterPresetName: "WEAK-RX",
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

    private static DspLiveRuntimeEvidenceDto RuntimeEvidence(
        string status = "fresh",
        string audioStatus = "fresh",
        double? audioPeakDbfs = -12.0,
        double? adcHeadroomDb = 24.0,
        string levelerRequestedProfile = "current",
        string levelerActiveProfile = "current",
        bool levelerExperimental = false,
        bool? levelerControlRmsValid = null,
        double? levelerControlRmsDbfs = null,
        double? levelerControlRmsHangDb = null,
        string? txOutputHeadroomRequestedProfile = null,
        string? txOutputHeadroomActiveProfile = null,
        bool? txOutputHeadroomExperimental = null,
        double? txOutputHeadroomTrimDb = null,
        bool? txOutputHeadroomPureSignalBypassed = null) =>
        new(
            SchemaVersion: 5,
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
            RxAudioLevelerBoostSlewLimited: false,
            RxAudioLevelerPeakLimited: false,
            RxAudioLevelerOutputLimited: false,
            RxAudioLevelerRequestedProfile: levelerRequestedProfile,
            RxAudioLevelerActiveProfile: levelerActiveProfile,
            RxAudioLevelerExperimental: levelerExperimental,
            RxAudioLevelerControlRmsValid: levelerControlRmsValid,
            RxAudioLevelerControlRmsDbfs: levelerControlRmsDbfs,
            RxAudioLevelerControlRmsHangDb: levelerControlRmsHangDb,
            TxOutputHeadroomRequestedProfile: txOutputHeadroomRequestedProfile,
            TxOutputHeadroomActiveProfile: txOutputHeadroomActiveProfile,
            TxOutputHeadroomExperimental: txOutputHeadroomExperimental,
            TxOutputHeadroomTrimDb: txOutputHeadroomTrimDb,
            TxOutputHeadroomPureSignalBypassed: txOutputHeadroomPureSignalBypassed,
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
