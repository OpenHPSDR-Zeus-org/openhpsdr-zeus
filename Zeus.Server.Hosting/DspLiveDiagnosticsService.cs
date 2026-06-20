// SPDX-License-Identifier: GPL-2.0-or-later
//
// Read-only live DSP modernization diagnostics. This service deliberately
// consumes snapshots and does no DSP work on the realtime path.

using Zeus.Contracts;

namespace Zeus.Server;

public static class DspLiveDiagnosticsService
{
    public static DspLiveDiagnosticsDto Build(
        SmartNrConditionDto condition,
        DspLiveRuntimeEvidenceDto? runtimeEvidence = null,
        StateDto? radioState = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var evidence = new List<string>();
        var constraints = new List<string>();
        var actions = new List<string>();
        var benchmarkPlan = DspBenchmarkPlanCatalog.Build();
        var externalCandidates = DspExternalEngineCandidateCatalog.All();
        var tools = new List<string>
        {
            "wdsp-lineage-parity-audit",
            "offline-dsp-benchmark-harness",
            "dsp-benchmark-acceptance-plan",
            "dsp-benchmark-metric-catalog",
            "dsp-live-runtime-evidence",
            "g2-live-capture",
            "g2-rx-peak-hunt",
        };

        int score = 100;
        if (condition.WdspNativeLoadable)
            evidence.Add("wdsp-native-loadable");
        else
        {
            score -= 35;
            constraints.Add("wdsp-native-unloadable");
            actions.Add("Fix native WDSP packaging/loading before judging DSP quality.");
        }

        if (condition.WdspActive)
            evidence.Add("wdsp-active");
        else
        {
            score -= 40;
            constraints.Add("wdsp-inactive");
            actions.Add("Connect the radio or restart the DSP engine so live WDSP telemetry is available.");
        }

        if (condition.WdspEmnrPost2Available)
            evidence.Add("emnr-post2-available");
        else
        {
            constraints.Add("emnr-post2-unavailable");
            if (ModeEquals(condition.ExpectedNrMode, "Emnr") || ModeEquals(condition.RequestedNrMode, "Emnr"))
                score -= 8;
        }

        if (condition.WdspNr4SbnrAvailable)
            evidence.Add("nr4-sbnr-available");
        else if (ModeEquals(condition.ExpectedNrMode, "Sbnr") || ModeEquals(condition.RequestedNrMode, "Sbnr"))
        {
            score -= 20;
            constraints.Add("nr4-sbnr-exports-missing");
            actions.Add("Rebuild or install WDSP with NR4/SBNR exports before evaluating NR4 as best-in-class.");
        }

        if (!condition.Available)
        {
            score -= 25;
            constraints.Add("frontend-dsp-scene-missing");
            actions.Add("Open a Zeus frontend with Signal Intelligence and Smart NR enabled so scene evidence is live.");
            tools.Add("frontend-dsp-scene-publisher");
        }
        else
        {
            evidence.Add("frontend-dsp-scene-available");
            if (condition.Fresh)
                evidence.Add("frontend-dsp-scene-fresh");
            if (condition.Stale)
            {
                score -= 35;
                constraints.Add("frontend-dsp-scene-stale");
                actions.Add("Refresh or reconnect the frontend before using Smart NR scene evidence.");
            }
            else if (!condition.Fresh)
            {
                score -= 15;
                constraints.Add($"frontend-dsp-scene-{condition.Status}");
                actions.Add("Wait for fresh frontend spectrum evidence before tuning NR/AGC decisions.");
            }
        }

        if (condition.SourceClockSkewMs is > 5000)
        {
            score -= 25;
            constraints.Add("frontend-clock-skew");
            actions.Add("Fix client/host clock skew before trusting scene age or freshness.");
        }

        if (condition.RuntimeAligned == true)
            evidence.Add("smart-nr-runtime-aligned");
        else if (condition.RuntimeAligned == false)
        {
            if (string.Equals(condition.RuntimeAlignmentStatus, "apply-pending", StringComparison.OrdinalIgnoreCase))
            {
                score -= 10;
                constraints.Add("smart-nr-apply-pending");
                actions.Add("Wait for the DSP apply latch before judging the active NR mode by ear.");
            }
            else
            {
                score -= 25;
                constraints.Add("smart-nr-runtime-misaligned");
                actions.Add("Reapply Smart NR or inspect the DSP apply path before tuning weak-signal NR.");
            }
        }
        else
        {
            score -= 8;
            constraints.Add("smart-nr-profile-unmapped");
        }

        if (condition.HeldByRxChain == true)
        {
            score -= 20;
            constraints.Add("smart-nr-held-by-rx-chain");
            actions.Add("Resolve RX-chain health before increasing NR aggressiveness.");
        }

        if (condition.RxChainScore is { } rxScore)
        {
            evidence.Add($"rx-chain-score-{rxScore}");
            if (rxScore < 60)
            {
                score -= 25;
                constraints.Add("rx-chain-health-poor");
            }
            else if (rxScore < 80)
            {
                score -= 10;
                constraints.Add("rx-chain-health-needs-attention");
            }
        }

        if (string.Equals(condition.RxChainTone, "protect", StringComparison.OrdinalIgnoreCase))
        {
            score -= 15;
            constraints.Add("rx-chain-protect");
        }
        else if (string.Equals(condition.RxChainTone, "optimize", StringComparison.OrdinalIgnoreCase))
        {
            score -= 5;
            constraints.Add("rx-chain-optimize");
        }

        if (condition.CoherentSubthresholdSignal == true)
            evidence.Add("coherent-subthreshold-signal");
        if (condition.AdjacentNoiseUsable == true)
        {
            evidence.Add("adjacent-noise-profile-usable");
            tools.Add("adjacent-noise-profile");
        }
        else if (condition.AdjacentNoiseBins is > 0)
        {
            evidence.Add("adjacent-noise-profile-sampled");
            tools.Add("adjacent-noise-profile");
        }
        if (condition.CoherentMaxSnrDb is { } coherentSnr)
            evidence.Add($"coherent-snr-{coherentSnr:0.0}db");
        if (condition.ImpulsivePct is > 10.0)
            constraints.Add("impulsive-scene");

        foreach (var candidate in externalCandidates)
            tools.Add($"external-post-demod-bakeoff:{candidate.Id}");

        if (runtimeEvidence is not null)
        {
            evidence.Add($"runtime-evidence-{runtimeEvidence.Status}");
            if (runtimeEvidence.RxMetersFresh)
                evidence.Add("rx-meters-fresh");
            if (runtimeEvidence.AudioFresh)
                evidence.Add("final-audio-fresh");
            if (runtimeEvidence.AgcGainDb is { } agcGain)
                evidence.Add($"agc-gain-{agcGain:0.0}db");
            if (runtimeEvidence.AdcHeadroomDb is { } headroom)
                evidence.Add($"adc-headroom-{headroom:0.0}db");
            if (runtimeEvidence.AudioRmsDbfs is { } rms)
                evidence.Add($"audio-rms-{rms:0.0}dbfs");
            if (runtimeEvidence.AudioPeakDbfs is { } peak)
                evidence.Add($"audio-peak-{peak:0.0}dbfs");
            var rxAudioLevelerExperimental = runtimeEvidence.RxAudioLevelerExperimental == true;
            if (!string.IsNullOrWhiteSpace(runtimeEvidence.RxAudioLevelerActiveProfile) &&
                (rxAudioLevelerExperimental ||
                 !string.Equals(runtimeEvidence.RxAudioLevelerActiveProfile, "current", StringComparison.OrdinalIgnoreCase)))
            {
                evidence.Add($"rx-audio-leveler-{runtimeEvidence.RxAudioLevelerActiveProfile}");
            }

            if (rxAudioLevelerExperimental)
            {
                evidence.Add("rx-audio-leveler-experimental-profile");
                actions.Add("Capture matched current-profile and candidate-profile live diagnostics traces before promoting RX leveler changes.");
            }
            if (runtimeEvidence.RxAudioLevelerControlRmsValid == true)
                evidence.Add("rx-audio-leveler-control-rms-evidence");

            var txOutputHeadroomExperimental = runtimeEvidence.TxOutputHeadroomExperimental == true;
            if (!string.IsNullOrWhiteSpace(runtimeEvidence.TxOutputHeadroomRequestedProfile) &&
                (txOutputHeadroomExperimental ||
                 !string.Equals(runtimeEvidence.TxOutputHeadroomRequestedProfile, "current", StringComparison.OrdinalIgnoreCase)))
            {
                evidence.Add($"tx-output-headroom-requested-{runtimeEvidence.TxOutputHeadroomRequestedProfile}");
            }
            if (!string.IsNullOrWhiteSpace(runtimeEvidence.TxOutputHeadroomActiveProfile) &&
                !string.Equals(runtimeEvidence.TxOutputHeadroomActiveProfile, "current", StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add($"tx-output-headroom-active-{runtimeEvidence.TxOutputHeadroomActiveProfile}");
            }
            if (txOutputHeadroomExperimental)
            {
                evidence.Add("tx-output-headroom-experimental-profile");
                actions.Add("Keep the TX output headroom profile opt-in until TX voice, two-tone, PureSignal, and on-air G2 evidence are reviewed.");
            }
            if (runtimeEvidence.TxOutputHeadroomPureSignalBypassed == true)
            {
                evidence.Add("tx-output-headroom-puresignal-bypassed");
                actions.Add("PureSignal is armed, so the TX output headroom candidate is bypassed by design; collect separate PureSignal-safe evidence before promotion.");
            }

            if (!runtimeEvidence.AudioFresh)
            {
                score -= 10;
                constraints.Add("final-audio-not-fresh");
                actions.Add("Wait for fresh final RX audio before judging NR/AGC or external-engine quality.");
            }

            if (!runtimeEvidence.RxMetersFresh)
            {
                score -= 5;
                constraints.Add("rx-meters-not-fresh");
                actions.Add("Wait for fresh RXA meter evidence before using AGC gain/headroom as benchmark context.");
            }

            switch (runtimeEvidence.Status)
            {
                case "audio-clipping-risk":
                    score -= 20;
                    constraints.Add("final-audio-clipping-risk");
                    actions.Add("Reduce RX leveler boost, front-end gain, or plugin output before collecting DSP acceptance audio.");
                    break;
                case "audio-muted-by-squelch":
                    score -= 12;
                    constraints.Add("final-audio-muted-by-squelch");
                    actions.Add("Open, lower, or disable squelch before treating silence as weak-signal preservation evidence.");
                    break;
                case "audio-monitor-backlog":
                    score -= 10;
                    constraints.Add("monitor-audio-backlog");
                    actions.Add("Drain or stop local playback monitor injection before judging live RX audio fidelity.");
                    break;
                case "audio-tx-monitor":
                    constraints.Add("tx-monitor-audio-active");
                    actions.Add("Disable TX monitor when collecting receive-side NR/AGC audio evidence.");
                    break;
                case "adc-headroom-low":
                    score -= 15;
                    constraints.Add("adc-headroom-low");
                    actions.Add("Add attenuation or reduce preamp/front-end gain before evaluating NR/AGC improvements.");
                    break;
            }
        }

        score = Math.Clamp(score, 0, 100);
        string status = Status(condition, constraints, score);
        string tone = QualityTone(status, condition, score);
        var nextBenchmarkScenarios = DspBenchmarkPlanCatalog.NextScenarioIds(condition);
        var externalBakeoff = ExternalEngineBakeoffReadiness(condition, runtimeEvidence, externalCandidates);
        tools.Add("external-engine-live-bakeoff-watch");
        if (externalBakeoff.Ready)
        {
            evidence.Add("ready-for-external-engine-bakeoff");
            actions.Add("Capture a manually tuned G2 NR-off/current-Zeus baseline and an opt-in RX Audio Suite candidate trace before promoting any replacement DSP path.");
        }

        bool ready = score >= 85
            && condition.WdspActive
            && condition.WdspNativeLoadable
            && condition.Available
            && condition.Fresh
            && condition.RuntimeAligned != false
            && !constraints.Any(IsHardConstraint);

        if (ready)
        {
            actions.Add("Capture a G2 live benchmark run and compare against the offline fixture baseline before changing any default.");
            evidence.Add("ready-for-g2-live-benchmark");
        }

        if (actions.Count == 0)
            actions.Add("Keep collecting live diagnostics and run the offline benchmark harness before making DSP tuning changes.");

        return new DspLiveDiagnosticsDto(
            SchemaVersion: 1,
            GeneratedUtc: DateTimeOffset.UtcNow,
            Status: status,
            QualityTone: tone,
            ReadinessScore: score,
            ReadyForLiveBenchmark: ready,
            ReadyForExternalEngineBakeoff: externalBakeoff.Ready,
            ExternalEngineBakeoffStatus: externalBakeoff.Status,
            ExternalEngineBakeoffConstraints: externalBakeoff.Constraints,
            RolloutGate: "opt-in-only-until-benchmark-and-g2-on-air-acceptance",
            WdspActive: condition.WdspActive,
            WdspNativeLoadable: condition.WdspNativeLoadable,
            WdspEmnrPost2Available: condition.WdspEmnrPost2Available,
            WdspNr4SbnrAvailable: condition.WdspNr4SbnrAvailable,
            Nr4Readiness: condition.Nr4Readiness,
            FrontendSceneAvailable: condition.Available,
            FrontendSceneStatus: condition.Status,
            FrontendSceneFresh: condition.Fresh,
            FrontendSceneStale: condition.Stale,
            FrontendSceneAgeMs: condition.AgeMs,
            FrontendTopPeaks: condition.TopPeaks,
            FrontendAdjacentNoiseUsable: condition.AdjacentNoiseUsable,
            FrontendAdjacentNoiseBins: condition.AdjacentNoiseBins,
            FrontendAdjacentNoiseLeftBins: condition.AdjacentNoiseLeftBins,
            FrontendAdjacentNoiseRightBins: condition.AdjacentNoiseRightBins,
            FrontendAdjacentNoiseFloorDb: condition.AdjacentNoiseFloorDb,
            FrontendAdjacentNoiseP10Db: condition.AdjacentNoiseP10Db,
            FrontendAdjacentNoiseP50Db: condition.AdjacentNoiseP50Db,
            FrontendAdjacentNoiseP90Db: condition.AdjacentNoiseP90Db,
            FrontendAdjacentNoiseLeftFloorDb: condition.AdjacentNoiseLeftFloorDb,
            FrontendAdjacentNoiseRightFloorDb: condition.AdjacentNoiseRightFloorDb,
            FrontendAdjacentNoiseSlopeDbPerKhz: condition.AdjacentNoiseSlopeDbPerKhz,
            FrontendAdjacentNoiseRejectedPct: condition.AdjacentNoiseRejectedPct,
            SmartNrProfile: condition.Profile,
            ExpectedNrMode: condition.ExpectedNrMode,
            RuntimeAligned: condition.RuntimeAligned,
            RuntimeAlignmentStatus: condition.RuntimeAlignmentStatus,
            RequestedNrMode: condition.RequestedNrMode,
            EffectiveNrMode: condition.EffectiveNrMode,
            HeldByRxChain: condition.HeldByRxChain,
            RxChainScore: condition.RxChainScore,
            RxChainTone: condition.RxChainTone,
            RxChainLabel: condition.RxChainLabel,
            RxChainFilterLowHz: condition.RxChain.FilterLowHz,
            RxChainFilterHighHz: condition.RxChain.FilterHighHz,
            RxChainFilterWidthHz: condition.RxChain.FilterWidthHz,
            RxChainFilterPresetName: condition.RxChain.FilterPresetName,
            RadioVfoHz: radioState?.VfoHz,
            RadioLoHz: radioState?.RadioLoHz,
            RadioMode: radioState?.Mode.ToString(),
            RadioCtunEnabled: radioState?.CtunEnabled,
            RadioSampleRate: radioState?.SampleRate,
            RuntimeEvidence: runtimeEvidence,
            Evidence: Unique(evidence),
            Constraints: Unique(constraints),
            RecommendedActions: Unique(actions),
            CandidateTools: Unique(tools),
            BenchmarkPlanEndpoint: "/api/dsp/benchmark-plan",
            BenchmarkScenarioCount: benchmarkPlan.Scenarios.Length,
            NextBenchmarkScenarios: nextBenchmarkScenarios,
            BenchmarkAcceptanceGates: benchmarkPlan.GlobalAcceptanceGates,
            ExternalEngineCandidates: externalCandidates,
            DiagnosticRecommendation: Recommendation(status, condition, actions));
    }

    private static string Status(SmartNrConditionDto condition, List<string> constraints, int score)
    {
        if (!condition.WdspNativeLoadable) return "wdsp-native-unloadable";
        if (!condition.WdspActive) return "dsp-engine-unavailable";
        if (constraints.Contains("frontend-clock-skew")) return "frontend-clock-skew";
        if (condition.Stale || constraints.Contains("frontend-dsp-scene-stale")) return "frontend-scene-stale";
        if (!condition.Available) return "frontend-scene-missing";
        if (constraints.Any(c => c is "nr4-sbnr-exports-missing")) return "nr-capability-limited";
        if (constraints.Contains("smart-nr-apply-pending")) return "smart-nr-apply-pending";
        if (constraints.Contains("smart-nr-runtime-misaligned")) return "smart-nr-runtime-misaligned";
        if (constraints.Contains("rx-chain-protect")) return "rx-chain-protect";
        if (constraints.Contains("final-audio-clipping-risk")) return "final-audio-clipping-risk";
        if (constraints.Contains("final-audio-not-fresh")) return "final-audio-not-fresh";
        if (constraints.Contains("adc-headroom-low")) return "adc-headroom-low";
        if (constraints.Contains("final-audio-muted-by-squelch")) return "final-audio-muted-by-squelch";
        if (score >= 85) return "ready-for-live-benchmark";
        if (score >= 65) return "verify-before-tuning";
        return "diagnostics-not-ready";
    }

    private static string QualityTone(string status, SmartNrConditionDto condition, int score)
    {
        if (status is "wdsp-native-unloadable" or "dsp-engine-unavailable" or "nr-capability-limited"
            or "rx-chain-protect" or "final-audio-clipping-risk" or "final-audio-not-fresh"
            or "adc-headroom-low" or "diagnostics-not-ready")
            return "protect";
        if (!condition.Available || status is "frontend-scene-missing")
            return "standby";
        if (score >= 85)
            return "ready";
        return "verify";
    }

    private static string Recommendation(string status, SmartNrConditionDto condition, List<string> actions)
    {
        return status switch
        {
            "wdsp-native-unloadable" => "WDSP native loading is unavailable; fix the native runtime before modernizing DSP behavior.",
            "dsp-engine-unavailable" => "WDSP is not the active engine; connect the G2 and verify WDSP lifecycle before judging NR/AGC quality.",
            "frontend-clock-skew" => "Frontend DSP scene timestamps are in the future; fix clock skew before trusting live scene diagnostics.",
            "frontend-scene-stale" => "Frontend scene evidence is stale; refresh the client before using Smart NR recommendations.",
            "frontend-scene-missing" => "No frontend DSP scene is available; open Signal Intelligence/Smart NR so backend diagnostics can correlate scene evidence with WDSP state.",
            "nr-capability-limited" => "The requested Smart NR path needs native WDSP exports that are not available; rebuild/update WDSP before evaluating that mode.",
            "smart-nr-apply-pending" => condition.RuntimeAlignmentRecommendation,
            "smart-nr-runtime-misaligned" => condition.RuntimeAlignmentRecommendation,
            "rx-chain-protect" => condition.RxChainRecommendation ?? "RX-chain health is in protect mode; resolve ADC/AGC/attenuator posture before increasing DSP aggressiveness.",
            "final-audio-clipping-risk" => "Final RX audio is near full scale; reduce gain or plugin output before collecting DSP acceptance evidence.",
            "final-audio-not-fresh" => "Final RX audio is missing or stale; restore the DSP/audio publish path before evaluating live DSP quality.",
            "adc-headroom-low" => "ADC headroom is low; stabilize front-end gain and attenuation before evaluating NR/AGC improvements.",
            "final-audio-muted-by-squelch" => "Final audio is muted by squelch; open or lower squelch before using silence as weak-signal evidence.",
            "ready-for-live-benchmark" => "Live diagnostics are aligned enough for a G2 benchmark capture; keep the new DSP path opt-in until benchmark and on-air evidence prove it.",
            _ => actions[0],
        };
    }

    private static bool IsHardConstraint(string constraint) =>
        constraint is "wdsp-native-unloadable"
            or "wdsp-inactive"
            or "frontend-dsp-scene-missing"
            or "frontend-dsp-scene-stale"
            or "frontend-clock-skew"
            or "nr4-sbnr-exports-missing"
            or "smart-nr-runtime-misaligned"
            or "rx-chain-protect"
            or "final-audio-not-fresh"
            or "final-audio-clipping-risk"
            or "adc-headroom-low";

    private static ExternalEngineBakeoffReadinessResult ExternalEngineBakeoffReadiness(
        SmartNrConditionDto condition,
        DspLiveRuntimeEvidenceDto? runtimeEvidence,
        DspExternalEngineCandidateDto[] externalCandidates)
    {
        var constraints = new List<string>();
        if (!condition.WdspNativeLoadable)
            constraints.Add("wdsp-native-unloadable");
        if (!condition.WdspActive)
            constraints.Add("wdsp-inactive");

        if (!condition.Available)
            constraints.Add("frontend-dsp-scene-missing");
        else if (condition.Stale || !condition.Fresh)
            constraints.Add("frontend-dsp-scene-not-fresh");

        if (condition.RuntimeAligned == false
            && !string.Equals(condition.RuntimeAlignmentStatus, "apply-pending", StringComparison.OrdinalIgnoreCase))
            constraints.Add("smart-nr-runtime-misaligned");

        if (condition.HeldByRxChain == true)
            constraints.Add("smart-nr-held-by-rx-chain");
        if (condition.RxChainScore is < 60)
            constraints.Add("rx-chain-health-poor");
        if (string.Equals(condition.RxChainTone, "protect", StringComparison.OrdinalIgnoreCase))
            constraints.Add("rx-chain-protect");

        if (externalCandidates.Length == 0)
        {
            constraints.Add("external-engine-catalog-missing");
        }
        else
        {
            if (externalCandidates.Any(static candidate =>
                    !string.Equals(candidate.DefaultState, "off", StringComparison.OrdinalIgnoreCase)))
                constraints.Add("external-engine-default-not-off");

            bool hasPostDemodRxCandidate = externalCandidates.Any(static candidate =>
                candidate.AllowedSignalPaths.Any(static path =>
                    path.Contains("post-demod-rx-audio", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("rx-audio-suite", StringComparison.OrdinalIgnoreCase)));
            if (!hasPostDemodRxCandidate)
                constraints.Add("external-engine-post-demod-route-missing");
        }

        if (runtimeEvidence is null)
        {
            constraints.Add("runtime-evidence-missing");
        }
        else
        {
            if (!runtimeEvidence.RxMetersFresh)
                constraints.Add("rx-meters-not-fresh");
            if (!runtimeEvidence.AudioFresh)
                constraints.Add("final-audio-not-fresh");
            if (runtimeEvidence.TxMonitorRequested)
                constraints.Add("tx-monitor-audio-active");

            switch (runtimeEvidence.Status)
            {
                case "audio-clipping-risk":
                    constraints.Add("final-audio-clipping-risk");
                    break;
                case "audio-muted-by-squelch":
                    constraints.Add("final-audio-muted-by-squelch");
                    break;
                case "audio-monitor-backlog":
                    constraints.Add("monitor-audio-backlog");
                    break;
                case "audio-tx-monitor":
                    constraints.Add("tx-monitor-audio-active");
                    break;
                case "adc-headroom-low":
                    constraints.Add("adc-headroom-low");
                    break;
            }
        }

        var unique = Unique(constraints);
        return unique.Length == 0
            ? new(true, "ready-for-external-engine-bakeoff", [])
            : new(false, "external-engine-bakeoff-preflight-required", unique);
    }

    private static bool ModeEquals(string? left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string[] Unique(IEnumerable<string> values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private sealed record ExternalEngineBakeoffReadinessResult(
        bool Ready,
        string Status,
        string[] Constraints);
}
