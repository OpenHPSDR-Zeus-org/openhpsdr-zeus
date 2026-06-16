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
        DspLiveRuntimeEvidenceDto? runtimeEvidence = null)
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

        if (condition.WdspNr5SpnrAvailable)
            evidence.Add("nr5-spnr-available");
        else if (ModeEquals(condition.ExpectedNrMode, "Nr5") || ModeEquals(condition.RequestedNrMode, "Nr5"))
        {
            score -= 25;
            constraints.Add("nr5-spnr-exports-missing");
            actions.Add("Rebuild or install WDSP with NR5/SPNR exports before evaluating NR5 weak-signal behavior.");
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
        if (condition.CoherentMaxSnrDb is { } coherentSnr)
            evidence.Add($"coherent-snr-{coherentSnr:0.0}db");
        if (condition.ImpulsivePct is > 10.0)
            constraints.Add("impulsive-scene");

        var nr5 = condition.Nr5SpnrDiagnostics;
        if (ModeEquals(condition.EffectiveNrMode, "Nr5") || nr5 is not null)
        {
            tools.Add("nr5-spnr-diagnostics");
            if (nr5 is null)
            {
                score -= 10;
                constraints.Add("nr5-diagnostics-missing");
                actions.Add("Collect NR5/SPNR diagnostics before tuning NR5 constants.");
            }
            else
            {
                evidence.Add($"nr5-learned-frames-{nr5.LearnedFrames}");
                evidence.Add($"nr5-signal-confidence-{nr5.SignalConfidence:0.000}");
                evidence.Add($"nr5-weak-signal-memory-{nr5.WeakSignalMemory:0.000}");
                if (nr5.LearnedFrames < 20)
                {
                    score -= 10;
                    constraints.Add("nr5-learning");
                    actions.Add("Let NR5 learn more frames before evaluating weak-signal preservation.");
                }
                if (nr5.SignalConfidence < 0.10 && condition.CoherentSubthresholdSignal == true)
                {
                    score -= 10;
                    constraints.Add("nr5-low-confidence-on-coherent-scene");
                    actions.Add("Tune NR5 coherence/ridge protection against the benchmark fixture before raising aggressiveness.");
                }
                if (nr5.AgcGate < 0.10 && condition.CoherentSubthresholdSignal == true)
                {
                    score -= 8;
                    constraints.Add("nr5-agc-gate-closed-on-coherent-scene");
                }
                if (nr5.FloorReductionDb > 20.0 && nr5.SignalConfidence < 0.20)
                {
                    score -= 8;
                    constraints.Add("nr5-floor-pressure-high");
                }
            }
        }

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
        var nr5Tuning = Nr5TuningReadiness(condition, runtimeEvidence);
        if (nr5Tuning.Status != "nr5-not-active")
            tools.Add("nr5-live-tuning-watch");
        if (nr5Tuning.Ready)
            evidence.Add("ready-for-nr5-live-tuning");

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
            ReadyForNr5Tuning: nr5Tuning.Ready,
            Nr5TuningStatus: nr5Tuning.Status,
            Nr5TuningConstraints: nr5Tuning.Constraints,
            RolloutGate: "opt-in-only-until-benchmark-and-g2-on-air-acceptance",
            WdspActive: condition.WdspActive,
            WdspNativeLoadable: condition.WdspNativeLoadable,
            WdspEmnrPost2Available: condition.WdspEmnrPost2Available,
            WdspNr4SbnrAvailable: condition.WdspNr4SbnrAvailable,
            WdspNr5SpnrAvailable: condition.WdspNr5SpnrAvailable,
            Nr4Readiness: condition.Nr4Readiness,
            Nr5Readiness: condition.Nr5Readiness,
            FrontendSceneAvailable: condition.Available,
            FrontendSceneStatus: condition.Status,
            FrontendSceneFresh: condition.Fresh,
            FrontendSceneStale: condition.Stale,
            FrontendSceneAgeMs: condition.AgeMs,
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
            Nr5SpnrDiagnostics: nr5,
            Nr5SignalConfidence: nr5?.SignalConfidence,
            Nr5AgcGate: nr5?.AgcGate,
            Nr5SignalProbability: nr5?.SignalProbability,
            Nr5TextureFill: nr5?.TextureFill,
            Nr5MaskSmoothing: nr5?.MaskSmoothing,
            Nr5WeakSignalMemory: nr5?.WeakSignalMemory,
            Nr5MeanGain: nr5?.MeanGain,
            Nr5FloorReductionDb: nr5?.FloorReductionDb,
            Nr5OutputPeakDbfs: nr5?.OutputPeakDbfs,
            Nr5PeakEvidence: nr5?.PeakEvidence,
            Nr5PeakLimitDbfs: nr5?.PeakLimitDbfs,
            Nr5PeakReductionDb: nr5?.PeakReductionDb,
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
        if (constraints.Any(c => c is "nr4-sbnr-exports-missing" or "nr5-spnr-exports-missing")) return "nr-capability-limited";
        if (constraints.Contains("smart-nr-apply-pending")) return "smart-nr-apply-pending";
        if (constraints.Contains("smart-nr-runtime-misaligned")) return "smart-nr-runtime-misaligned";
        if (constraints.Contains("rx-chain-protect")) return "rx-chain-protect";
        if (constraints.Contains("final-audio-clipping-risk")) return "final-audio-clipping-risk";
        if (constraints.Contains("final-audio-not-fresh")) return "final-audio-not-fresh";
        if (constraints.Contains("adc-headroom-low")) return "adc-headroom-low";
        if (constraints.Contains("final-audio-muted-by-squelch")) return "final-audio-muted-by-squelch";
        if (constraints.Any(c => c.StartsWith("nr5-", StringComparison.Ordinal))) return "nr5-needs-benchmark";
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
            "nr5-needs-benchmark" => "NR5/SPNR is active or requested but diagnostics show a tuning constraint; use the synthetic benchmark fixtures before changing constants.",
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
            or "nr5-spnr-exports-missing"
            or "smart-nr-runtime-misaligned"
            or "rx-chain-protect"
            or "final-audio-not-fresh"
            or "final-audio-clipping-risk"
            or "adc-headroom-low";

    private static Nr5LiveTuningReadiness Nr5TuningReadiness(
        SmartNrConditionDto condition,
        DspLiveRuntimeEvidenceDto? runtimeEvidence)
    {
        bool nr5Relevant = ModeEquals(condition.RequestedNrMode, "Nr5")
            || ModeEquals(condition.EffectiveNrMode, "Nr5")
            || condition.Nr5SpnrDiagnostics is not null;
        if (!nr5Relevant)
            return new(false, "nr5-not-active", ["nr5-not-active"]);

        var constraints = new List<string>();
        if (!condition.WdspNativeLoadable)
            constraints.Add("wdsp-native-unloadable");
        if (!condition.WdspActive)
            constraints.Add("wdsp-inactive");
        if (!condition.WdspNr5SpnrAvailable)
            constraints.Add("nr5-spnr-exports-missing");
        if (!ModeEquals(condition.RequestedNrMode, "Nr5"))
            constraints.Add("nr5-not-requested");
        if (!ModeEquals(condition.EffectiveNrMode, "Nr5"))
            constraints.Add("nr5-not-effective");
        if (condition.RuntimeAligned == false
            && !string.Equals(condition.RuntimeAlignmentStatus, "apply-pending", StringComparison.OrdinalIgnoreCase))
            constraints.Add("smart-nr-runtime-misaligned");

        var nr5 = condition.Nr5SpnrDiagnostics;
        if (nr5 is null)
        {
            constraints.Add("nr5-diagnostics-missing");
        }
        else
        {
            if (!nr5.Run)
                constraints.Add("nr5-not-running");
            if (nr5.LearnedFrames < 20)
                constraints.Add("nr5-learning");
            if (!nr5.AgcRun)
                constraints.Add("nr5-agc-disabled");
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
            ? new(true, "ready-for-nr5-live-tuning", [])
            : new(false, "nr5-tuning-preflight-required", unique);
    }

    private static bool ModeEquals(string? left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string[] Unique(IEnumerable<string> values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private sealed record Nr5LiveTuningReadiness(
        bool Ready,
        string Status,
        string[] Constraints);
}
