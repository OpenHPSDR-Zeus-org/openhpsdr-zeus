// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

internal static class DspRxAudioLevelerFixtureBenchmark
{
    private const int BlockSamples = 1024;

    public static RxAudioLevelerFixtureBenchmarkDto Build()
    {
        var scenarios = new[]
        {
            BuildScenario(
                "ssb-syllable-step",
                "SSB syllable level steps",
                "Alternating weak and strong speech-like audio after a steady talker pre-roll.",
                SyllableStepBlocks(),
                CandidatePassesSyllableStep),
            BuildScenario(
                "near-target-speech",
                "Near-target speech stability",
                "Already-readable active speech must not pick up extra candidate loudness movement.",
                NearTargetSpeechBlocks(),
                CandidatePassesNearTargetSpeech),
            BuildScenario(
                "live-crest-headroom",
                "Live crest headroom guard",
                "Moderate active speech with sparse crest peaks must keep extra headroom without final limiting.",
                LiveCrestHeadroomBlocks(),
                CandidatePassesLiveCrestHeadroom),
            BuildScenario(
                "sustained-weak-speech",
                "Sustained weak speech catch-up",
                "Weak audio after a stronger speech pre-roll must still reach the RX listen level.",
                SustainedWeakSpeechBlocks(),
                CandidatePassesSustainedWeakSpeech),
            BuildScenario(
                "strong-after-weak",
                "Strong signal after weak-signal boost",
                "A hot block after weak-signal boost must not blast or lean on final limiting.",
                StrongAfterWeakBlocks(),
                CandidatePassesStrongAfterWeak),
        };

        int candidatePassCount = scenarios.Count(static scenario => scenario.Comparison.CandidatePasses);
        var readiness = new RxAudioLevelerFixtureReadinessDto(
            ScenarioCount: scenarios.Length,
            CandidatePassCount: candidatePassCount,
            CandidateFailCount: scenarios.Length - candidatePassCount,
            CandidateAllGatesPass: candidatePassCount == scenarios.Length,
            ExperimentalOptIn: true,
            DefaultBehaviorChanged: false,
            ReadyForLiveAb: candidatePassCount == scenarios.Length,
            Recommendation: candidatePassCount == scenarios.Length
                ? "candidate-ready-for-live-g2-ab"
                : "fixture-gates-block-live-ab");

        return new RxAudioLevelerFixtureBenchmarkDto(
            SchemaVersion: 1,
            EvidenceKind: "rx-audio-leveler-profile-fixture",
            DefaultProfile: DspPipelineService.RxAudioLevelerProfileId(DspPipelineService.RxAudioLevelerProfile.Current),
            CandidateProfile: DspPipelineService.RxAudioLevelerProfileId(DspPipelineService.RxAudioLevelerProfile.StableSpeechCandidate),
            ExperimentalOptIn: true,
            DefaultBehaviorChanged: false,
            Readiness: readiness,
            AcceptanceGates:
            [
                "current profile remains byte-for-byte equivalent to the default overload",
                "candidate bounds syllable gain movement while preserving weak syllables without final limiting",
                "candidate does not add pumping to already-readable active speech",
                "candidate preserves additional crest headroom on live-like active speech without final limiting",
                "candidate reaches weak speech target after sustained weak audio",
                "candidate handles strong audio after weak boost without clipping or limiter pressure",
            ],
            Scenarios: scenarios);
    }

