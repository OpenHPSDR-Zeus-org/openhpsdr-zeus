// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for /api/dsp/nr-condition. The route exposes the existing
// frontend Smart NR condition feed plus backend NR runtime readiness.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public sealed class SmartNrConditionEndpointTests
{
    [Fact]
    public void BuildSmartNrRxChainRuntime_ExposesAgcAttenuationAndAdcState()
    {
        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.0.2.10:1024",
            VfoHz: 7_262_000,
            Mode: RxMode.LSB,
            FilterLowHz: -3500,
            FilterHighHz: -100,
            SampleRate: 384_000,
            AgcTopDb: 83,
            Agc: new AgcConfig(Mode: AgcMode.Fast),
            Squelch: new SquelchConfig(Enabled: true, Level: 18, Adaptive: true),
            AttenDb: 1,
            AutoAttEnabled: true,
            AttOffsetDb: 2,
            AdcOverloadWarning: true,
            FilterPresetName: "F4",
            AutoAgcEnabled: true,
            AgcOffsetDb: -31,
            PreampOn: true);
        var adc = new AdcProtectionStatusDto(
            Config: new AdcProtectionConfig(Enabled: true),
            AttenDb: 1,
            OffsetDb: 2,
            EffectiveDb: 3,
            Warning: true,
            OverloadLevel: 4,
            LastOverloadBits: 0x03,
            Adc0MaxMagnitude: 44_000,
            Adc1MaxMagnitude: null,
            Adc0MaxMagnitudeAtOverload: 50_000,
            Adc1MaxMagnitudeAtOverload: 0,
            LastTelemetryUtc: DateTimeOffset.UtcNow);

        var runtime = ZeusEndpoints.BuildSmartNrRxChainRuntime(state, adc);

        Assert.Equal(2, runtime.SchemaVersion);
        Assert.Equal("backend-radio-state", runtime.Source);
        Assert.Equal(-3500, runtime.FilterLowHz);
        Assert.Equal(-100, runtime.FilterHighHz);
        Assert.Equal(3400, runtime.FilterWidthHz);
        Assert.Equal("F4", runtime.FilterPresetName);
        Assert.True(runtime.AutoAgcEnabled);
        Assert.Equal("Fast", runtime.AgcMode);
        Assert.Equal(52, runtime.EffectiveAgcTopDb);
        Assert.True(runtime.AutoAttEnabled);
        Assert.Equal(3, runtime.EffectiveAttenDb);
        Assert.True(runtime.AdcOverloadWarning);
        Assert.Equal(4, runtime.AdcOverloadLevel);
        Assert.Equal((ushort)44_000, runtime.Adc0MaxMagnitude);
        Assert.Null(runtime.Adc1MaxMagnitude);
        Assert.True(runtime.SquelchEnabled);
        Assert.Equal(18, runtime.SquelchLevel);
        Assert.True(runtime.PreampOn);
    }

    [Fact]
    public async Task GetBeforeFrontendScene_ReturnsMissingConditionWithNrRuntime()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dsp/nr-condition");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal("missing", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Contains(root.GetProperty("requestedNrMode").GetString(), new[] { "Off", "Anr", "Emnr", "Sbnr" });
        Assert.Contains(root.GetProperty("effectiveNrMode").GetString(), new[] { "Off", "Anr", "Emnr", "Sbnr" });
        Assert.Contains(
            root.GetProperty("nr4Readiness").GetString(),
            new[] { "available", "missing-sbnr-exports", "wdsp-native-unloadable" });
        var rxChain = root.GetProperty("rxChain");
        Assert.Equal("backend-radio-state", rxChain.GetProperty("source").GetString());
        Assert.True(rxChain.GetProperty("filterWidthHz").GetInt32() >= 0);
        Assert.True(double.IsFinite(rxChain.GetProperty("agcTopDb").GetDouble()));
        Assert.True(double.IsFinite(rxChain.GetProperty("effectiveAgcTopDb").GetDouble()));
        Assert.Contains(rxChain.GetProperty("autoAttEnabled").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains("No frontend DSP scene", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task GetAfterFrontendScene_ReturnsFreshSmartNrCondition()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/radio/diagnostics/dsp-scene", new
        {
            sourceClientId = "frontend-test",
            mode = "USB",
            signalProfile = "dx",
            signalReason = "coherent weak ridge",
            smartNrProfile = "NR2",
            smartNrReason = "weak sparse signal",
            smartNrRecommendation = "Keep RX headroom and use gentle NR2",
            smartNrHeldByRxChain = true,
            smartNrRxChainLabel = "ADC headroom limited",
            smartNrRxChainRecommendation = "Add 3-6 dB attenuation",
            smartNrRxChainTone = "protect",
            smartNrRxChainScore = 62,
            maxSnrDb = 7.14,
            coherentMaxSnrDb = 6.83,
            occupiedPct = 1.23,
            coherentOccupiedPct = 0.84,
            impulsivePct = 0.12,
            peakCount = 1,
            coherentPeakCount = 1,
            coherentSubthresholdSignal = true,
            sourceAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var resp = await client.GetAsync("/api/dsp/nr-condition");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("fresh", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal("USB", root.GetProperty("mode").GetString());
        Assert.Equal("NR2", root.GetProperty("profile").GetString());
        Assert.Equal("weak sparse signal", root.GetProperty("reason").GetString());
        Assert.Equal("Keep RX headroom and use gentle NR2", root.GetProperty("recommendation").GetString());
        Assert.True(root.GetProperty("heldByRxChain").GetBoolean());
        Assert.Equal("ADC headroom limited", root.GetProperty("rxChainLabel").GetString());
        Assert.Equal("Add 3-6 dB attenuation", root.GetProperty("rxChainRecommendation").GetString());
        Assert.Equal("protect", root.GetProperty("rxChainTone").GetString());
        Assert.Equal(62, root.GetProperty("rxChainScore").GetInt32());
        Assert.Equal(7.1, root.GetProperty("maxSnrDb").GetDouble());
        Assert.Equal(6.8, root.GetProperty("coherentMaxSnrDb").GetDouble());
        Assert.Equal(1, root.GetProperty("peakCount").GetInt32());
        Assert.True(root.GetProperty("coherentSubthresholdSignal").GetBoolean());
        Assert.Contains(root.GetProperty("effectiveNrMode").GetString(), new[] { "Off", "Anr", "Emnr", "Sbnr" });
        var rxChain = root.GetProperty("rxChain");
        Assert.Equal("backend-radio-state", rxChain.GetProperty("source").GetString());
        Assert.Contains(rxChain.GetProperty("agcMode").GetString(), new[] { "Fixed", "Long", "Slow", "Med", "Fast", "Custom" });
        Assert.Contains(rxChain.GetProperty("adcProtectionEnabled").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains(rxChain.GetProperty("squelchEnabled").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains("constrained by RX-chain health", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task GetLiveDiagnostics_ReturnsToolFriendlyModernizationSummary()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/radio/diagnostics/dsp-scene", new
        {
            sourceClientId = "frontend-live",
            mode = "USB",
            signalProfile = "dx",
            signalReason = "coherent weak ridge",
            smartNrProfile = "NR2",
            smartNrReason = "weak sparse signal",
            smartNrRecommendation = "Keep RX headroom and use gentle NR2",
            smartNrHeldByRxChain = false,
            smartNrRxChainLabel = "RX chain optimized",
            smartNrRxChainRecommendation = "Hold front-end settings",
            smartNrRxChainTone = "neutral",
            smartNrRxChainScore = 91,
            maxSnrDb = 7.14,
            coherentMaxSnrDb = 6.83,
            occupiedPct = 1.23,
            coherentOccupiedPct = 0.84,
            impulsivePct = 0.12,
            peakCount = 1,
            coherentPeakCount = 1,
            coherentSubthresholdSignal = true,
            sourceAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var resp = await client.GetAsync("/api/dsp/live-diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("readinessScore").GetInt32() >= 0);
        Assert.Equal("opt-in-only-until-benchmark-and-g2-on-air-acceptance", root.GetProperty("rolloutGate").GetString());
        Assert.True(root.GetProperty("frontendSceneAvailable").GetBoolean());
        Assert.Equal("NR2", root.GetProperty("smartNrProfile").GetString());
        Assert.False(root.GetProperty("readyForExternalEngineBakeoff").GetBoolean());
        Assert.Equal(
            "external-engine-bakeoff-preflight-required",
            root.GetProperty("externalEngineBakeoffStatus").GetString());
        Assert.Contains(
            root.GetProperty("externalEngineBakeoffConstraints").EnumerateArray().Select(item => item.GetString()),
            item => item == "final-audio-not-fresh");
        Assert.Contains(root.GetProperty("status").GetString(), new[]
        {
            "dsp-engine-unavailable",
            // On Linux/CI without FFTW, libwdsp.so can't load and the
            // diagnostics correctly report this — a legitimate non-ready
            // status the allow-list was simply missing.
            "wdsp-native-unloadable",
            "frontend-scene-missing",
            "smart-nr-runtime-misaligned",
            "smart-nr-apply-pending",
            "verify-before-tuning",
            "ready-for-live-benchmark",
        });

        var tools = root.GetProperty("candidateTools").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("offline-dsp-benchmark-harness", tools);
        Assert.Contains("dsp-benchmark-acceptance-plan", tools);
        Assert.Contains("dsp-live-runtime-evidence", tools);
        Assert.Contains("g2-live-capture", tools);
        Assert.Contains("external-engine-live-bakeoff-watch", tools);
        Assert.Contains("external-post-demod-bakeoff:rnnoise", tools);
        Assert.Contains("external-post-demod-bakeoff:deepfilternet", tools);
        Assert.Equal("/api/dsp/benchmark-plan", root.GetProperty("benchmarkPlanEndpoint").GetString());
        Assert.True(root.GetProperty("benchmarkScenarioCount").GetInt32() >= 12);
        Assert.Equal("missing", root.GetProperty("runtimeEvidence").GetProperty("audioStatus").GetString());
        Assert.False(root.GetProperty("runtimeEvidence").GetProperty("audioFresh").GetBoolean());
        Assert.NotEmpty(root.GetProperty("nextBenchmarkScenarios").EnumerateArray());
        Assert.Contains(
            root.GetProperty("benchmarkAcceptanceGates").EnumerateArray().Select(item => item.GetString()),
            gate => gate is not null && gate.Contains("No weak-signal loss", StringComparison.Ordinal));

        var candidates = root.GetProperty("externalEngineCandidates")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Contains("rnnoise", candidates);
        Assert.Contains("rmnoise", candidates);
        Assert.Contains("deepfilternet", candidates);
        Assert.Contains("speexdsp", candidates);
        Assert.Contains("webrtc-apm", candidates);
    }

    [Fact]
    public async Task GetBenchmarkPlan_ReturnsModernizationAcceptanceScenarios()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dsp/benchmark-plan");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("G2", root.GetProperty("firstHardwareTarget").GetString());
        Assert.Contains(
            root.GetProperty("requiredComparisons").EnumerateArray().Select(item => item.GetString()),
            item => item == "thetis-parity");
        Assert.Contains(
            root.GetProperty("globalAcceptanceGates").EnumerateArray().Select(item => item.GetString()),
            item => item is not null && item.Contains("No TX clipping", StringComparison.Ordinal));

        var scenarios = root.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.True(scenarios.Length >= 12);
        var ids = scenarios.Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Contains("weak-cw-carrier", ids);
        Assert.Contains("ssb-like-speech", ids);
        Assert.Contains("rx-audio-leveler-passband", ids);
        Assert.Contains("tx-puresignal-safe-bypass", ids);
        Assert.Contains("wdsp-channel-lifecycle", ids);

        var rxLeveler = scenarios.Single(item => item.GetProperty("id").GetString() == "rx-audio-leveler-passband");
        Assert.Equal("opt-in-fixture-and-live-a-b-ready", rxLeveler.GetProperty("fixtureStatus").GetString());
        Assert.Contains(
            rxLeveler.GetProperty("requiredComparisons").EnumerateArray().Select(item => item.GetString()),
            item => item == "candidate-under-test");
        Assert.Contains(
            rxLeveler.GetProperty("requiredArtifacts").EnumerateArray().Select(item => item.GetString()),
            item => item == "G2 RX leveler A/B summary");

        var pureSignal = scenarios.Single(item => item.GetProperty("id").GetString() == "tx-puresignal-safe-bypass");
        Assert.Equal("hardware-capture-required", pureSignal.GetProperty("fixtureStatus").GetString());
        Assert.Contains(
            pureSignal.GetProperty("acceptanceGates").EnumerateArray().Select(item => item.GetString()),
            item => item is not null && item.Contains("PureSignal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetBenchmarkMetricCatalog_ReturnsMetricDirectionSemantics()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dsp/benchmark-metric-catalog");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Contains(
            root.GetProperty("directionValues").EnumerateArray().Select(item => item.GetString()),
            item => item == "lower");
        Assert.Contains(
            root.GetProperty("comparatorValues").EnumerateArray().Select(item => item.GetString()),
            item => item == "no-regression");

        var metrics = root.GetProperty("metrics").EnumerateArray().ToArray();
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "wantedsnr"
            && item.GetProperty("direction").GetString() == "higher"
            && item.GetProperty("acceptanceComparator").GetString() == "no-regression"
            && item.GetProperty("acceptanceScopes").EnumerateArray().Any(scope => scope.GetString() == "weak-cw-carrier"));
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "signalsinad"
            && item.GetProperty("direction").GetString() == "higher"
            && item.GetProperty("acceptanceScopes").EnumerateArray().Any(scope => scope.GetString() == "weak-cw-carrier"));
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "agcgainmovement"
            && item.GetProperty("direction").GetString() == "lower");
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "processingelapsedms"
            && item.GetProperty("direction").GetString() == "lower"
            && item.GetProperty("acceptanceScopes").EnumerateArray().Any(scope => scope.GetString() == "tx-two-tone"));
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "throughputratio"
            && item.GetProperty("direction").GetString() == "higher"
            && item.GetProperty("acceptanceScopes").EnumerateArray().Any(scope => scope.GetString() == "weak-cw-carrier"));
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "outputrms"
            && item.GetProperty("direction").GetString() == "informational"
            && item.GetProperty("acceptanceComparator").GetString() == "informational");
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "clippingcount"
            && item.GetProperty("acceptanceThreshold").GetString() == "0"
            && item.GetProperty("acceptanceComparator").GetString() == "at-or-below");
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "txlevelergainreduction"
            && item.GetProperty("direction").GetString() == "lower"
            && item.GetProperty("acceptanceScopes").EnumerateArray().Any(scope => scope.GetString() == "tx-two-tone"));
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "txalcgainreduction"
            && item.GetProperty("direction").GetString() == "lower");
        Assert.Contains(metrics, item =>
            item.GetProperty("id").GetString() == "txcompressorpeak"
            && item.GetProperty("direction").GetString() == "informational"
            && item.GetProperty("acceptanceScopes").EnumerateArray().Any(scope => scope.GetString() == "tx-voice-like"));
    }

    [Fact]
    public async Task GetBenchmarkCaptureManifest_ReturnsCurrentEvidenceChecklist()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dsp/benchmark-capture-manifest");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.StartsWith("dsp-capture-", root.GetProperty("manifestId").GetString(), StringComparison.Ordinal);
        Assert.Equal("G2", root.GetProperty("hardwareTarget").GetString());
        Assert.Equal("/api/dsp/live-diagnostics", root.GetProperty("liveDiagnosticsEndpoint").GetString());
        Assert.Equal("/api/dsp/benchmark-plan", root.GetProperty("benchmarkPlanEndpoint").GetString());
        Assert.Equal("/api/dsp/external-engine-candidates", root.GetProperty("externalEngineCandidatesEndpoint").GetString());
        Assert.Contains(
            root.GetProperty("scenarioIds").EnumerateArray().Select(item => item.GetString()),
            item => item == "wdsp-channel-lifecycle");
        Assert.Contains(
            root.GetProperty("requiredComparisons").EnumerateArray().Select(item => item.GetString()),
            item => item == "current-zeus");
        Assert.NotEmpty(root.GetProperty("preflightChecks").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("stopConditions").EnumerateArray());

        var artifacts = root.GetProperty("requiredArtifacts").EnumerateArray().ToArray();
        Assert.Contains(artifacts, item => item.GetProperty("id").GetString() == "live-diagnostics-json");
        Assert.Contains(artifacts, item => item.GetProperty("id").GetString() == "live-diagnostics-trace-index");
        Assert.Contains(artifacts, item => item.GetProperty("id").GetString() == "live-diagnostics-history");
        Assert.Contains(artifacts, item => item.GetProperty("id").GetString() == "wdsp-runtime-artifact-audit");
        Assert.Contains(artifacts, item => item.GetProperty("source").GetString() == "/api/radio/diagnostics/dsp-scene");
        Assert.Contains(artifacts, item => item.GetProperty("id").GetString() == "offline-fixture-metrics");
        Assert.Contains(artifacts, item =>
            item.GetProperty("id").GetString() == "rx-audio-leveler-fixture-benchmark" &&
            !item.GetProperty("required").GetBoolean() &&
            item.GetProperty("source").GetString() == "tools/run-dsp-rx-leveler-fixture-benchmark.ps1");
        Assert.Contains(artifacts, item =>
            item.GetProperty("id").GetString() == "tx-output-headroom-ab-trace" &&
            item.GetProperty("kind").GetString() == "diagnostics-ab-summary-json" &&
            !item.GetProperty("required").GetBoolean() &&
            item.GetProperty("source").GetString() == "tools/capture-tx-output-headroom-ab.ps1");
    }

    [Fact]
    public async Task GetModernizationSnapshot_ReturnsOneCallEvidenceBundle()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dsp/modernization-snapshot");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.StartsWith("dsp-modernization-", root.GetProperty("snapshotId").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("evidenceCompletenessScore").GetInt32() is >= 0 and <= 100);
        Assert.Equal("G2", root.GetProperty("hardwareTarget").GetString());
        Assert.Contains(
            root.GetProperty("includedEndpoints").EnumerateArray().Select(item => item.GetString()),
            item => item == "/api/dsp/modernization-snapshot");
        Assert.Contains(
            root.GetProperty("includedArtifacts").EnumerateArray().Select(item => item.GetString()),
            item => item == "live-diagnostics-json");
        Assert.Contains(
            root.GetProperty("missingEvidence").EnumerateArray().Select(item => item.GetString()),
            item => item == "frontend-dsp-scene");
        Assert.Equal(1, root.GetProperty("smartNrCondition").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("liveDiagnostics").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("benchmarkPlan").GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("captureManifest").GetProperty("schemaVersion").GetInt32());
        Assert.NotEmpty(root.GetProperty("externalEngineCandidates").EnumerateArray());
    }

    [Fact]
    public async Task GetExternalEngineCandidates_ReturnsOptInBakeoffCatalog()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dsp/external-engine-candidates");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var candidates = body.RootElement.EnumerateArray().ToArray();

        Assert.Equal(7, candidates.Length);
        var ids = candidates.Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { "rnnoise", "rmnoise", "dpdfnet", "deepfilternet", "clearervoice-studio", "speexdsp", "webrtc-apm" }, ids);
        Assert.All(candidates, item =>
        {
            Assert.Equal("off", item.GetProperty("defaultState").GetString());
            Assert.Equal("candidate-only-opt-in-bakeoff", item.GetProperty("rolloutPolicy").GetString());
            Assert.Contains("post-demod", item.GetProperty("integrationPoint").GetString());
            Assert.Equal(
                item.GetProperty("id").GetString() == "rnnoise"
                    ? "rx-vst-plugin-path-supported-not-bundled"
                    : "catalog-only-not-integrated",
                item.GetProperty("evaluationStage").GetString());
            Assert.Contains(
                item.GetProperty("allowedSignalPaths").EnumerateArray().Select(path => path.GetString()),
                path => path is not null && path.Contains("post-demod", StringComparison.Ordinal));
            Assert.Contains(
                item.GetProperty("forbiddenSignalPaths").EnumerateArray().Select(path => path.GetString()),
                path => path == "raw-wdsp-iq");
            Assert.Contains(
                item.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
                control => control == "operator-visible-opt-in");
            Assert.Contains(
                item.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
                control => control == "clean-bypass-fallback");
            var fallbackPolicy = item.GetProperty("fallbackPolicy").GetString() ?? "";
            Assert.True(
                fallbackPolicy.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                || fallbackPolicy.Contains("fall back", StringComparison.OrdinalIgnoreCase)
                || fallbackPolicy.Contains("bypass", StringComparison.OrdinalIgnoreCase),
                $"Missing fallback or bypass policy for {item.GetProperty("id").GetString()}");
            Assert.NotEmpty(item.GetProperty("requiredBenchmarks").EnumerateArray());
            Assert.NotEmpty(item.GetProperty("requiredEvidence").EnumerateArray());
            Assert.NotEmpty(item.GetProperty("blockers").EnumerateArray());
            Assert.NotEmpty(item.GetProperty("referenceUrls").EnumerateArray());
        });

        var webrtc = Assert.Single(candidates, item => item.GetProperty("id").GetString() == "webrtc-apm");
        Assert.Contains(
            webrtc.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "webrtc-aec-disabled");
        Assert.Contains(
            webrtc.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "webrtc-agc-disabled");

        var rnnoise = Assert.Single(candidates, item => item.GetProperty("id").GetString() == "rnnoise");
        Assert.Contains("RX Audio Suite", rnnoise.GetProperty("integrationPoint").GetString());
        Assert.Contains(
            rnnoise.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "official-xiph-runtime-only");
        Assert.Contains(
            rnnoise.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "le9endary-training-reference-only");
        Assert.Contains(
            rnnoise.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "werman-plugin-reference-only");
        Assert.Contains(
            rnnoise.GetProperty("requiredBenchmarks").EnumerateArray().Select(benchmark => benchmark.GetString()),
            benchmark => benchmark == "weak-ssb-volume-parity");
        Assert.Contains("le9endary/RNNoise has no repo license", rnnoise.GetProperty("license").GetString());
        Assert.Contains("werman/noise-suppression-for-voice is GPL-3.0", rnnoise.GetProperty("license").GetString());
        Assert.Contains(
            rnnoise.GetProperty("referenceUrls").EnumerateArray().Select(url => url.GetString()),
            url => url == "https://github.com/le9endary/RNNoise");
        Assert.Contains(
            rnnoise.GetProperty("referenceUrls").EnumerateArray().Select(url => url.GetString()),
            url => url == "https://github.com/werman/noise-suppression-for-voice");

        var rmnoise = Assert.Single(candidates, item => item.GetProperty("id").GetString() == "rmnoise");
        Assert.Contains("RX Audio Suite", rmnoise.GetProperty("integrationPoint").GetString());
        Assert.Contains(
            rmnoise.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "recording-consent-gate");
        Assert.Contains(
            rmnoise.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "service-availability-fallback");
        Assert.Contains(
            rmnoise.GetProperty("requiredBenchmarks").EnumerateArray().Select(benchmark => benchmark.GetString()),
            benchmark => benchmark == "service-unavailable-bypass");
        Assert.Contains("service terms", rmnoise.GetProperty("license").GetString());
        Assert.Contains(
            rmnoise.GetProperty("referenceUrls").EnumerateArray().Select(url => url.GetString()),
            url => url == "https://ournetplace.com/rm-noise/");

        var dpdfnet = Assert.Single(candidates, item => item.GetProperty("id").GetString() == "dpdfnet");
        Assert.Contains("RX Audio Suite", dpdfnet.GetProperty("integrationPoint").GetString());
        Assert.Contains(
            dpdfnet.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "onnx-or-tflite-runtime-package-review");
        Assert.Contains(
            dpdfnet.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "48khz-frame-adapter");
        Assert.Contains(
            dpdfnet.GetProperty("requiredBenchmarks").EnumerateArray().Select(benchmark => benchmark.GetString()),
            benchmark => benchmark == "realtime-latency-g2");
        Assert.Contains("Apache-2.0", dpdfnet.GetProperty("license").GetString());
        Assert.Contains(
            dpdfnet.GetProperty("referenceUrls").EnumerateArray().Select(url => url.GetString()),
            url => url == "https://github.com/ceva-ip/DPDFNet");

        var clearerVoice = Assert.Single(candidates, item => item.GetProperty("id").GetString() == "clearervoice-studio");
        Assert.Contains("offline", clearerVoice.GetProperty("integrationPoint").GetString());
        Assert.Contains(
            clearerVoice.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "offline-only-until-runtime-approved");
        Assert.Contains(
            clearerVoice.GetProperty("requiredControls").EnumerateArray().Select(control => control.GetString()),
            control => control == "recording-consent-gate");
        Assert.Contains(
            clearerVoice.GetProperty("requiredBenchmarks").EnumerateArray().Select(benchmark => benchmark.GetString()),
            benchmark => benchmark == "offline-bypass");
        Assert.Contains("Apache-2.0", clearerVoice.GetProperty("license").GetString());
        Assert.Contains(
            clearerVoice.GetProperty("referenceUrls").EnumerateArray().Select(url => url.GetString()),
            url => url == "https://github.com/modelscope/ClearerVoice-Studio");
    }

    [Fact]
    public async Task GetDspSceneAfterFrontendPost_ReturnsSceneSnapshot()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/radio/diagnostics/dsp-scene", new
        {
            sourceClientId = "frontend-live",
            mode = "LSB",
            signalProfile = "weak-sparse",
            signalReason = "single coherent ridge",
            smartNrProfile = "NR2",
            smartNrRecommendation = "Keep RX headroom and use gentle NR2",
            smartNrRxChainLabel = "AGC stressed",
            smartNrRxChainRecommendation = "Auto AGC reducing AGC-T under ADC pressure",
            smartNrRxChainTone = "optimize",
            smartNrRxChainScore = 68,
            maxSnrDb = 5.64,
            coherentMaxSnrDb = 5.31,
            occupiedPct = 0.74,
            coherentOccupiedPct = 0.62,
            peakCount = 1,
            coherentPeakCount = 1,
            coherentSubthresholdSignal = true,
            sourceAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var resp = await client.GetAsync("/api/radio/diagnostics/dsp-scene");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("fresh", root.GetProperty("status").GetString());
        Assert.Equal("frontend-live", root.GetProperty("sourceClientId").GetString());
        Assert.Equal("LSB", root.GetProperty("mode").GetString());
        Assert.Equal("weak-sparse", root.GetProperty("signalProfile").GetString());
        Assert.Equal("NR2", root.GetProperty("smartNrProfile").GetString());
        Assert.Equal("AGC stressed", root.GetProperty("smartNrRxChainLabel").GetString());
        Assert.Equal("Auto AGC reducing AGC-T under ADC pressure", root.GetProperty("smartNrRxChainRecommendation").GetString());
        Assert.Equal("optimize", root.GetProperty("smartNrRxChainTone").GetString());
        Assert.Equal(68, root.GetProperty("smartNrRxChainScore").GetInt32());
        Assert.Equal(5.6, root.GetProperty("maxSnrDb").GetDouble());
        Assert.Equal(5.3, root.GetProperty("coherentMaxSnrDb").GetDouble());
        Assert.True(root.GetProperty("coherentSubthresholdSignal").GetBoolean());
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesLiveNrConditionEndpoint()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var smartNr = body.RootElement
            .GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "rx.smart-nr.adaptive");
        var controls = smartNr.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("/api/dsp/nr-condition", controls);
        Assert.Contains("/api/dsp/live-diagnostics", controls);
        Assert.Contains("/api/dsp/external-engine-candidates", controls);
        Assert.Contains("/api/dsp/benchmark-plan", controls);
        Assert.Contains("/api/dsp/benchmark-capture-manifest", controls);
        Assert.Contains("/api/dsp/modernization-snapshot", controls);
        Assert.Contains("/api/radio/diagnostics/dsp-scene", controls);
        Assert.DoesNotContain("planned:/api/dsp/nr-condition", controls);

        var telemetry = smartNr.GetProperty("telemetryPaths")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("/api/dsp/live-diagnostics.externalEngineCandidates", telemetry);
        Assert.Contains("/api/dsp/external-engine-candidates[].requiredBenchmarks", telemetry);
        Assert.Contains("/api/dsp/external-engine-candidates[].requiredControls", telemetry);
        Assert.Contains("/api/dsp/external-engine-candidates[].forbiddenSignalPaths", telemetry);
        Assert.Contains("/api/dsp/external-engine-candidates[].fallbackPolicy", telemetry);
        Assert.Contains("/api/dsp/benchmark-plan.globalAcceptanceGates", telemetry);
        Assert.Contains("/api/dsp/benchmark-plan.scenarios[].acceptanceGates", telemetry);
        Assert.Contains("/api/dsp/benchmark-metric-catalog.metrics[].acceptanceComparator", telemetry);
        Assert.Contains("/api/dsp/benchmark-metric-catalog.metrics[].acceptanceScopes", telemetry);
        Assert.Contains("/api/dsp/benchmark-capture-manifest.requiredArtifacts", telemetry);
        Assert.Contains("/api/dsp/benchmark-capture-manifest.stopConditions", telemetry);
        Assert.Contains("/api/dsp/modernization-snapshot.evidenceCompletenessScore", telemetry);
        Assert.Contains("/api/dsp/modernization-snapshot.missingEvidence", telemetry);
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
    }
}