    private static RxAudioLevelerFixtureScenarioDto BuildScenario(
        string id,
        string name,
        string description,
        IReadOnlyList<LevelerFixtureBlock> blocks,
        Func<RxAudioLevelerFixtureProfileMetricsDto, RxAudioLevelerFixtureProfileMetricsDto, bool> candidatePasses)
    {
        var current = RunProfile(DspPipelineService.RxAudioLevelerProfile.Current, blocks);
        var candidate = RunProfile(DspPipelineService.RxAudioLevelerProfile.StableSpeechCandidate, blocks);
        var comparison = new RxAudioLevelerFixtureComparisonDto(
            CandidatePasses: candidatePasses(current, candidate),
            MaxGainDeltaReductionDb: Round(current.MaxAbsGainDeltaDb - candidate.MaxAbsGainDeltaDb),
            AppliedGainMovementReductionDb: Round(current.AppliedGainMovementDb - candidate.AppliedGainMovementDb),
            OutputRmsMovementReductionDb: Round(current.OutputRmsMovementDb - candidate.OutputRmsMovementDb),
            WeakOutputAverageDeltaDb: Round(candidate.WeakOutputAverageDbfs - current.WeakOutputAverageDbfs),
            CandidatePeakRegressionDb: Round(candidate.MaxOutputPeakDbfs - current.MaxOutputPeakDbfs),
            CandidateLimitBlockDelta: candidate.OutputLimitedBlockCount - current.OutputLimitedBlockCount);

        return new RxAudioLevelerFixtureScenarioDto(
            Id: id,
            ScenarioId: id,
            Name: name,
            Description: description,
            SignalPath: "RX audio post-demod",
            FixtureStatus: "in-process-fixture-ready",
            Current: current,
            Candidate: candidate,
            Comparison: comparison,
            Comparisons:
            [
                BuildComparisonEntry("current-zeus", current, passed: true),
                BuildComparisonEntry("candidate-under-test", candidate, passed: comparison.CandidatePasses),
            ]);
    }

    private static RxAudioLevelerFixtureComparisonEntryDto BuildComparisonEntry(
        string comparisonId,
        RxAudioLevelerFixtureProfileMetricsDto metrics,
        bool passed) =>
        new(
            ComparisonId: comparisonId,
            Profile: metrics.Profile,
            Metrics: new RxAudioLevelerFixtureMetricMapDto(
                MaxAbsGainDeltaDb: metrics.MaxAbsGainDeltaDb,
                AppliedGainMovementDb: metrics.AppliedGainMovementDb,
                OutputRmsMovementDb: metrics.OutputRmsMovementDb,
                WeakOutputAverageDbfs: metrics.WeakOutputAverageDbfs,
                MaxOutputPeakDbfs: metrics.MaxOutputPeakDbfs,
                OutputLimitedBlockCount: metrics.OutputLimitedBlockCount,
                PeakLimitedBlockCount: metrics.PeakLimitedBlockCount,
                BlocksToTarget: metrics.BlocksToTarget,
                ControlRmsValidBlockCount: metrics.ControlRmsValidBlockCount,
                MaxControlRmsHangDb: metrics.MaxControlRmsHangDb),
            Gates:
            [
                new RxAudioLevelerFixtureGateDto("rx-audio-leveler-profile-fixture", passed),
            ]);

    private static RxAudioLevelerFixtureProfileMetricsDto RunProfile(
        DspPipelineService.RxAudioLevelerProfile profile,
        IReadOnlyList<LevelerFixtureBlock> blocks)
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        var samples = new float[BlockSamples];

        int measuredBlocks = 0;
        int weakMeasuredBlocks = 0;
        int outputLimitedBlocks = 0;
        int peakLimitedBlocks = 0;
        int boostSlewLimitedBlocks = 0;
        int controlRmsValidBlocks = 0;
        int blocksToTarget = -1;
        double maxAbsGainDeltaDb = 0.0;
        double minGainDb = double.PositiveInfinity;
        double maxGainDb = double.NegativeInfinity;
        double minOutputRmsDbfs = double.PositiveInfinity;
        double maxOutputRmsDbfs = double.NegativeInfinity;
        double maxOutputPeakDbfs = double.NegativeInfinity;
        double weakOutputRmsDbfsSum = 0.0;
        double maxControlRmsHangDb = 0.0;

        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            var block = blocks[blockIndex];
            block.Fill(samples);
            DspPipelineService.ApplyRxAudioLeveler(samples, ref state, profile);

            if (!block.Measure)
                continue;

            measuredBlocks++;
            if (state.OutputLimited)
                outputLimitedBlocks++;
            if (state.PeakLimited)
                peakLimitedBlocks++;
            if (state.BoostSlewLimited)
                boostSlewLimitedBlocks++;
            if (state.ControlRmsValid)
                controlRmsValidBlocks++;

            maxAbsGainDeltaDb = Math.Max(maxAbsGainDeltaDb, Math.Abs(state.GainDeltaDb));
            minGainDb = Math.Min(minGainDb, state.AppliedGainDb);
            maxGainDb = Math.Max(maxGainDb, state.AppliedGainDb);
            minOutputRmsDbfs = Math.Min(minOutputRmsDbfs, state.OutputRmsDbfs);
            maxOutputRmsDbfs = Math.Max(maxOutputRmsDbfs, state.OutputRmsDbfs);
            maxOutputPeakDbfs = Math.Max(maxOutputPeakDbfs, state.OutputPeakDbfs);
            maxControlRmsHangDb = Math.Max(maxControlRmsHangDb, state.ControlRmsHangDb);

            if (block.Weak)
            {
                weakMeasuredBlocks++;
                weakOutputRmsDbfsSum += state.OutputRmsDbfs;
            }

            if (blocksToTarget < 0 &&
                state.OutputRmsDbfs is >= -20.5 and <= -16.5)
            {
                blocksToTarget = measuredBlocks;
            }
        }

        double appliedGainMovementDb = measuredBlocks > 0 && double.IsFinite(minGainDb) && double.IsFinite(maxGainDb)
            ? maxGainDb - minGainDb
            : 0.0;
        double outputRmsMovementDb = measuredBlocks > 0 && double.IsFinite(minOutputRmsDbfs) && double.IsFinite(maxOutputRmsDbfs)
            ? maxOutputRmsDbfs - minOutputRmsDbfs
            : 0.0;
        double weakOutputAverageDbfs = weakMeasuredBlocks > 0
            ? weakOutputRmsDbfsSum / weakMeasuredBlocks
            : 0.0;

        return new RxAudioLevelerFixtureProfileMetricsDto(
            Profile: DspPipelineService.RxAudioLevelerProfileId(profile),
            MeasurementBlockCount: measuredBlocks,
            WeakMeasurementBlockCount: weakMeasuredBlocks,
            BlocksToTarget: blocksToTarget,
            FinalGainDb: Round(state.GainDb),
            FinalOutputRmsDbfs: Round(state.OutputRmsDbfs),
            FinalOutputPeakDbfs: Round(state.OutputPeakDbfs),
            MaxAbsGainDeltaDb: Round(maxAbsGainDeltaDb),
            AppliedGainMovementDb: Round(appliedGainMovementDb),
            OutputRmsMovementDb: Round(outputRmsMovementDb),
            WeakOutputAverageDbfs: Round(weakOutputAverageDbfs),
            MaxOutputPeakDbfs: Round(maxOutputPeakDbfs),
            OutputLimitedBlockCount: outputLimitedBlocks,
            PeakLimitedBlockCount: peakLimitedBlocks,
            BoostSlewLimitedBlockCount: boostSlewLimitedBlocks,
            ControlRmsValidBlockCount: controlRmsValidBlocks,
            MaxControlRmsHangDb: Round(maxControlRmsHangDb));
    }

    private static bool CandidatePassesSyllableStep(
        RxAudioLevelerFixtureProfileMetricsDto current,
        RxAudioLevelerFixtureProfileMetricsDto candidate) =>
        candidate.OutputRmsMovementDb <= current.OutputRmsMovementDb - 1.5 &&
        candidate.WeakOutputAverageDbfs >= current.WeakOutputAverageDbfs + 1.5 &&
        candidate.MaxAbsGainDeltaDb <= 4.5 &&
        candidate.AppliedGainMovementDb <= 4.5 &&
        candidate.OutputLimitedBlockCount == 0 &&
        candidate.MaxOutputPeakDbfs < -8.0;

    private static bool CandidatePassesNearTargetSpeech(
        RxAudioLevelerFixtureProfileMetricsDto current,
        RxAudioLevelerFixtureProfileMetricsDto candidate) =>
        candidate.OutputRmsMovementDb <= current.OutputRmsMovementDb + 0.25 &&
        candidate.AppliedGainMovementDb <= current.AppliedGainMovementDb + 0.25 &&
        candidate.MaxControlRmsHangDb <= 0.25 &&
        candidate.OutputLimitedBlockCount == 0 &&
        candidate.MaxOutputPeakDbfs < -4.0;

    private static bool CandidatePassesLiveCrestHeadroom(
        RxAudioLevelerFixtureProfileMetricsDto current,
        RxAudioLevelerFixtureProfileMetricsDto candidate) =>
        candidate.OutputLimitedBlockCount == 0 &&
        candidate.PeakLimitedBlockCount <= current.PeakLimitedBlockCount &&
        candidate.MaxOutputPeakDbfs <= -3.2 &&
        candidate.MaxOutputPeakDbfs <= current.MaxOutputPeakDbfs - 0.5 &&
        candidate.AppliedGainMovementDb <= current.AppliedGainMovementDb + 0.25 &&
        candidate.OutputRmsMovementDb <= current.OutputRmsMovementDb + 0.25;

    private static bool CandidatePassesSustainedWeakSpeech(
        RxAudioLevelerFixtureProfileMetricsDto current,
        RxAudioLevelerFixtureProfileMetricsDto candidate) =>
        candidate.FinalOutputRmsDbfs >= -20.5 &&
        candidate.FinalOutputRmsDbfs <= -16.5 &&
        candidate.OutputLimitedBlockCount == 0 &&
        candidate.BlocksToTarget > 0;

    private static bool CandidatePassesStrongAfterWeak(
        RxAudioLevelerFixtureProfileMetricsDto current,
        RxAudioLevelerFixtureProfileMetricsDto candidate) =>
        candidate.FinalOutputRmsDbfs <= current.FinalOutputRmsDbfs + 0.5 &&
        candidate.FinalOutputPeakDbfs < -1.0 &&
        candidate.OutputLimitedBlockCount == 0 &&
        candidate.PeakLimitedBlockCount <= current.PeakLimitedBlockCount;

    private static LevelerFixtureBlock[] SyllableStepBlocks()
    {
        var blocks = new List<LevelerFixtureBlock>();
        for (int i = 0; i < 6; i++)
            blocks.Add(ConstantBlock("pre-roll", 0.04f, measure: false, weak: false));
        for (int i = 0; i < 12; i++)
        {
            bool weak = i % 2 == 0;
            blocks.Add(ConstantBlock(weak ? "weak-syllable" : "strong-syllable", weak ? 0.01f : 0.04f, measure: true, weak));
        }

        return blocks.ToArray();
    }

    private static LevelerFixtureBlock[] NearTargetSpeechBlocks()
    {
        var blocks = new List<LevelerFixtureBlock>();
        for (int i = 0; i < 4; i++)
            blocks.Add(ConstantBlock("pre-roll", 0.16f, measure: false, weak: false));
        for (int i = 0; i < 12; i++)
        {
            float value = i % 3 == 0 ? 0.18f : 0.13f;
            blocks.Add(ConstantBlock("near-target-active", value, measure: true, weak: false));
        }

        return blocks.ToArray();
    }

    private static LevelerFixtureBlock[] LiveCrestHeadroomBlocks()
    {
        var blocks = new List<LevelerFixtureBlock>();
        for (int i = 0; i < 5; i++)
            blocks.Add(CrestBlock("crest-pre-roll", 0.04f, 0.4f, measure: false, weak: false));
        for (int i = 0; i < 10; i++)
        {
            float baseValue = i % 2 == 0 ? 0.038f : 0.042f;
            blocks.Add(CrestBlock("live-crest-active", baseValue, 0.4f, measure: true, weak: false));
        }

        return blocks.ToArray();
    }

    private static LevelerFixtureBlock[] SustainedWeakSpeechBlocks()
    {
        var blocks = new List<LevelerFixtureBlock>();
        for (int i = 0; i < 6; i++)
            blocks.Add(ConstantBlock("pre-roll", 0.04f, measure: false, weak: false));
        for (int i = 0; i < 20; i++)
            blocks.Add(ConstantBlock("sustained-weak", 0.01f, measure: true, weak: true));
        return blocks.ToArray();
    }

    private static LevelerFixtureBlock[] StrongAfterWeakBlocks()
    {
        var blocks = new List<LevelerFixtureBlock>();
        for (int i = 0; i < 18; i++)
            blocks.Add(ConstantBlock("weak-pre-roll", 0.01f, measure: false, weak: true));
        blocks.Add(ConstantBlock("strong-arrival", 0.9f, measure: true, weak: false));
        return blocks.ToArray();
    }

    private static LevelerFixtureBlock ConstantBlock(string segment, float value, bool measure, bool weak) =>
        new(segment, measure, weak, samples => Array.Fill(samples, value));

    private static LevelerFixtureBlock CrestBlock(string segment, float baseValue, float crestValue, bool measure, bool weak) =>
        new(segment, measure, weak, samples =>
        {
            Array.Fill(samples, baseValue);
            for (int i = 320; i < samples.Length; i += 256)
                samples[i] = crestValue;
        });

    private static double Round(double value) =>
        double.IsFinite(value) ? Math.Round(value, 6) : value;

    private sealed record LevelerFixtureBlock(
        string Segment,
        bool Measure,
        bool Weak,
        Action<float[]> Fill);
}

internal sealed record RxAudioLevelerFixtureBenchmarkDto(
    int SchemaVersion,
    string EvidenceKind,
    string DefaultProfile,
    string CandidateProfile,
    bool ExperimentalOptIn,
    bool DefaultBehaviorChanged,
    RxAudioLevelerFixtureReadinessDto Readiness,
    string[] AcceptanceGates,
    RxAudioLevelerFixtureScenarioDto[] Scenarios);

internal sealed record RxAudioLevelerFixtureReadinessDto(
    int ScenarioCount,
    int CandidatePassCount,
    int CandidateFailCount,
    bool CandidateAllGatesPass,
    bool ExperimentalOptIn,
    bool DefaultBehaviorChanged,
    bool ReadyForLiveAb,
    string Recommendation);

internal sealed record RxAudioLevelerFixtureScenarioDto(
    string Id,
    string ScenarioId,
    string Name,
    string Description,
    string SignalPath,
    string FixtureStatus,
    RxAudioLevelerFixtureProfileMetricsDto Current,
    RxAudioLevelerFixtureProfileMetricsDto Candidate,
    RxAudioLevelerFixtureComparisonDto Comparison,
    RxAudioLevelerFixtureComparisonEntryDto[] Comparisons);

internal sealed record RxAudioLevelerFixtureProfileMetricsDto(
    string Profile,
    int MeasurementBlockCount,
    int WeakMeasurementBlockCount,
    int BlocksToTarget,
    double FinalGainDb,
    double FinalOutputRmsDbfs,
    double FinalOutputPeakDbfs,
    double MaxAbsGainDeltaDb,
    double AppliedGainMovementDb,
    double OutputRmsMovementDb,
    double WeakOutputAverageDbfs,
    double MaxOutputPeakDbfs,
    int OutputLimitedBlockCount,
    int PeakLimitedBlockCount,
    int BoostSlewLimitedBlockCount,
    int ControlRmsValidBlockCount,
    double MaxControlRmsHangDb);

internal sealed record RxAudioLevelerFixtureComparisonDto(
    bool CandidatePasses,
    double MaxGainDeltaReductionDb,
    double AppliedGainMovementReductionDb,
    double OutputRmsMovementReductionDb,
    double WeakOutputAverageDeltaDb,
    double CandidatePeakRegressionDb,
    int CandidateLimitBlockDelta);

internal sealed record RxAudioLevelerFixtureComparisonEntryDto(
    string ComparisonId,
    string Profile,
    RxAudioLevelerFixtureMetricMapDto Metrics,
    RxAudioLevelerFixtureGateDto[] Gates);

internal sealed record RxAudioLevelerFixtureMetricMapDto(
    double MaxAbsGainDeltaDb,
    double AppliedGainMovementDb,
    double OutputRmsMovementDb,
    double WeakOutputAverageDbfs,
    double MaxOutputPeakDbfs,
    int OutputLimitedBlockCount,
    int PeakLimitedBlockCount,
    int BlocksToTarget,
    int ControlRmsValidBlockCount,
    double MaxControlRmsHangDb);

internal sealed record RxAudioLevelerFixtureGateDto(
    string Id,
    bool Passed);
