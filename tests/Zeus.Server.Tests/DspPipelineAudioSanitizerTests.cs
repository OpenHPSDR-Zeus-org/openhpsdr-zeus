// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;
using Zeus.Contracts;
using System.Text.Json;

namespace Zeus.Server.Tests;

public sealed class DspPipelineAudioSanitizerTests
{
    private static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i] * samples[i];
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static float PeakAbs(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
            peak = Math.Max(peak, Math.Abs(samples[i]));
        return peak;
    }

    private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);

    private static Nr5SpnrDiagnosticsDto Nr5Diagnostics(
        double signalConfidence,
        double signalProbability,
        double agcGate,
        double recoveryDrive,
        double weakSignalMemory,
        double outputDbfs = -44.0,
        double inputDbfs = -40.0,
        double maskSmoothing = 0.32,
        double peakEvidence = 0.0,
        double outputPeakDbfs = -34.0,
        double adjacentNoiseTrust = 0.62,
        double adjacentNoiseDrive = 0.18,
        bool adjacentNoiseUsable = true,
        double levelDrive = 0.8) =>
        new(
            SchemaVersion: 9,
            ChannelId: 0,
            Run: true,
            Position: 1,
            LearnedFrames: 100,
            Aggressiveness: 0.62,
            AgcRun: true,
            TargetRms: 0.075,
            MaxGain: 48.0,
            AgcGain: 1.0,
            AgcGainDb: 0.0,
            PresencePeak: 0.5,
            SaliencePeak: 0.3,
            CoherencePeak: 0.2,
            RidgePeak: 0.2,
            MeanGain: 0.2,
            MinGain: 0.01,
            SuppressionDb: -14.0,
            NoiseFloorDb: -20.0,
            FloorReductionDb: 4.5,
            DynamicRangeDb: 30.0,
            SignalProbability: signalProbability,
            TextureFill: 0.03,
            MaskSmoothing: maskSmoothing,
            SignalConfidence: signalConfidence,
            AgcGate: agcGate,
            LevelDrive: levelDrive,
            RecoveryDrive: recoveryDrive,
            WeakSignalMemory: weakSignalMemory,
            MakeupGain: 1.0,
            MakeupGainDb: 0.0,
            InputRms: 0.01,
            InputDbfs: inputDbfs,
            OutputRms: Math.Pow(10.0, outputDbfs / 20.0),
            OutputDbfs: outputDbfs,
            OutputPeak: Math.Pow(10.0, outputPeakDbfs / 20.0),
            OutputPeakDbfs: outputPeakDbfs,
            PeakEvidence: peakEvidence,
            PeakLimit: 0.6,
            PeakLimitDbfs: -4.5,
            PeakReductionDb: 0.0,
            AdjacentNoiseUsable: adjacentNoiseUsable,
            AdjacentNoiseBins: 72,
            AdjacentNoiseFloorDb: -105.0,
            AdjacentNoiseTrust: adjacentNoiseTrust,
            AdjacentNoiseDrive: adjacentNoiseDrive,
            AdjacentNoiseRejectedPct: 8.0,
            AdjacentNoiseLeftBins: 34,
            AdjacentNoiseRightBins: 38,
            AdjacentNoiseLeftFloorDb: -105.2,
            AdjacentNoiseRightFloorDb: -104.9,
            AdjacentNoiseSideBalance: 0.895,
            AdjacentNoiseAsymmetryDb: 0.3);

    private static FrontendDspSceneTopPeakDto FrontendTopPeak(
        int offsetHz,
        double snrDb = 18.1,
        double dbfs = -84.0,
        double confidence = 0.857,
        bool coherent = true) =>
        new(
            FrequencyHz: 14_267_000 + offsetHz,
            OffsetHz: offsetHz,
            SnrDb: snrDb,
            Dbfs: dbfs,
            Confidence: confidence,
            Coherent: coherent);

    [Fact]
    public void SanitizeAudioBuffer_ClampsOverrangeAndZerosNonFiniteSamples()
    {
        float[] samples =
        {
            float.NaN,
            float.PositiveInfinity,
            float.NegativeInfinity,
            1.25f,
            -1.5f,
            0.125f,
        };

        DspPipelineService.SanitizeAudioBuffer(samples);

        Assert.Equal(0f, samples[0]);
        Assert.Equal(0f, samples[1]);
        Assert.Equal(0f, samples[2]);
        Assert.Equal(1f, samples[3]);
        Assert.Equal(-1f, samples[4]);
        Assert.Equal(0.125f, samples[5]);
    }

    [Fact]
    public void LimitRxAudioBuffer_SoftLimitsFinalBusOverrange()
    {
        float[] samples =
        {
            float.NaN,
            0.5f,
            0.95f,
            -1.1f,
        };

        DspPipelineService.LimitRxAudioBuffer(samples);

        Assert.Equal(0f, samples[0]);
        Assert.Equal(0.5f, samples[1]);
        Assert.InRange(samples[2], 0.83f, 0.841f);
        Assert.InRange(samples[3], -0.841f, -0.83f);
    }

    [Fact]
    public void SanitizeDisplayBuffer_ReplacesOnlyNonFiniteBins()
    {
        float[] bins =
        {
            -140.5f,
            float.NaN,
            -73.25f,
            float.PositiveInfinity,
            12.5f,
            float.NegativeInfinity,
        };

        DspPipelineService.SanitizeDisplayBuffer(bins);

        Assert.Equal(-140.5f, bins[0]);
        Assert.Equal(-200f, bins[1]);
        Assert.Equal(-73.25f, bins[2]);
        Assert.Equal(-200f, bins[3]);
        Assert.Equal(12.5f, bins[4]);
        Assert.Equal(-200f, bins[5]);
    }

    [Fact]
    public void BuildDisplayBufferDiagnostics_ReportsFiniteDisplayStats()
    {
        float[] bins =
        {
            -120.25f,
            -100.5f,
            float.NaN,
            -80f,
            float.PositiveInfinity,
        };

        using var doc = JsonSerializer.SerializeToDocument(
            DspPipelineService.BuildDisplayBufferDiagnostics(valid: true, bins, ageMs: 33));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("valid").GetBoolean());
        Assert.Equal(33, root.GetProperty("ageMs").GetInt64());
        Assert.Equal(3, root.GetProperty("validBins").GetInt32());
        Assert.Equal(-120.2, root.GetProperty("minDb").GetDouble());
        Assert.Equal(-80.0, root.GetProperty("maxDb").GetDouble());
        Assert.Equal(-100.2, root.GetProperty("meanDb").GetDouble());
        Assert.Equal(40.2, root.GetProperty("dynamicRangeDb").GetDouble());
    }

    [Fact]
    public void BuildDisplayBufferDiagnostics_SuppressesStatsWhenInvalid()
    {
        using var doc = JsonSerializer.SerializeToDocument(
            DspPipelineService.BuildDisplayBufferDiagnostics(valid: false, new[] { -80f, -90f }, ageMs: null));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("ageMs").ValueKind);
        Assert.Equal(0, root.GetProperty("validBins").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("minDb").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("maxDb").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("meanDb").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("dynamicRangeDb").ValueKind);
    }

    [Fact]
    public void BuildRxDspChainDiagnostics_FlagsNrCapabilityLimitsAndManualNotches()
    {
        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.25:1024",
            VfoHz: 7_262_000,
            Mode: RxMode.LSB,
            FilterLowHz: -2850,
            FilterHighHz: -150,
            SampleRate: 384_000,
            AgcTopDb: 68.0,
            Agc: new AgcConfig(AgcMode.Fast),
            Squelch: new SquelchConfig(Enabled: true, Level: 18, Adaptive: true),
            Nr: new NrConfig(
                NrMode: NrMode.Sbnr,
                AnfEnabled: true,
                SnbEnabled: true,
                NbpNotchesEnabled: false,
                NbMode: NbMode.Nb1,
                NbThreshold: 18.0),
            AutoAgcEnabled: true,
            AgcOffsetDb: -6.0);
        var runtime = new DspNrRuntimeSnapshot(
            WdspActive: true,
            WdspNativeLoadable: true,
            WdspEmnrPost2Available: true,
            WdspNr4SbnrAvailable: false,
            WdspNr5SpnrAvailable: false,
            Nr4Readiness: "missing-sbnr-exports",
            Nr5Readiness: "missing-spnr-exports",
            RequestedNrMode: "Sbnr",
            EffectiveNrMode: "Off",
            Nr5SpnrDiagnostics: null);

        var diag = DspPipelineService.BuildRxDspChainDiagnostics(
            state,
            new[]
            {
                new NotchDto(7_261_200, 80, Active: true),
                new NotchDto(7_261_800, 120, Active: false),
            },
            runtime,
            appliedNr: state.Nr,
            appliedAgc: state.Agc,
            appliedSquelch: state.Squelch);

        Assert.Equal(1, diag.SchemaVersion);
        Assert.Equal("nr-capability-limited", diag.Status);
        Assert.Equal("Sbnr", diag.RequestedNrMode);
        Assert.Equal("Off", diag.EffectiveNrMode);
        Assert.True(diag.AnfEnabled);
        Assert.True(diag.SnbEnabled);
        Assert.Equal("Nb1", diag.NbMode);
        Assert.Equal(2, diag.ManualNotchCount);
        Assert.Equal(1, diag.ActiveManualNotchCount);
        Assert.True(diag.EffectiveNbpNotchesRun);
        Assert.Equal(62.0, diag.EffectiveAgcTopDb);
        Assert.Contains("manual-notches", diag.ActiveFeatures);
        Assert.Contains("nr-capability-limited", diag.QualityReasons);
        Assert.Contains("NR2/EMNR", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildAudioPathDiagnostics_FlagsClippingRisk()
    {
        var diag = DspPipelineService.BuildAudioPathDiagnostics(
            valid: true,
            ageMs: 18,
            lastSeq: 123,
            framesBroadcast: 12,
            source: "rx",
            sampleRateHz: 48_000,
            sampleCount: 1600,
            rms: 0.22,
            peak: 0.991,
            txMonitorRequested: false,
            squelchEnabled: false,
            squelchOpen: true,
            squelchTailActive: false,
            squelchGain: 1.0,
            monitorBacklogSamples: 0,
            audioSinkCount: 1);

        Assert.Equal("clipping-risk", diag.Status);
        Assert.True(diag.Fresh);
        Assert.False(diag.Stale);
        Assert.Equal("rx", diag.Source);
        Assert.Equal(123u, diag.LastSeq);
        Assert.Equal(12, diag.FramesBroadcast);
        Assert.Equal(-13.2, diag.RmsDbfs);
        Assert.Equal(-0.1, diag.PeakDbfs);
        Assert.Null(diag.RxAudioLevelerAppliedGainDb);
        Assert.Contains("full scale", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildAudioPathDiagnostics_ReportsAdaptiveSquelchMute()
    {
        var diag = DspPipelineService.BuildAudioPathDiagnostics(
            valid: true,
            ageMs: 12,
            lastSeq: 124,
            framesBroadcast: 13,
            source: "rx",
            sampleRateHz: 48_000,
            sampleCount: 1600,
            rms: 0.0,
            peak: 0.0,
            txMonitorRequested: false,
            squelchEnabled: true,
            squelchOpen: false,
            squelchTailActive: false,
            squelchGain: 0.0,
            monitorBacklogSamples: 0,
            audioSinkCount: 2);

        Assert.Equal("muted-by-squelch", diag.Status);
        Assert.True(diag.Fresh);
        Assert.True(diag.SquelchEnabled);
        Assert.False(diag.SquelchOpen);
        Assert.Equal(0.0, diag.SquelchGateGain);
        Assert.Equal("adaptive", diag.SquelchMode);
        Assert.Equal("backend-adaptive", diag.SquelchGateSource);
        Assert.True(diag.SquelchOpenKnown);
        Assert.Null(diag.RmsDbfs);
        Assert.Contains("adaptive squelch", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildAudioPathDiagnostics_ReportsSilentFixedSquelchAsWdspGate()
    {
        var diag = DspPipelineService.BuildAudioPathDiagnostics(
            valid: true,
            ageMs: 16,
            lastSeq: 126,
            framesBroadcast: 15,
            source: "rx",
            sampleRateHz: 48_000,
            sampleCount: 1600,
            rms: 0.0,
            peak: 0.0,
            txMonitorRequested: false,
            squelchEnabled: true,
            squelchOpen: true,
            squelchTailActive: false,
            squelchGain: 1.0,
            monitorBacklogSamples: 0,
            audioSinkCount: 2,
            squelchMode: "fixed",
            squelchGateSource: "wdsp-fixed",
            squelchOpenKnown: false);

        Assert.Equal("silent", diag.Status);
        Assert.True(diag.SquelchEnabled);
        Assert.True(diag.SquelchOpen);
        Assert.False(diag.SquelchOpenKnown);
        Assert.Equal("fixed", diag.SquelchMode);
        Assert.Equal("wdsp-fixed", diag.SquelchGateSource);
        Assert.Equal(1.0, diag.SquelchGateGain);
        Assert.Contains("fixed SQL is active", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildAudioPathDiagnostics_ReportsTxMonitorSource()
    {
        var diag = DspPipelineService.BuildAudioPathDiagnostics(
            valid: true,
            ageMs: 40,
            lastSeq: 125,
            framesBroadcast: 14,
            source: "tx-monitor",
            sampleRateHz: 48_000,
            sampleCount: 1600,
            rms: 0.05,
            peak: 0.18,
            txMonitorRequested: true,
            squelchEnabled: true,
            squelchOpen: false,
            squelchTailActive: true,
            squelchGain: 0.35,
            monitorBacklogSamples: 240,
            audioSinkCount: 2);

        Assert.Equal("tx-monitor", diag.Status);
        Assert.Equal("tx-monitor", diag.Source);
        Assert.True(diag.TxMonitorRequested);
        Assert.Equal(-26.0, diag.RmsDbfs);
        Assert.Equal(-14.9, diag.PeakDbfs);
        Assert.Equal(0.35, diag.SquelchGateGain);
        Assert.Null(diag.RxAudioLevelerAppliedGainDb);
        Assert.Null(diag.RxAudioLevelerOutputLimitReductionDb);
        Assert.Null(diag.RxAudioLevelerOutputLimited);
        Assert.Contains("processed transmit monitor", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildRxListenabilityDiagnostics_FlagsFixedSquelchBlockingSignal()
    {
        var rxMeters = DspPipelineService.BuildRxMetersDiagnostics(
            valid: true,
            ageMs: 25,
            channelId: 0,
            rxDbm: -85.6,
            meters: new RxMetersV2Frame(
                SignalPk: -73.4f,
                SignalAv: -85.6f,
                AdcPk: -61.3f,
                AdcAv: -70.1f,
                AgcGain: -55.5f,
                AgcEnvPk: -41.3f,
                AgcEnvAv: -50.7f));
        var audio = DspPipelineService.BuildAudioPathDiagnostics(
            valid: true,
            ageMs: 18,
            lastSeq: 130,
            framesBroadcast: 20,
            source: "rx",
            sampleRateHz: 48_000,
            sampleCount: 1600,
            rms: 0.0,
            peak: 0.0,
            txMonitorRequested: false,
            squelchEnabled: true,
            squelchOpen: true,
            squelchTailActive: false,
            squelchGain: 1.0,
            monitorBacklogSamples: 0,
            audioSinkCount: 2,
            squelchMode: "fixed",
            squelchGateSource: "wdsp-fixed",
            squelchOpenKnown: false);

        var listenability = DspPipelineService.BuildRxListenabilityDiagnostics(
            rxMeters,
            audio,
            new SquelchConfig(Enabled: true, Level: 42, Adaptive: false));

        Assert.Equal("fixed-squelch-suspect", listenability.Status);
        Assert.Equal("optimize", listenability.Tone);
        Assert.True(listenability.SignalPresent);
        Assert.False(listenability.AudioRecovered);
        Assert.Equal("fixed-squelch", listenability.Blocker);
        Assert.Contains("lower fixed SQL", listenability.Recommendation);
    }

    [Fact]
    public void BuildRxListenabilityDiagnostics_ReportsRecoveredAudio()
    {
        var rxMeters = DspPipelineService.BuildRxMetersDiagnostics(
            valid: true,
            ageMs: 20,
            channelId: 0,
            rxDbm: -82.0,
            meters: new RxMetersV2Frame(
                SignalPk: -76.0f,
                SignalAv: -88.0f,
                AdcPk: -55.0f,
                AdcAv: -68.0f,
                AgcGain: -12.0f,
                AgcEnvPk: -48.0f,
                AgcEnvAv: -55.0f));
        var audio = DspPipelineService.BuildAudioPathDiagnostics(
            valid: true,
            ageMs: 15,
            lastSeq: 131,
            framesBroadcast: 21,
            source: "rx",
            sampleRateHz: 48_000,
            sampleCount: 1600,
            rms: 0.04,
            peak: 0.16,
            txMonitorRequested: false,
            squelchEnabled: false,
            squelchOpen: true,
            squelchTailActive: false,
            squelchGain: 1.0,
            monitorBacklogSamples: 0,
            audioSinkCount: 2);

        var listenability = DspPipelineService.BuildRxListenabilityDiagnostics(
            rxMeters,
            audio,
            new SquelchConfig(Enabled: false, Level: 0, Adaptive: true));

        Assert.Equal("audio-recovered", listenability.Status);
        Assert.Equal("ready", listenability.Tone);
        Assert.True(listenability.SignalPresent);
        Assert.True(listenability.AudioRecovered);
        Assert.Equal("none", listenability.Blocker);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsWeakAudioTowardSpeechLevel()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        for (int i = 0; i < 12; i++)
        {
            Array.Fill(block, 0.01f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        Assert.InRange(Rms(block), 0.10f, 0.15f);
        Assert.True(state.GainDb > 20.0);
        Assert.True(state.DiagnosticsValid);
        Assert.Equal(state.GainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -20.5, -16.5);
        Assert.False(state.OutputLimited);
        Assert.Equal(0, state.OutputLimitSampleCount);
        Assert.Equal(0.0, state.OutputLimitReductionDb, precision: 6);
    }

    [Fact]
    public void ApplyRxAudioLeveler_RampsBoostAcrossBlock()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        Array.Fill(block, 0.01f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.Equal(6.0, state.GainDb, precision: 6);
        Assert.True(state.BoostSlewLimited);
        Assert.Equal(6.0, state.GainDeltaDb, precision: 6);
        Assert.InRange(block[0], 0.0099f, 0.0102f);
        Assert.InRange(block[255], 0.0199f, 0.0201f);
        Assert.InRange(block[^1], 0.0199f, 0.0201f);

        Array.Fill(block, 0.01f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.Equal(12.0, state.GainDb, precision: 6);
        Assert.True(state.BoostSlewLimited);
        Assert.InRange(block[0], 0.0199f, 0.0201f);
        Assert.InRange(block[255], 0.0397f, 0.0399f);
        Assert.InRange(block[^1], 0.0397f, 0.0399f);
    }

    [Fact]
    public void ApplyRxAudioLeveler_KeepsDeepFloorBoostConservative()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        Array.Fill(block, 0.001f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.Equal(3.5, state.GainDb, precision: 6);
        Assert.True(state.BoostSlewLimited);
        Assert.Equal(3.5, state.GainDeltaDb, precision: 6);
        Assert.InRange(state.InputRmsDbfs, -60.1, -59.9);
        Assert.InRange(block[^1], 0.00149f, 0.00151f);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotRestoreNr5MutedNoiseFloor()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 24.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-47.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.276,
            signalProbability: 0.121,
            agcGate: 0.458,
            recoveryDrive: 0.244,
            weakSignalMemory: 0.152);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 9.5, 13.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -38.5, -33.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsUncertainNr5SpeechTailBoost()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 24.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-43.0));
        block[0] = DbToLinear(-30.0);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.30,
            signalProbability: 0.15,
            agcGate: 0.50,
            recoveryDrive: 0.28,
            weakSignalMemory: 0.22);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 15.0, 16.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotNormalizeLowEvidenceNr5FloorToSpeechLevel()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 24.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-30.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.31,
            signalProbability: 0.175,
            agcGate: 0.399,
            recoveryDrive: 0.242,
            weakSignalMemory: 0.136,
            outputDbfs: -35.2);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 5.4, 6.4);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -25.2, -24.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsBorderlineLowEvidenceNr5Tail()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 24.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-38.6));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.302,
            signalProbability: 0.147,
            agcGate: 0.486,
            recoveryDrive: 0.325,
            weakSignalMemory: 0.282,
            outputDbfs: -31.9);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 12.9, 13.9);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -25.8, -24.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotNormalizeUncertainNr5OverLiftToVoiceLevel()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 16.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-33.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.318,
            signalProbability: 0.220,
            agcGate: 0.588,
            recoveryDrive: 0.389,
            weakSignalMemory: 0.346,
            outputDbfs: -22.8,
            inputDbfs: -45.5,
            maskSmoothing: 0.345);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 8.0, 9.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -25.7, -24.4);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_RequiresSpectralProofBeforeMakingNr5MemoryLoud()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 17.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-36.1));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.284,
            signalProbability: 0.113,
            agcGate: 0.878,
            recoveryDrive: 0.435,
            weakSignalMemory: 0.460,
            outputDbfs: -35.8,
            inputDbfs: -44.4,
            maskSmoothing: 0.352,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 8.0, 9.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -28.3, -27.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_PreservesNr5SpeechValleyWithoutPeakEvidence()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 25.1,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-55.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.278,
            signalProbability: 0.127,
            agcGate: 0.672,
            recoveryDrive: 0.383,
            weakSignalMemory: 0.376,
            outputDbfs: -31.6,
            inputDbfs: -37.5,
            maskSmoothing: 0.079,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 20.0, 27.0);
        Assert.InRange(state.AppliedGainDb, 20.0, 27.0);
        Assert.InRange(state.OutputRmsDbfs, -35.5, -28.5);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesProfiledNr5SpeechValleyWithoutPeakEvidence()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 28.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-59.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.287,
            signalProbability: 0.131,
            agcGate: 0.657,
            recoveryDrive: 0.373,
            weakSignalMemory: 0.360,
            outputDbfs: -39.9,
            inputDbfs: -56.6,
            maskSmoothing: 0.340,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.787,
            adjacentNoiseDrive: 0.703);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb >= 40.0);
        Assert.True(state.AppliedGainDb >= 40.0);
        Assert.InRange(state.OutputRmsDbfs, -20.5, -17.0);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesNativeNr5SpeechValleyWhenAdjacentTrustIsLow()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 34.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-64.6));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.311,
            signalProbability: 0.151,
            agcGate: 0.761,
            recoveryDrive: 0.492,
            weakSignalMemory: 0.350,
            outputDbfs: -38.5,
            inputDbfs: -55.2,
            maskSmoothing: 0.335,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.093,
            adjacentNoiseDrive: 0.059);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb >= 43.0);
        Assert.True(state.AppliedGainDb >= 43.0);
        Assert.InRange(state.OutputRmsDbfs, -22.0, -19.5);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesNativeRecoveredNr5SpeechWhenOutputEvidenceIsStrong()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 32.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-60.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.329,
            signalProbability: 0.197,
            agcGate: 0.620,
            recoveryDrive: 0.45,
            weakSignalMemory: 0.358,
            outputDbfs: -29.6,
            inputDbfs: -54.6,
            maskSmoothing: 0.32,
            peakEvidence: 0.181,
            adjacentNoiseTrust: 0.429,
            adjacentNoiseDrive: 0.30);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb >= 40.0);
        Assert.True(state.AppliedGainDb >= 40.0);
        Assert.InRange(state.OutputRmsDbfs, -21.0, -18.0);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesPeakBackedNativeRecoveredNr5SpeechWhenAgcGateLags()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 34.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-64.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.372,
            signalProbability: 0.200,
            agcGate: 0.495,
            recoveryDrive: 0.40,
            weakSignalMemory: 0.261,
            outputDbfs: -32.6,
            inputDbfs: -55.1,
            maskSmoothing: 0.33,
            peakEvidence: 0.248,
            adjacentNoiseTrust: 0.556,
            adjacentNoiseDrive: 0.432);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb >= 43.0);
        Assert.True(state.AppliedGainDb >= 43.0);
        Assert.InRange(state.OutputRmsDbfs, -21.5, -19.0);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotNormalizeLowProofNativeRecoveredFloor()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 29.4,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-58.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.290,
            signalProbability: 0.134,
            agcGate: 0.611,
            recoveryDrive: 0.30,
            weakSignalMemory: 0.402,
            outputDbfs: -38.5,
            inputDbfs: -50.5,
            maskSmoothing: 0.32,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.429,
            adjacentNoiseDrive: 0.30);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb < 35.0);
        Assert.True(state.AppliedGainDb < 35.0);
        Assert.True(state.OutputRmsDbfs < -24.0);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesFrontendPeakBackedNr5SpeechValley()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 31.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-57.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.299,
            signalProbability: 0.156,
            agcGate: 0.678,
            recoveryDrive: 0.441,
            weakSignalMemory: 0.469,
            outputDbfs: -35.8,
            inputDbfs: -47.8,
            maskSmoothing: 0.367,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.429,
            adjacentNoiseDrive: 0.30);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5, FrontendTopPeak(offsetHz: -164));

        Assert.True(state.DesiredGainDb >= 38.0);
        Assert.True(state.AppliedGainDb >= 38.0);
        Assert.InRange(state.OutputRmsDbfs, -21.0, -18.0);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
    }

    [Fact]
    public void ApplyRxAudioLeveler_IgnoresFarFrontendPeakForNr5SpeechValley()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 31.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-57.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.299,
            signalProbability: 0.156,
            agcGate: 0.678,
            recoveryDrive: 0.441,
            weakSignalMemory: 0.469,
            outputDbfs: -35.8,
            inputDbfs: -47.8,
            maskSmoothing: 0.367,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.429,
            adjacentNoiseDrive: 0.30);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5, FrontendTopPeak(offsetHz: -6_414));

        Assert.True(state.DesiredGainDb < 35.0);
        Assert.True(state.AppliedGainDb < 35.0);
        Assert.True(state.OutputRmsDbfs < -24.0);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesFrontendPeakBackedNr5ActiveSpeechWithoutNativeLift()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 29.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-58.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.287,
            signalProbability: 0.155,
            agcGate: 0.641,
            recoveryDrive: 0.399,
            weakSignalMemory: 0.401,
            outputDbfs: -34.9,
            inputDbfs: -43.5,
            maskSmoothing: 0.367,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.429,
            adjacentNoiseDrive: 0.30);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5, FrontendTopPeak(offsetHz: -414, snrDb: 22.7, dbfs: -80.3, confidence: 0.908));

        Assert.True(state.DesiredGainDb >= 37.0);
        Assert.True(state.AppliedGainDb >= 37.0);
        Assert.InRange(state.OutputRmsDbfs, -21.5, -18.0);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_PreservesFrontendConfirmedNr5PassbandContinuity()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 10.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 23
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-68.6));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.273,
            signalProbability: 0.120,
            agcGate: 0.350,
            recoveryDrive: 0.164,
            weakSignalMemory: 0.022,
            outputDbfs: -36.9,
            inputDbfs: -25.7,
            maskSmoothing: 0.073,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.457,
            adjacentNoiseDrive: 0.375);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 102, snrDb: 27.8, dbfs: -73.7, confidence: 0.94));

        Assert.True(
            state.DesiredGainDb >= 24.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.True(
            state.AppliedGainDb >= 24.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.InRange(state.OutputRmsDbfs, -45.0, -38.0);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
    }

    [Fact]
    public void ApplyRxAudioLeveler_FastCatchesRecoveredNr5PassbandOnset()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-57.1));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.349,
            signalProbability: 0.211,
            agcGate: 0.277,
            recoveryDrive: 0.644,
            weakSignalMemory: 0.081,
            outputDbfs: -21.9,
            inputDbfs: -29.2,
            maskSmoothing: 0.011,
            peakEvidence: 0.169,
            adjacentNoiseTrust: 0.45,
            adjacentNoiseDrive: 0.38);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -2_625, snrDb: 14.8, dbfs: -141.4, confidence: 0.801));

        Assert.True(
            state.DesiredGainDb >= 30.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.True(
            state.AppliedGainDb >= 29.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.True(
            state.OutputRmsDbfs > -32.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_KeepsSuppressedPassbandGapClosedWithoutNr5Output()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 10.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 18
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-72.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.274,
            signalProbability: 0.136,
            agcGate: 0.392,
            recoveryDrive: 0.177,
            weakSignalMemory: 0.043,
            outputDbfs: -50.4,
            inputDbfs: -34.0,
            maskSmoothing: 0.190,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.457,
            adjacentNoiseDrive: 0.375);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 102, snrDb: 29.4, dbfs: -72.0, confidence: 0.94));

        Assert.Equal(0.0, state.DesiredGainDb, precision: 3);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 3);
        Assert.InRange(state.OutputRmsDbfs, -73.5, -72.3);
        Assert.Equal(17, state.Nr5SpeechHoldBlocks);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_ExtendsNativeRecoveredSpeechPastGlobalBoostCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 36.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-59.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.373,
            signalProbability: 0.275,
            agcGate: 0.709,
            recoveryDrive: 0.60,
            weakSignalMemory: 0.479,
            outputDbfs: -25.7,
            inputDbfs: -41.9,
            maskSmoothing: 0.333,
            peakEvidence: 0.491,
            adjacentNoiseTrust: 0.332,
            adjacentNoiseDrive: 0.20);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb > 40.0);
        Assert.True(state.AppliedGainDb > 40.0);
        Assert.InRange(state.OutputRmsDbfs, -20.0, -17.0);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_RequiresNativeLiftForNativeNr5SpeechValleyBoost()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 34.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-64.6));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.311,
            signalProbability: 0.151,
            agcGate: 0.761,
            recoveryDrive: 0.492,
            weakSignalMemory: 0.350,
            outputDbfs: -46.0,
            inputDbfs: -55.2,
            maskSmoothing: 0.335,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.093,
            adjacentNoiseDrive: 0.059);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb < 40.0);
        Assert.True(state.AppliedGainDb < 40.0);
        Assert.True(state.OutputRmsDbfs < -24.0);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_RequiresNativeLiftForProfiledNr5ValleyBoost()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 28.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-59.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.287,
            signalProbability: 0.131,
            agcGate: 0.657,
            recoveryDrive: 0.373,
            weakSignalMemory: 0.360,
            outputDbfs: -46.0,
            inputDbfs: -56.6,
            maskSmoothing: 0.340,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.787,
            adjacentNoiseDrive: 0.703);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 10.0, 15.0);
        Assert.InRange(state.AppliedGainDb, 20.0, 25.0);
        Assert.True(state.OutputRmsDbfs < -28.0);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_PreservesPeakBackedNr5SpeechValleyWhenAgcGateLags()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 16.5,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-63.6));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.351,
            signalProbability: 0.205,
            agcGate: 0.328,
            recoveryDrive: 0.341,
            weakSignalMemory: 0.237,
            outputDbfs: -31.7,
            inputDbfs: -43.0,
            maskSmoothing: 0.372,
            peakEvidence: 0.198);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb >= 24.0);
        Assert.True(state.AppliedGainDb >= 24.0);
        Assert.InRange(state.OutputRmsDbfs, -40.5, -34.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_SlowsHeldNr5SpeechValleyRelease()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 22.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-57.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.301,
            signalProbability: 0.156,
            agcGate: 0.527,
            recoveryDrive: 0.271,
            weakSignalMemory: 0.196,
            outputDbfs: -37.1,
            inputDbfs: -41.4,
            maskSmoothing: 0.079,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 9.5, 11.5);
        Assert.InRange(state.AppliedGainDb, 18.0, 19.0);
        Assert.InRange(state.OutputRmsDbfs, -40.0, -37.5);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_RetainsContinuityBackedNr5SpeechTail()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 24.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-35.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.301,
            signalProbability: 0.125,
            agcGate: 0.653,
            recoveryDrive: 0.528,
            weakSignalMemory: 0.610,
            outputDbfs: -37.5,
            inputDbfs: -55.4,
            maskSmoothing: 0.320,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 11.0, 16.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -25.0, -21.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsVeryWeakContinuityBackedNr5SpeechTail()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 32.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-61.1));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.318,
            signalProbability: 0.151,
            agcGate: 0.895,
            recoveryDrive: 0.486,
            weakSignalMemory: 0.542,
            outputDbfs: -35.6,
            inputDbfs: -54.0,
            maskSmoothing: 0.376,
            peakEvidence: 0.005);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 42.5, 44.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb > 0.0);
        Assert.InRange(state.OutputRmsDbfs, -19.5, -17.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesHeldNativeRecoveredNr5SpeechTail()
    {
        var noHoldState = new DspPipelineService.RxAudioLevelerState { GainDb = 29.0 };
        var heldState = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 29.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] noHoldBlock = new float[1024];
        float[] heldBlock = new float[1024];
        Array.Fill(noHoldBlock, DbToLinear(-61.8));
        Array.Fill(heldBlock, DbToLinear(-61.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.269,
            signalProbability: 0.133,
            agcGate: 0.661,
            recoveryDrive: 0.389,
            weakSignalMemory: 0.386,
            outputDbfs: -39.5,
            inputDbfs: -53.3,
            maskSmoothing: 0.321,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(noHoldBlock, ref noHoldState, nr5);
        DspPipelineService.ApplyRxAudioLeveler(heldBlock, ref heldState, nr5);

        Assert.True(noHoldState.OutputRmsDbfs < -29.0);
        Assert.True(heldState.DesiredGainDb >= 40.0);
        Assert.Equal(heldState.DesiredGainDb, heldState.AppliedGainDb, precision: 6);
        Assert.InRange(heldState.OutputRmsDbfs, -22.0, -18.0);
        Assert.True(heldState.Nr5SpeechHoldBlocks > 0);
        Assert.False(heldState.BoostSlewLimited);
        Assert.False(heldState.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesAdjacentProfiledHeldNr5SpeechTail()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 12.5,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 21
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-66.0));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.299,
            signalProbability: 0.150,
            agcGate: 0.529,
            recoveryDrive: 0.276,
            weakSignalMemory: 0.204,
            outputDbfs: -42.5,
            inputDbfs: -50.5,
            maskSmoothing: 0.321,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.578,
            adjacentNoiseDrive: 0.462);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb >= 42.0);
        Assert.True(state.AppliedGainDb >= 36.0);
        Assert.InRange(state.OutputRmsDbfs, -30.5, -20.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsShortNr5SuppressedSpeechDropout()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 29.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 15
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-69.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.262,
            signalProbability: 0.115,
            agcGate: 0.303,
            recoveryDrive: 0.256,
            weakSignalMemory: 0.170,
            outputDbfs: -64.0,
            inputDbfs: -55.6,
            maskSmoothing: 0.378,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.517,
            adjacentNoiseDrive: 0.397);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.AppliedGainDb >= 24.0);
        Assert.InRange(state.OutputRmsDbfs, -47.0, -39.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesRelaxedAdjacentProfiledHeldNr5Tail()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 13.4,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 17
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-66.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.275,
            signalProbability: 0.122,
            agcGate: 0.418,
            recoveryDrive: 0.319,
            weakSignalMemory: 0.273,
            outputDbfs: -44.3,
            inputDbfs: -54.1,
            maskSmoothing: 0.334,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.615,
            adjacentNoiseDrive: 0.527);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb >= 38.0);
        Assert.True(state.AppliedGainDb >= 30.0);
        Assert.InRange(state.OutputRmsDbfs, -37.0, -24.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldContinuityNr5WeakSpeechTowardReadableLevel()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 7.6,
            PauseHoldBlocks = 17,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-52.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.315,
            signalProbability: 0.125,
            agcGate: 0.423,
            recoveryDrive: 0.386,
            weakSignalMemory: 0.380,
            outputDbfs: -37.9,
            inputDbfs: -46.9,
            maskSmoothing: 0.280,
            peakEvidence: 0.0,
            outputPeakDbfs: -27.0,
            adjacentNoiseTrust: 0.420,
            adjacentNoiseDrive: 0.210);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 375, snrDb: 15.9, dbfs: -88.0, confidence: 0.858));

        Assert.True(
            state.DesiredGainDb >= 13.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -40.5, -36.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotContinuityLiftNr5WeakSpeechWhenPeakIsUntrusted()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 7.6,
            PauseHoldBlocks = 17,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-52.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.315,
            signalProbability: 0.125,
            agcGate: 0.423,
            recoveryDrive: 0.386,
            weakSignalMemory: 0.380,
            outputDbfs: -37.9,
            inputDbfs: -46.9,
            maskSmoothing: 0.280,
            peakEvidence: 0.0,
            outputPeakDbfs: -37.0,
            adjacentNoiseTrust: 0.420,
            adjacentNoiseDrive: 0.210);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 6_200, snrDb: 15.9, dbfs: -88.0, confidence: 0.858));

        Assert.True(
            state.DesiredGainDb <= 11.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -46.0, -40.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsAdjacentProfiledHeldNr5TailWhenFrontendPeakIsFar()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 43.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-64.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.286,
            signalProbability: 0.141,
            agcGate: 0.356,
            recoveryDrive: 0.284,
            weakSignalMemory: 0.216,
            outputDbfs: -43.7,
            inputDbfs: -53.7,
            maskSmoothing: 0.377,
            peakEvidence: 0.0,
            outputPeakDbfs: -34.2,
            adjacentNoiseTrust: 0.695,
            adjacentNoiseDrive: 0.623);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -14_750, snrDb: 13.3, dbfs: -87.8, confidence: 0.794));

        Assert.True(
            state.AppliedGainDb <= 22.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -56.0, -41.0);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsLiveStyleOffPassbandNr5LiftWhenFrontendPeakIsFar()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 19.3,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-51.0));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.325,
            signalProbability: 0.121,
            agcGate: 0.543,
            recoveryDrive: 0.501,
            weakSignalMemory: 0.556,
            outputDbfs: -35.6,
            inputDbfs: -53.4,
            maskSmoothing: 0.095,
            peakEvidence: 0.069,
            outputPeakDbfs: -26.5,
            adjacentNoiseTrust: 0.522,
            adjacentNoiseDrive: 0.474);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -12_750, snrDb: 13.4, dbfs: -87.0, confidence: 0.782));

        Assert.True(
            state.DesiredGainDb <= 13.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(
            state.OutputRmsDbfs <= -37.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsSharpNr5LiftWhenFrontendProofIsUnavailable()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 28.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-51.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.341,
            signalProbability: 0.187,
            agcGate: 0.434,
            recoveryDrive: 0.653,
            weakSignalMemory: 0.413,
            outputDbfs: -32.1,
            inputDbfs: -52.4,
            maskSmoothing: 0.054,
            peakEvidence: 0.174,
            outputPeakDbfs: -21.2,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            adjacentNoiseUsable: false);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(
            state.DesiredGainDb <= 18.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(
            state.OutputRmsDbfs <= -33.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsDeepWeakNr5LiftWhenOnlyFreshPeakIsFar()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 40.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-61.0));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.291,
            signalProbability: 0.140,
            agcGate: 0.547,
            recoveryDrive: 0.381,
            weakSignalMemory: 0.373,
            outputDbfs: -38.5,
            inputDbfs: -53.2,
            maskSmoothing: 0.367,
            peakEvidence: 0.0,
            outputPeakDbfs: -28.1,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            adjacentNoiseUsable: false);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -56_569, snrDb: 20.7, dbfs: -85.1, confidence: 0.882));

        Assert.True(
            state.DesiredGainDb <= 21.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(
            state.OutputRmsDbfs <= -40.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsWrongSidebandNr5LiftUsingSignedFilterPassband()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 27.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-50.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.321,
            signalProbability: 0.133,
            agcGate: 0.297,
            recoveryDrive: 0.385,
            weakSignalMemory: 0.349,
            outputDbfs: -39.5,
            inputDbfs: -52.7,
            maskSmoothing: 0.077,
            peakEvidence: 0.003,
            outputPeakDbfs: -29.8,
            adjacentNoiseTrust: 0.189,
            adjacentNoiseDrive: 0.121);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -559, snrDb: 23.1, dbfs: -81.5, confidence: 0.914),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb <= 13.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(
            state.OutputRmsDbfs <= -38.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsFrontendConfirmedSuppressedNr5WeakOnset()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-100.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.282,
            signalProbability: 0.145,
            agcGate: 0.245,
            recoveryDrive: 0.160,
            weakSignalMemory: 0.012,
            outputDbfs: -77.4,
            inputDbfs: -55.1,
            maskSmoothing: 0.386,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.782,
            adjacentNoiseDrive: 0.724);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 850, snrDb: 20.0, confidence: 0.875));

        Assert.True(
            state.DesiredGainDb >= 35.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 30.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.False(state.BoostSlewLimited);
        Assert.InRange(state.OutputRmsDbfs, -70.5, -58.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftSuppressedNr5FloorWithoutPassbandPeak()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-100.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.282,
            signalProbability: 0.145,
            agcGate: 0.245,
            recoveryDrive: 0.160,
            weakSignalMemory: 0.012,
            outputDbfs: -77.4,
            inputDbfs: -55.1,
            maskSmoothing: 0.386,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.782,
            adjacentNoiseDrive: 0.724);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -101.0, -100.0);
        Assert.Equal(0, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsNativeSuppressedNr5WeakFragmentWithoutFrontendPeak()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-87.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.252,
            signalProbability: 0.108,
            agcGate: 0.200,
            recoveryDrive: 0.016,
            weakSignalMemory: 0.0,
            outputDbfs: -65.0,
            inputDbfs: -44.0,
            maskSmoothing: 0.340,
            peakEvidence: 0.0,
            outputPeakDbfs: -49.5,
            adjacentNoiseTrust: 0.480,
            adjacentNoiseDrive: 0.400);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(
            state.DesiredGainDb >= 20.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 20.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.False(state.BoostSlewLimited);
        Assert.InRange(state.OutputRmsDbfs, -68.0, -60.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsDeepNativeSuppressedNr5WeakFragmentWithPassbandPeak()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 0.0,
            PauseHoldBlocks = 7,
            Nr5SpeechHoldBlocks = 15
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-104.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.219,
            signalProbability: 0.090,
            agcGate: 0.081,
            recoveryDrive: 0.016,
            weakSignalMemory: 0.0,
            outputDbfs: -80.7,
            inputDbfs: -45.6,
            maskSmoothing: 0.301,
            peakEvidence: 0.0,
            outputPeakDbfs: -71.9,
            adjacentNoiseTrust: 0.826,
            adjacentNoiseDrive: 0.786);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 1_188, snrDb: 17.0, dbfs: -87.1, confidence: 0.831),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb >= 35.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 29.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(state.BoostSlewLimited);
        Assert.InRange(state.OutputRmsDbfs, -77.0, -70.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsDeepNativeSuppressedNr5WeakFragmentAcrossPassbandDrop()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 0.0,
            PauseHoldBlocks = 7,
            Nr5SpeechHoldBlocks = 13
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-103.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.235,
            signalProbability: 0.095,
            agcGate: 0.089,
            recoveryDrive: 0.016,
            weakSignalMemory: 0.0,
            outputDbfs: -79.9,
            inputDbfs: -46.4,
            maskSmoothing: 0.309,
            peakEvidence: 0.0,
            outputPeakDbfs: -70.2,
            adjacentNoiseTrust: 0.728,
            adjacentNoiseDrive: 0.650);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(
            state.DesiredGainDb >= 35.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 29.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(state.BoostSlewLimited);
        Assert.InRange(state.OutputRmsDbfs, -76.5, -69.5);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftLowProofNativeSuppressedNr5WeakFragment()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-87.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.205,
            signalProbability: 0.050,
            agcGate: 0.115,
            recoveryDrive: 0.006,
            weakSignalMemory: 0.0,
            outputDbfs: -65.0,
            inputDbfs: -44.0,
            maskSmoothing: 0.240,
            peakEvidence: 0.0,
            outputPeakDbfs: -49.5,
            adjacentNoiseTrust: 0.220,
            adjacentNoiseDrive: 0.180);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -88.0, -87.0);
        Assert.Equal(0, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsPassbandBackedDeepNativeSuppressedNr5FragmentPastGenericMax()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 36.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-103.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.215,
            signalProbability: 0.072,
            agcGate: 0.087,
            recoveryDrive: 0.014,
            weakSignalMemory: 0.0,
            outputDbfs: -81.5,
            inputDbfs: -39.0,
            maskSmoothing: 0.250,
            peakEvidence: 0.0,
            outputPeakDbfs: -64.3,
            adjacentNoiseTrust: 0.765,
            adjacentNoiseDrive: 0.711,
            levelDrive: 0.075);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 1_000, snrDb: 19.1, dbfs: -85.1, confidence: 0.860),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -58.0, -53.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftDeepNativeSuppressedNr5FragmentPastGenericMaxWhenPeakIsFar()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 36.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-103.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.215,
            signalProbability: 0.072,
            agcGate: 0.087,
            recoveryDrive: 0.014,
            weakSignalMemory: 0.0,
            outputDbfs: -81.5,
            inputDbfs: -39.0,
            maskSmoothing: 0.250,
            peakEvidence: 0.0,
            outputPeakDbfs: -64.3,
            adjacentNoiseTrust: 0.765,
            adjacentNoiseDrive: 0.711,
            levelDrive: 0.075);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 43_625, snrDb: 22.3, dbfs: -83.9, confidence: 0.900),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -104.0, -102.5);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsPassbandSuppressedNr5OnsetAfterHoldExpires()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-75.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.248,
            signalProbability: 0.069,
            agcGate: 0.384,
            recoveryDrive: 0.032,
            weakSignalMemory: 0.0,
            outputDbfs: -52.7,
            inputDbfs: -41.3,
            maskSmoothing: 0.337,
            peakEvidence: 0.0,
            outputPeakDbfs: -36.5,
            adjacentNoiseTrust: 0.261,
            adjacentNoiseDrive: 0.184,
            levelDrive: 0.755);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 1_000, snrDb: 15.4, dbfs: -88.9, confidence: 0.808),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb >= 21.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 21.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -54.5, -49.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftPassbandSuppressedNr5OnsetWhenPeakIsOutsideFilter()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-75.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.248,
            signalProbability: 0.069,
            agcGate: 0.384,
            recoveryDrive: 0.032,
            weakSignalMemory: 0.0,
            outputDbfs: -52.7,
            inputDbfs: -41.3,
            maskSmoothing: 0.337,
            peakEvidence: 0.0,
            outputPeakDbfs: -36.5,
            adjacentNoiseTrust: 0.261,
            adjacentNoiseDrive: 0.184,
            levelDrive: 0.755);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 6_000, snrDb: 21.2, dbfs: -83.3, confidence: 0.888),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -76.0, -74.5);
        Assert.Equal(0, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldPassbandSuppressedNr5OnsetWithLowAgcGate()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-82.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.249,
            signalProbability: 0.085,
            agcGate: 0.186,
            recoveryDrive: 0.046,
            weakSignalMemory: 0.0,
            outputDbfs: -60.2,
            inputDbfs: -39.8,
            maskSmoothing: 0.293,
            peakEvidence: 0.0,
            outputPeakDbfs: -44.4,
            adjacentNoiseTrust: 0.519,
            adjacentNoiseDrive: 0.384,
            levelDrive: 0.494);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 1_000, snrDb: 15.0, dbfs: -89.0, confidence: 0.804),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb >= 27.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 27.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -56.0, -52.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsLiveHeldPassbandSuppressedNr5Dropout()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 31.6,
            PauseHoldBlocks = 17,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-87.1));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.223,
            signalProbability: 0.081,
            agcGate: 0.224,
            recoveryDrive: 0.018,
            weakSignalMemory: 0.0,
            outputDbfs: -61.5,
            inputDbfs: -41.7,
            maskSmoothing: 0.327,
            peakEvidence: 0.0,
            outputPeakDbfs: -44.3,
            adjacentNoiseTrust: 0.504,
            adjacentNoiseDrive: 0.401,
            levelDrive: 0.394);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 797, snrDb: 14.8, dbfs: -89.3, confidence: 0.801),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb >= 31.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 31.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -57.5, -53.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsPassbandBackedNr5WarmupBlackout()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = -6.3
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-26.4));
        block[0] = DbToLinear(-22.2);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.159,
            signalProbability: 0.021,
            agcGate: 0.0,
            recoveryDrive: 0.0,
            weakSignalMemory: 0.0,
            outputDbfs: -76.4,
            inputDbfs: -27.8,
            maskSmoothing: 0.0,
            peakEvidence: 0.0,
            outputPeakDbfs: -70.9,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            adjacentNoiseUsable: true,
            levelDrive: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -187, snrDb: 42.7, dbfs: -33.6, confidence: 0.940),
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.True(
            state.DesiredGainDb >= 7.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, gainDelta={state.GainDeltaDb:F2}");
        Assert.True(
            state.AppliedGainDb >= 7.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, gainDelta={state.GainDeltaDb:F2}");
        Assert.InRange(state.OutputRmsDbfs, -20.5, -17.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftNr5WarmupBlackoutWithoutPassbandProof()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = -6.3
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-26.4));
        block[0] = DbToLinear(-22.2);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.159,
            signalProbability: 0.021,
            agcGate: 0.0,
            recoveryDrive: 0.0,
            weakSignalMemory: 0.0,
            outputDbfs: -76.4,
            inputDbfs: -27.8,
            maskSmoothing: 0.0,
            peakEvidence: 0.0,
            outputPeakDbfs: -70.9,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            adjacentNoiseUsable: true,
            levelDrive: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            frontendTopPeak: null,
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.True(
            state.AppliedGainDb <= 1.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, gainDelta={state.GainDeltaDb:F2}");
        Assert.True(state.OutputRmsDbfs <= -25.0, $"output={state.OutputRmsDbfs:F1}");
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldPassbandSuppressedNr5OnsetWithVeryLowAgcWhenAdjacentAgrees()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-88.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.235,
            signalProbability: 0.094,
            agcGate: 0.095,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -53.3,
            inputDbfs: -39.9,
            maskSmoothing: 0.383,
            peakEvidence: 0.0,
            outputPeakDbfs: -37.3,
            adjacentNoiseTrust: 0.838,
            adjacentNoiseDrive: 0.808,
            levelDrive: 0.186);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 938, snrDb: 15.0, dbfs: -88.8, confidence: 0.803),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb >= 38.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 38.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -50.5, -47.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftVeryLowAgcHeldPassbandOnsetWithWeakAdjacentProfile()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-88.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.235,
            signalProbability: 0.094,
            agcGate: 0.095,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -53.3,
            inputDbfs: -39.9,
            maskSmoothing: 0.383,
            peakEvidence: 0.0,
            outputPeakDbfs: -37.3,
            adjacentNoiseTrust: 0.335,
            adjacentNoiseDrive: 0.232,
            levelDrive: 0.186);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 938, snrDb: 15.0, dbfs: -88.8, confidence: 0.803),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -89.0, -88.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsLongHeldPassbandSuppressedNr5OnsetWithModestAdjacentSupport()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 18
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-84.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.231,
            signalProbability: 0.101,
            agcGate: 0.159,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -61.2,
            inputDbfs: -41.6,
            maskSmoothing: 0.358,
            peakEvidence: 0.0,
            outputPeakDbfs: -44.1,
            adjacentNoiseTrust: 0.319,
            adjacentNoiseDrive: 0.219,
            levelDrive: 0.494);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 750, snrDb: 14.3, dbfs: -89.0, confidence: 0.800),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb >= 29.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 29.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -56.0, -51.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_BridgesHeldPassbandFrameWhenNativeNr5OutputIsStrong()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-81.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.305,
            signalProbability: 0.166,
            agcGate: 0.179,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -43.8,
            inputDbfs: -40.5,
            maskSmoothing: 0.367,
            peakEvidence: 0.0,
            outputPeakDbfs: -26.3,
            adjacentNoiseTrust: 0.290,
            adjacentNoiseDrive: 0.200,
            levelDrive: 0.580);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 813, snrDb: 14.2, dbfs: -89.1, confidence: 0.793),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb >= 35.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 35.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -47.0, -44.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotBridgeStrongNativeNr5OutputWhenPeakIsOutsideFilter()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-81.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.305,
            signalProbability: 0.166,
            agcGate: 0.179,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -43.8,
            inputDbfs: -40.5,
            maskSmoothing: 0.367,
            peakEvidence: 0.0,
            outputPeakDbfs: -26.3,
            adjacentNoiseTrust: 0.290,
            adjacentNoiseDrive: 0.200,
            levelDrive: 0.580);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -60_750, snrDb: 16.0, dbfs: -88.0, confidence: 0.820),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -82.0, -80.5);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsAdjacentBackedHeldSuppressedNr5OnsetWhenFrontendPeakIsMissing()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            PauseHoldBlocks = 16,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-106.1));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.224,
            signalProbability: 0.098,
            agcGate: 0.083,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -61.4,
            inputDbfs: -42.3,
            maskSmoothing: 0.416,
            peakEvidence: 0.0,
            outputPeakDbfs: -45.5,
            adjacentNoiseTrust: 0.868,
            adjacentNoiseDrive: 0.841,
            levelDrive: 0.186);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 25_250, snrDb: 17.9, dbfs: -88.0, confidence: 0.850),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -60.0, -56.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsAdjacentBackedDeepNativeSuppressedNr5FragmentWhenFrontendPeakIsMissing()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 36.0,
            PauseHoldBlocks = 16,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-103.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.206,
            signalProbability: 0.070,
            agcGate: 0.090,
            recoveryDrive: 0.012,
            weakSignalMemory: 0.0,
            outputDbfs: -82.0,
            inputDbfs: -39.1,
            maskSmoothing: 0.297,
            peakEvidence: 0.0,
            outputPeakDbfs: -65.5,
            adjacentNoiseTrust: 0.848,
            adjacentNoiseDrive: 0.802,
            levelDrive: 0.186);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 25_250, snrDb: 17.9, dbfs: -88.0, confidence: 0.850),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -58.5, -54.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsDominantPassbandHeldNativeSuppressedNr5FragmentWithLowNativeConfidence()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-105.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.186,
            signalProbability: 0.056,
            agcGate: 0.263,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -83.7,
            inputDbfs: -52.1,
            maskSmoothing: 0.327,
            peakEvidence: 0.0,
            outputPeakDbfs: -72.8,
            adjacentNoiseTrust: 0.374,
            adjacentNoiseDrive: 0.259,
            levelDrive: 0.186);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 375, snrDb: 26.0, dbfs: -78.4, confidence: 0.940),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -60.5, -56.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_AcquiresWeakPassbandNr5SpeechWithoutExistingHold()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-81.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.239,
            signalProbability: 0.077,
            agcGate: 0.168,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -61.1,
            inputDbfs: -40.9,
            maskSmoothing: 0.323,
            peakEvidence: 0.0,
            outputPeakDbfs: -45.0,
            adjacentNoiseTrust: 0.537,
            adjacentNoiseDrive: 0.465,
            levelDrive: 0.186);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 938, snrDb: 14.3, dbfs: -89.3, confidence: 0.795),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb >= 24.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 24.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -59.0, -53.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldPassbandBackedDeepNr5FragmentWithModestAdjacentSupport()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 36.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 23
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-104.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.206,
            signalProbability: 0.058,
            agcGate: 0.110,
            recoveryDrive: 0.012,
            weakSignalMemory: 0.0,
            outputDbfs: -82.4,
            inputDbfs: -39.7,
            maskSmoothing: 0.260,
            peakEvidence: 0.0,
            outputPeakDbfs: -66.4,
            adjacentNoiseTrust: 0.776,
            adjacentNoiseDrive: 0.668,
            levelDrive: 0.186);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 688, snrDb: 14.7, dbfs: -89.2, confidence: 0.800),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb > 44.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -58.5, -54.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_AddsQuietNr5ComfortTailForHeldSuppressedSpeech()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 18.7,
            PauseHoldBlocks = 0,
            Nr5SpeechHoldBlocks = 3
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-102.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.242,
            signalProbability: 0.106,
            agcGate: 0.219,
            recoveryDrive: 0.029,
            weakSignalMemory: 0.001,
            outputDbfs: -79.3,
            inputDbfs: -51.4,
            maskSmoothing: 0.363,
            peakEvidence: 0.0,
            outputPeakDbfs: -68.7,
            adjacentNoiseTrust: 0.484,
            adjacentNoiseDrive: 0.411);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 7.0, 12.0);
        Assert.InRange(state.AppliedGainDb, 7.0, 12.0);
        Assert.InRange(state.OutputRmsDbfs, -96.0, -90.0);
        Assert.Equal(2, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotAddQuietNr5ComfortTailWithoutHeldSpeech()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-102.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.242,
            signalProbability: 0.106,
            agcGate: 0.219,
            recoveryDrive: 0.029,
            weakSignalMemory: 0.001,
            outputDbfs: -79.3,
            inputDbfs: -51.4,
            maskSmoothing: 0.363,
            peakEvidence: 0.0,
            outputPeakDbfs: -68.7,
            adjacentNoiseTrust: 0.484,
            adjacentNoiseDrive: 0.411);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -103.2, -102.2);
        Assert.Equal(0, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsFrontendConfirmedHeldSuppressedNr5SpeechTail()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 11.6,
            PauseHoldBlocks = 13,
            Nr5SpeechHoldBlocks = 8
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-76.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.273,
            signalProbability: 0.139,
            agcGate: 0.277,
            recoveryDrive: 0.171,
            weakSignalMemory: 0.078,
            outputDbfs: -77.4,
            inputDbfs: -56.5,
            maskSmoothing: 0.221,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.364,
            adjacentNoiseDrive: 0.245);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 524, snrDb: 12.2, dbfs: -91.4, confidence: 0.765));

        Assert.True(
            state.DesiredGainDb >= 20.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 20.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -58.0, -48.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftHeldSuppressedNr5TailWithoutPassbandPeak()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 11.6,
            PauseHoldBlocks = 13,
            Nr5SpeechHoldBlocks = 8
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-76.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.273,
            signalProbability: 0.139,
            agcGate: 0.277,
            recoveryDrive: 0.171,
            weakSignalMemory: 0.078,
            outputDbfs: -77.4,
            inputDbfs: -56.5,
            maskSmoothing: 0.221,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.364,
            adjacentNoiseDrive: 0.245);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 4_180, snrDb: 14.4, dbfs: -92.0, confidence: 0.796));

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -77.0, -75.5);
        Assert.Equal(7, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsFrontendConfirmedHeldSuppressedNr5ModerateTail()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 39.2,
            PauseHoldBlocks = 17,
            Nr5SpeechHoldBlocks = 22
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-73.1));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.272,
            signalProbability: 0.125,
            agcGate: 0.316,
            recoveryDrive: 0.185,
            weakSignalMemory: 0.078,
            outputDbfs: -57.8,
            inputDbfs: -38.0,
            maskSmoothing: 0.220,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.458,
            adjacentNoiseDrive: 0.335);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 242, snrDb: 13.8, dbfs: -91.0, confidence: 0.788));

        Assert.True(
            state.DesiredGainDb >= 24.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.True(
            state.AppliedGainDb >= 24.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, hold={state.Nr5SpeechHoldBlocks}");
        Assert.InRange(state.OutputRmsDbfs, -40.0, -34.0);
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_BridgesFrontendConfirmedPassbandSuppressedValley()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 4.7,
            PauseHoldBlocks = 16,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-57.0));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.213,
            signalProbability: 0.056,
            agcGate: 0.434,
            recoveryDrive: 0.031,
            weakSignalMemory: 0.001,
            outputDbfs: -47.8,
            inputDbfs: -28.4,
            maskSmoothing: 0.012,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.054,
            adjacentNoiseDrive: 0.020);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 0, snrDb: 39.4, dbfs: -64.5, confidence: 0.94));

        Assert.True(
            state.AppliedGainDb >= 22.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.False(state.BoostSlewLimited);
        Assert.True(
            state.OutputRmsDbfs >= -36.0 && state.OutputRmsDbfs <= -28.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, delta={state.GainDeltaDb:F2}");
        Assert.Equal(26, state.Nr5SpeechHoldBlocks);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotBridgePassbandSuppressedValleyWhenPeakIsFar()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 4.7,
            PauseHoldBlocks = 16,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-57.0));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.213,
            signalProbability: 0.056,
            agcGate: 0.434,
            recoveryDrive: 0.031,
            weakSignalMemory: 0.001,
            outputDbfs: -47.8,
            inputDbfs: -28.4,
            maskSmoothing: 0.012,
            peakEvidence: 0.0,
            adjacentNoiseTrust: 0.054,
            adjacentNoiseDrive: 0.020);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 6_200, snrDb: 39.4, dbfs: -64.5, confidence: 0.94));

        Assert.True(
            state.AppliedGainDb <= 14.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.InRange(state.OutputRmsDbfs, -50.0, -42.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsNr5LiftWhenFrontendPeakIsOutsidePassband()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-52.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.394,
            signalProbability: 0.176,
            agcGate: 0.564,
            recoveryDrive: 0.527,
            weakSignalMemory: 0.078,
            outputDbfs: -30.8,
            inputDbfs: -51.2,
            maskSmoothing: 0.345,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -132_976, snrDb: 37.1, dbfs: -67.0, confidence: 0.94));

        Assert.True(
            state.DesiredGainDb <= 10.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.InRange(state.OutputRmsDbfs, -44.5, -42.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_ReleasesHotNr5GainWhenFrontendPeakDisagrees()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 34.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-52.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.394,
            signalProbability: 0.176,
            agcGate: 0.564,
            recoveryDrive: 0.527,
            weakSignalMemory: 0.078,
            outputDbfs: -30.8,
            inputDbfs: -51.2,
            maskSmoothing: 0.345,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -132_976, snrDb: 37.1, dbfs: -67.0, confidence: 0.94));

        Assert.True(
            state.AppliedGainDb <= 10.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.InRange(state.OutputRmsDbfs, -44.5, -42.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsHighNativePeakNr5LiftWhenFrontendPeakDisagrees()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 32.4,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-50.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.381,
            signalProbability: 0.346,
            agcGate: 0.570,
            recoveryDrive: 0.620,
            weakSignalMemory: 0.611,
            outputDbfs: -26.5,
            inputDbfs: -48.3,
            maskSmoothing: 0.357,
            peakEvidence: 0.542);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 131_024, snrDb: 26.6, dbfs: -75.0, confidence: 0.94));

        Assert.True(
            state.AppliedGainDb <= 8.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.InRange(state.OutputRmsDbfs, -44.5, -42.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_PreservesWeakNr5LevelWhenFrontendDisagrees()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 14.0,
            PauseHoldBlocks = 12,
            Nr5SpeechHoldBlocks = 12
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-68.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.264,
            signalProbability: 0.118,
            agcGate: 0.520,
            recoveryDrive: 0.175,
            weakSignalMemory: 0.041,
            outputDbfs: -57.4,
            inputDbfs: -49.3,
            maskSmoothing: 0.347,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -57_039, snrDb: 12.8, dbfs: -92.0, confidence: 0.774));

        Assert.True(
            state.DesiredGainDb >= 22.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.InRange(state.OutputRmsDbfs, -47.0, -43.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsMarginalEdgePassbandNr5LiftWithoutNativeAgreement()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 42.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-62.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.311,
            signalProbability: 0.160,
            agcGate: 0.54,
            recoveryDrive: 0.360,
            weakSignalMemory: 0.22,
            outputDbfs: -38.4,
            inputDbfs: -43.4,
            maskSmoothing: 0.383,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 2_201, snrDb: 16.6, dbfs: -91.1, confidence: 0.794));

        Assert.True(
            state.AppliedGainDb <= 20.5,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.InRange(state.OutputRmsDbfs, -45.0, -42.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsHotNr5LiftWhenFrontendPeakIsInsidePassband()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-52.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.394,
            signalProbability: 0.176,
            agcGate: 0.564,
            recoveryDrive: 0.527,
            weakSignalMemory: 0.078,
            outputDbfs: -30.8,
            inputDbfs: -51.2,
            maskSmoothing: 0.345,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 850, snrDb: 18.0, dbfs: -84.0, confidence: 0.86));

        Assert.InRange(state.DesiredGainDb, 18.0, 21.0);
        Assert.InRange(state.OutputRmsDbfs, -35.5, -33.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotCapModerateNr5SpeechWithoutHotOutputProof()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 30.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-52.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.312,
            signalProbability: 0.116,
            agcGate: 0.410,
            recoveryDrive: 0.262,
            weakSignalMemory: 0.180,
            outputDbfs: -44.0,
            inputDbfs: -52.4,
            maskSmoothing: 0.320,
            peakEvidence: 0.0,
            outputPeakDbfs: -34.0);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 850, snrDb: 18.0, dbfs: -84.0, confidence: 0.86));

        Assert.True(
            state.DesiredGainDb >= 23.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.True(
            state.AppliedGainDb >= 23.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}");
        Assert.InRange(state.OutputRmsDbfs, -27.5, -22.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsDeepWeakNr5SpeechWhenSpectralProofAgrees()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 29.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 10
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-65.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.31,
            signalProbability: 0.17,
            agcGate: 0.54,
            recoveryDrive: 0.36,
            weakSignalMemory: 0.28,
            outputDbfs: -42.0,
            inputDbfs: -55.4,
            maskSmoothing: 0.30,
            peakEvidence: 0.02);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb >= 34.0);
        Assert.True(state.AppliedGainDb >= 34.0);
        Assert.False(state.BoostSlewLimited);
        Assert.InRange(state.OutputRmsDbfs, -32.5, -28.5);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DropsHeldLowProofNr5FloorInsteadOfNormalizingIt()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 34.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-65.6));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.331,
            signalProbability: 0.181,
            agcGate: 0.316,
            recoveryDrive: 0.322,
            weakSignalMemory: 0.189,
            outputDbfs: -40.6,
            inputDbfs: -55.3,
            maskSmoothing: 0.361,
            peakEvidence: 0.079);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb < 28.0);
        Assert.True(state.AppliedGainDb < 28.0);
        Assert.True(state.GainDeltaDb < -6.0);
        Assert.InRange(state.OutputRmsDbfs, -42.5, -37.5);
        Assert.InRange(state.Nr5SpeechHoldBlocks, 20, 25);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsNativeNr5WeakSpeechOnset()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 24.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-64.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.319,
            signalProbability: 0.184,
            agcGate: 0.399,
            recoveryDrive: 0.221,
            weakSignalMemory: 0.115,
            outputDbfs: -40.6,
            inputDbfs: -52.6,
            maskSmoothing: 0.360,
            peakEvidence: 0.028);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 20.0, 24.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -45.0, -40.0);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_NormalizesStrongNr5SpeechWhenProofAgrees()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 32.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-48.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.368,
            signalProbability: 0.213,
            agcGate: 0.556,
            recoveryDrive: 0.42,
            weakSignalMemory: 0.442,
            outputDbfs: -24.0,
            inputDbfs: -27.3,
            maskSmoothing: 0.340,
            peakEvidence: 0.239);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 18.0, 19.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -29.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsStrongNr5FrameAfterWeakMakeupWhenPeakProofIsLow()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 36.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-51.0));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.375,
            signalProbability: 0.218,
            agcGate: 0.411,
            recoveryDrive: 0.034,
            weakSignalMemory: 0.0,
            outputDbfs: -26.2,
            inputDbfs: -39.7,
            maskSmoothing: 0.296,
            peakEvidence: 0.0,
            outputPeakDbfs: -10.5,
            adjacentNoiseTrust: 0.503,
            adjacentNoiseDrive: 0.410,
            levelDrive: 0.186);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 1_031, snrDb: 14.7, dbfs: -89.0, confidence: 0.800),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.True(
            state.DesiredGainDb <= 12.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, delta={state.GainDeltaDb:F2}");
        Assert.True(
            state.AppliedGainDb <= 12.0,
            $"desired={state.DesiredGainDb:F2}, applied={state.AppliedGainDb:F2}, output={state.OutputRmsDbfs:F1}, delta={state.GainDeltaDb:F2}");
        Assert.InRange(state.OutputRmsDbfs, -42.5, -39.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsHighProofStrongNr5SpeechToPreventHotPumping()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 31.5,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-47.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.385,
            signalProbability: 0.659,
            agcGate: 0.816,
            recoveryDrive: 1.0,
            weakSignalMemory: 0.620,
            outputDbfs: -26.4,
            inputDbfs: -48.4,
            maskSmoothing: 0.332,
            peakEvidence: 1.0,
            outputPeakDbfs: -15.4,
            adjacentNoiseTrust: 0.801,
            adjacentNoiseDrive: 0.741,
            levelDrive: 0.977);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 1_125, snrDb: 14.9, dbfs: -89.2, confidence: 0.802),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 17.0, 19.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsHotNr5SpeechAtUpperStrongInputBoundary()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 25.4,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-43.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.390,
            signalProbability: 0.247,
            agcGate: 0.528,
            recoveryDrive: 0.448,
            weakSignalMemory: 0.435,
            outputDbfs: -21.7,
            inputDbfs: -50.0,
            maskSmoothing: 0.320,
            peakEvidence: 0.275,
            outputPeakDbfs: -12.8,
            adjacentNoiseTrust: 0.856,
            adjacentNoiseDrive: 0.809,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 469, snrDb: 13.4, dbfs: -90.0, confidence: 0.782),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 13.0, 15.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsNearFieldStrongNr5SpeechFromLive40mTrace()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 20.7,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-38.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.374,
            signalProbability: 0.207,
            agcGate: 0.876,
            recoveryDrive: 0.555,
            weakSignalMemory: 0.240,
            outputDbfs: -22.6,
            inputDbfs: -16.3,
            maskSmoothing: 0.0,
            peakEvidence: 0.217,
            outputPeakDbfs: -11.4,
            adjacentNoiseTrust: 0.049,
            adjacentNoiseDrive: 0.018,
            levelDrive: 0.997);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -375, snrDb: 35.9, dbfs: -54.4, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 8.0, 10.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsNearFieldStrongNr5SpeechWhenAgcGateLags()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 19.3,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-37.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.379,
            signalProbability: 0.211,
            agcGate: 0.561,
            recoveryDrive: 0.684,
            weakSignalMemory: 0.008,
            outputDbfs: -22.9,
            inputDbfs: -13.5,
            maskSmoothing: 0.021,
            peakEvidence: 0.226,
            outputPeakDbfs: -13.2,
            adjacentNoiseTrust: 0.082,
            adjacentNoiseDrive: 0.023,
            levelDrive: 0.949);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -375, snrDb: 35.9, dbfs: -54.4, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 6.5, 9.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsNearFieldStrongNr5SpeechWithModerateNativeOutput()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 21.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-41.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.353,
            signalProbability: 0.169,
            agcGate: 0.840,
            recoveryDrive: 0.723,
            weakSignalMemory: 0.685,
            outputDbfs: -27.9,
            inputDbfs: -44.6,
            maskSmoothing: 0.296,
            peakEvidence: 0.131,
            outputPeakDbfs: -15.4,
            adjacentNoiseTrust: 0.443,
            adjacentNoiseDrive: 0.355,
            levelDrive: 0.999);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 80_000, snrDb: 27.4, dbfs: -59.6, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 10.5, 13.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsNativeHotWeakNr5SpeechWithModestAgcGate()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 25.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-44.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.389,
            signalProbability: 0.234,
            agcGate: 0.364,
            recoveryDrive: 0.523,
            weakSignalMemory: 0.602,
            outputDbfs: -29.2,
            inputDbfs: -43.4,
            maskSmoothing: 0.320,
            peakEvidence: 0.352,
            outputPeakDbfs: -17.4,
            adjacentNoiseTrust: 0.002,
            adjacentNoiseDrive: 0.001,
            levelDrive: 0.532);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -5_250, snrDb: 31.6, dbfs: -53.5, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 13.0, 16.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -28.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsPassbandConfirmedHotNr5SpeechWithLowConfidence()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 20.4,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 21
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-34.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.311,
            signalProbability: 0.141,
            agcGate: 0.826,
            recoveryDrive: 0.284,
            weakSignalMemory: 0.215,
            outputDbfs: -20.5,
            inputDbfs: -14.3,
            maskSmoothing: 0.018,
            peakEvidence: 0.0,
            outputPeakDbfs: -9.3,
            adjacentNoiseTrust: 0.083,
            adjacentNoiseDrive: 0.026,
            levelDrive: 0.990);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -2_812, snrDb: 25.7, dbfs: -55.2, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 5.0, 6.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -30.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsPassbandConfirmedModerateNr5OutputBurst()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 19.5,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-40.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.348,
            signalProbability: 0.130,
            agcGate: 0.861,
            recoveryDrive: 0.397,
            weakSignalMemory: 0.385,
            outputDbfs: -31.4,
            inputDbfs: -42.2,
            maskSmoothing: 0.032,
            peakEvidence: 0.114,
            outputPeakDbfs: -19.6,
            adjacentNoiseTrust: 0.045,
            adjacentNoiseDrive: 0.015,
            levelDrive: 0.998);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -2_250, snrDb: 27.8, dbfs: -58.4, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 9.0, 11.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotUsePassbandBurstCapForOffPassbandPeak()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 19.5,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-40.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.348,
            signalProbability: 0.130,
            agcGate: 0.861,
            recoveryDrive: 0.397,
            weakSignalMemory: 0.385,
            outputDbfs: -31.4,
            inputDbfs: -42.2,
            maskSmoothing: 0.032,
            peakEvidence: 0.114,
            outputPeakDbfs: -19.6,
            adjacentNoiseTrust: 0.045,
            adjacentNoiseDrive: 0.015,
            levelDrive: 0.998);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -12_250, snrDb: 27.8, dbfs: -58.4, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.True(state.DesiredGainDb > 14.0, $"desired={state.DesiredGainDb:F2}");
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldNativeSpeechDespiteFrontendDisagreement()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 9.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-42.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.353,
            signalProbability: 0.194,
            agcGate: 0.890,
            recoveryDrive: 0.514,
            weakSignalMemory: 0.587,
            outputDbfs: -27.0,
            inputDbfs: -43.3,
            maskSmoothing: 0.0,
            peakEvidence: 0.121,
            outputPeakDbfs: -16.8,
            adjacentNoiseTrust: 0.553,
            adjacentNoiseDrive: 0.497,
            levelDrive: 0.999);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 64_688, snrDb: 23.1, dbfs: -70.3, confidence: 0.914),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 7.0, 9.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -35.5, -33.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldNativeSpeechWhenAgcGateIsLowButContinuityIsStrong()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 2.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-44.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.315,
            signalProbability: 0.175,
            agcGate: 0.434,
            recoveryDrive: 0.593,
            weakSignalMemory: 0.715,
            outputDbfs: -30.5,
            inputDbfs: -44.6,
            maskSmoothing: 0.0,
            peakEvidence: 0.134,
            outputPeakDbfs: -21.0,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.662);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -10_500, snrDb: 24.3, dbfs: -76.2, confidence: 0.931),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 8.0, 10.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -37.0, -34.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldNativeSpeechWhenAgcMemorySupportsMarginalProbability()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 0.0,
            PauseHoldBlocks = 0,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-42.1));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.319,
            signalProbability: 0.155,
            agcGate: 0.789,
            recoveryDrive: 0.507,
            weakSignalMemory: 0.576,
            outputDbfs: -30.8,
            inputDbfs: -40.9,
            maskSmoothing: 0.336,
            peakEvidence: 0.032,
            outputPeakDbfs: -20.3,
            adjacentNoiseTrust: 0.356,
            adjacentNoiseDrive: 0.313,
            levelDrive: 0.983);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -30_750, snrDb: 41.8, dbfs: -46.7, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 6.0, 8.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -36.0, -34.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldHotNativeOnsetDespiteFrontendDisagreement()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 1.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-43.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.322,
            signalProbability: 0.205,
            agcGate: 0.421,
            recoveryDrive: 0.443,
            weakSignalMemory: 0.472,
            outputDbfs: -27.9,
            inputDbfs: -36.2,
            maskSmoothing: 0.0,
            peakEvidence: 0.141,
            outputPeakDbfs: -17.7,
            adjacentNoiseTrust: 0.001,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.694);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -6_750, snrDb: 23.8, dbfs: -66.6, confidence: 0.923),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 7.0, 9.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -37.0, -34.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_KeepsFrontendDisagreementCapForLowProofNativeTail()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 10.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-42.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.300,
            signalProbability: 0.150,
            agcGate: 0.700,
            recoveryDrive: 0.450,
            weakSignalMemory: 0.540,
            outputDbfs: -31.0,
            inputDbfs: -43.2,
            maskSmoothing: 0.0,
            peakEvidence: 0.070,
            outputPeakDbfs: -22.0,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.900);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 64_688, snrDb: 23.1, dbfs: -70.3, confidence: 0.914),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 0.0, 1.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -43.0, -41.8);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsNoFrontendLowProofHeldSpeechFromLiveTrace()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 17.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-44.3));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.282,
            signalProbability: 0.144,
            agcGate: 0.834,
            recoveryDrive: 0.460,
            weakSignalMemory: 0.500,
            outputDbfs: -29.5,
            inputDbfs: -32.9,
            maskSmoothing: 0.317,
            peakEvidence: 0.0,
            outputPeakDbfs: -20.6,
            adjacentNoiseTrust: 0.022,
            adjacentNoiseDrive: 0.013,
            levelDrive: 0.979);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            frontendTopPeak: null,
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 6.0, 9.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -38.5, -34.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsOffPassbandLowProofWeakBoundaryFromLiveTrace()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 20.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-40.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.336,
            signalProbability: 0.120,
            agcGate: 0.909,
            recoveryDrive: 0.512,
            weakSignalMemory: 0.584,
            outputDbfs: -29.7,
            inputDbfs: -42.1,
            maskSmoothing: 0.0,
            peakEvidence: 0.135,
            outputPeakDbfs: -17.1,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.999);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -5_250, snrDb: 39.5, dbfs: -50.4, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 5.0, 7.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -36.5, -33.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsOffPassbandHeldTailFromLive40mTrace()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 20.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-40.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.310,
            signalProbability: 0.138,
            agcGate: 0.747,
            recoveryDrive: 0.479,
            weakSignalMemory: 0.531,
            outputDbfs: -32.4,
            inputDbfs: -43.1,
            maskSmoothing: 0.339,
            peakEvidence: 0.0,
            outputPeakDbfs: -21.2,
            adjacentNoiseTrust: 0.002,
            adjacentNoiseDrive: 0.002,
            levelDrive: 0.989);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -6_562, snrDb: 23.9, dbfs: -63.7, confidence: 0.925),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 5.0, 7.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -36.5, -33.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsHotLowProofG2TailFromLive40mTrace()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 9.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-40.5));
        block[0] = DbToLinear(-23.6);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.329,
            signalProbability: 0.169,
            agcGate: 0.898,
            recoveryDrive: 0.508,
            weakSignalMemory: 0.578,
            outputDbfs: -26.7,
            inputDbfs: -40.0,
            maskSmoothing: 0.281,
            peakEvidence: 0.066,
            outputPeakDbfs: -14.1,
            adjacentNoiseTrust: 0.067,
            adjacentNoiseDrive: 0.044,
            levelDrive: 0.998);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 49_875, snrDb: 55.1, dbfs: -35.4, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 7.0, 9.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -34.5, -31.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_KeepsPeakProvenG2SpeechAboveHotTailCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 9.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-39.8));
        block[0] = DbToLinear(-27.8);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.476,
            signalProbability: 0.483,
            agcGate: 0.720,
            recoveryDrive: 1.000,
            weakSignalMemory: 0.568,
            outputDbfs: -23.7,
            inputDbfs: -38.6,
            maskSmoothing: 0.333,
            peakEvidence: 1.000,
            outputPeakDbfs: -13.2,
            adjacentNoiseTrust: 0.405,
            adjacentNoiseDrive: 0.359,
            levelDrive: 0.974);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 49_875, snrDb: 55.1, dbfs: -35.4, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 9.5, 12.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -30.0, -27.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsModerateHotLowProofG2TailFromLive40mTrace()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 9.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-40.1));
        block[0] = DbToLinear(-27.6);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.327,
            signalProbability: 0.254,
            agcGate: 0.943,
            recoveryDrive: 0.622,
            weakSignalMemory: 0.616,
            outputDbfs: -25.1,
            inputDbfs: -42.4,
            maskSmoothing: 0.380,
            peakEvidence: 0.364,
            outputPeakDbfs: -13.5,
            adjacentNoiseTrust: 0.115,
            adjacentNoiseDrive: 0.056,
            levelDrive: 1.000);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 82_875, snrDb: 50.1, dbfs: -40.4, confidence: 0.940),
            filterLowHz: -3_960,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 9.0, 11.8);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -30.5, -28.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsStrong7305HighBoostToSpeechParity()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 20.7,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-38.7));
        block[0] = DbToLinear(-29.7);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.370,
            signalProbability: 0.200,
            agcGate: 0.485,
            recoveryDrive: 0.314,
            weakSignalMemory: 0.050,
            outputDbfs: -26.3,
            inputDbfs: -25.7,
            maskSmoothing: 0.0,
            peakEvidence: 0.191,
            outputPeakDbfs: -17.6,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.958);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -45_125, snrDb: 57.2, dbfs: -40.4, confidence: 0.940),
            filterLowHz: -4_655,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 8.0, 10.8);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -30.5, -28.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsPassbandLowProof7305HighBoostToSpeechParity()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 19.2,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 24
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-41.8));
        block[0] = DbToLinear(-29.6);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.304,
            signalProbability: 0.176,
            agcGate: 0.874,
            recoveryDrive: 0.226,
            weakSignalMemory: 0.078,
            outputDbfs: -27.5,
            inputDbfs: -22.7,
            maskSmoothing: 0.078,
            peakEvidence: 0.038,
            outputPeakDbfs: -17.3,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.999);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -187, snrDb: 33.9, dbfs: -32.5, confidence: 0.940),
            filterLowHz: -4_655,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 10.5, 13.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -30.5, -28.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsStrongPassband7305HighBoostToSpeechParity()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 17.4,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-35.4));
        block[0] = DbToLinear(-26.4);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.361,
            signalProbability: 0.214,
            agcGate: 0.570,
            recoveryDrive: 0.647,
            weakSignalMemory: 0.011,
            outputDbfs: -22.9,
            inputDbfs: -17.4,
            maskSmoothing: 0.0,
            peakEvidence: 0.160,
            outputPeakDbfs: -14.2,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 1.000);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -187, snrDb: 36.7, dbfs: -32.0, confidence: 0.940),
            filterLowHz: -4_655,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 5.5, 8.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -30.5, -28.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsFarOnly7305NoiseOpenHotRow()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 10.3,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-39.1));
        block[0] = DbToLinear(-27.8);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.409,
            signalProbability: 0.116,
            agcGate: 0.978,
            recoveryDrive: 0.915,
            weakSignalMemory: 0.595,
            outputDbfs: -24.2,
            inputDbfs: -39.5,
            maskSmoothing: 0.332,
            peakEvidence: 0.743,
            outputPeakDbfs: -13.7,
            adjacentNoiseTrust: 0.544,
            adjacentNoiseDrive: 0.500,
            levelDrive: 0.999);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 184_875, snrDb: 54.8, dbfs: -111.9, confidence: 0.940),
            filterLowHz: -4_655,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 1.0, 3.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -38.5, -36.5);
        Assert.True(state.Nr5NoSignalNoiseCap);
        Assert.True(state.Nr5FarPeakNoiseCap);
        Assert.True(state.Nr5NoProofNoiseCap);
        Assert.InRange(state.Nr5NoSignalNoisePrior, 0.220, 1.0);
        Assert.InRange(state.Nr5NoiseProfilePrior, 0.030, 0.145);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_RequiresStableAdjacentNoSignalProfileBeforeNoProofCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 16.0,
            PauseHoldBlocks = 18
        };
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.255,
            signalProbability: 0.140,
            agcGate: 0.500,
            recoveryDrive: 0.060,
            weakSignalMemory: 0.050,
            outputDbfs: -24.0,
            inputDbfs: -39.1,
            maskSmoothing: 0.020,
            peakEvidence: 0.0,
            outputPeakDbfs: -13.0,
            adjacentNoiseTrust: 0.850,
            adjacentNoiseDrive: 0.820,
            levelDrive: 0.900);

        float[] first = new float[1024];
        Array.Fill(first, DbToLinear(-39.1));
        first[0] = DbToLinear(-27.8);

        DspPipelineService.ApplyRxAudioLeveler(first, ref state, nr5);

        double firstDesiredDb = state.DesiredGainDb;
        Assert.False(state.Nr5NoSignalNoiseCap);
        Assert.True(state.Nr5NoiseProfilePrior > 0.145);

        for (int i = 0; i < 3; i++)
        {
            float[] next = new float[1024];
            Array.Fill(next, DbToLinear(-39.1));
            next[0] = DbToLinear(-27.8);
            DspPipelineService.ApplyRxAudioLeveler(next, ref state, nr5);
        }

        Assert.True(state.Nr5NoSignalNoiseCap);
        Assert.False(state.Nr5FarPeakNoiseCap);
        Assert.True(state.Nr5NoProofNoiseCap);
        Assert.InRange(state.Nr5NoiseProfilePrior, 0.145, 1.0);
        Assert.True(state.DesiredGainDb < firstDesiredDb - 8.0);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    public void ApplyRxAudioLeveler_RmNoiseGateAcceptsExplicitOnValues(string optInValue)
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE");
        string? previousExperimental = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", null);
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", optInValue);
        try
        {
            var state = StableNoSignalRmNoiseState();
            float[] block = StableNoSignalRmNoiseBlock();
            var nr5 = StableNoSignalRmNoiseDiagnostics();

            DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

            Assert.True(
                state.Nr5RmNoiseGate,
                $"prior={state.Nr5NoSignalNoisePrior:F3} profile={state.Nr5NoiseProfilePrior:F3} " +
                $"cap={state.Nr5NoSignalNoiseCap} noProof={state.Nr5NoProofNoiseCap} " +
                $"desired={state.DesiredGainDb:F1} output={state.OutputRmsDbfs:F1}");
            Assert.True(state.Nr5NoSignalNoiseCap);
            Assert.True(state.Nr5NoProofNoiseCap);
            Assert.InRange(state.Nr5RmNoiseSuppressionDb, 12.0, 80.0);
            Assert.True(state.DesiredGainDb <= -14.0);
            Assert.True(state.OutputRmsDbfs <= -50.0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", previous);
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previousExperimental);
        }
    }

    [Fact]
    public void ApplyRxAudioLeveler_RmNoiseGateIsDefaultOn()
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE");
        string? previousExperimental = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", null);
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", null);
        try
        {
            var state = StableNoSignalRmNoiseState();
            float[] block = StableNoSignalRmNoiseBlock();

            DspPipelineService.ApplyRxAudioLeveler(block, ref state, StableNoSignalRmNoiseDiagnostics());

            Assert.True(state.Nr5RmNoiseGate);
            Assert.InRange(state.Nr5RmNoiseSuppressionDb, 12.0, 80.0);
            Assert.True(state.Nr5NoSignalNoiseCap);
            Assert.True(state.Nr5NoProofNoiseCap);
            Assert.True(state.OutputRmsDbfs <= -50.0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", previous);
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previousExperimental);
        }
    }

    [Fact]
    public void ApplyRxAudioLeveler_RmNoiseGateSuppressesQuietResidualNoSignalFloor()
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE");
        string? previousExperimental = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", null);
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", null);
        try
        {
            var state = new DspPipelineService.RxAudioLevelerState
            {
                Nr5NoiseProfilePrior = 0.350
            };
            float[] block = new float[1024];
            Array.Fill(block, DbToLinear(-79.9));
            block[0] = DbToLinear(-67.0);
            var nr5 = Nr5Diagnostics(
                signalConfidence: 0.227,
                signalProbability: 0.082,
                agcGate: 0.144,
                recoveryDrive: 0.020,
                weakSignalMemory: 0.0,
                outputDbfs: -80.6,
                inputDbfs: -57.1,
                maskSmoothing: 0.372,
                peakEvidence: 0.0,
                outputPeakDbfs: -67.0,
                adjacentNoiseTrust: 0.547,
                adjacentNoiseDrive: 0.514,
                adjacentNoiseUsable: true,
                levelDrive: 0.230);

            DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

            Assert.True(
                state.Nr5RmNoiseGate,
                $"prior={state.Nr5NoSignalNoisePrior:F3} profile={state.Nr5NoiseProfilePrior:F3} " +
                $"desired={state.DesiredGainDb:F1} applied={state.AppliedGainDb:F1} " +
                $"input={state.InputRmsDbfs:F1} output={state.OutputRmsDbfs:F1}");
            Assert.True(state.Nr5NoSignalNoiseCap);
            Assert.True(state.Nr5NoProofNoiseCap);
            Assert.InRange(state.Nr5RmNoiseSuppressionDb, 10.0, 30.0);
            Assert.True(state.DesiredGainDb <= -10.0);
            Assert.True(state.AppliedGainDb <= -10.0);
            Assert.True(state.OutputRmsDbfs <= -91.0);
            Assert.Equal(0, state.Nr5SpeechHoldBlocks);
            Assert.False(state.OutputLimited);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", previous);
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previousExperimental);
        }
    }

    [Fact]
    public void ApplyRxAudioLeveler_RmNoiseQuietGatePreservesPassbandSpeechProof()
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE");
        string? previousExperimental = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", null);
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", null);
        try
        {
            var state = new DspPipelineService.RxAudioLevelerState
            {
                Nr5NoiseProfilePrior = 0.350
            };
            float[] block = new float[1024];
            Array.Fill(block, DbToLinear(-79.9));
            block[0] = DbToLinear(-67.0);
            var nr5 = Nr5Diagnostics(
                signalConfidence: 0.227,
                signalProbability: 0.082,
                agcGate: 0.144,
                recoveryDrive: 0.020,
                weakSignalMemory: 0.0,
                outputDbfs: -80.6,
                inputDbfs: -57.1,
                maskSmoothing: 0.372,
                peakEvidence: 0.0,
                outputPeakDbfs: -67.0,
                adjacentNoiseTrust: 0.547,
                adjacentNoiseDrive: 0.514,
                adjacentNoiseUsable: true,
                levelDrive: 0.230);

            DspPipelineService.ApplyRxAudioLeveler(
                block,
                ref state,
                nr5,
                FrontendTopPeak(offsetHz: 1_050, snrDb: 18.4, dbfs: -90.0, confidence: 0.850),
                filterLowHz: 100,
                filterHighHz: 3_100);

            Assert.False(state.Nr5RmNoiseGate);
            Assert.True(double.IsNaN(state.Nr5RmNoiseSuppressionDb));
            Assert.True(state.OutputRmsDbfs > -82.0);
            Assert.False(state.OutputLimited);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", previous);
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previousExperimental);
        }
    }

    [Fact]
    public void ApplyRxAudioLeveler_RmNoiseGateSuppressesHeldQuietResidualTail()
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE");
        string? previousExperimental = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", null);
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", null);
        try
        {
            var state = new DspPipelineService.RxAudioLevelerState
            {
                PauseHoldBlocks = 17,
                Nr5SpeechHoldBlocks = 25,
                Nr5NoiseProfilePrior = 0.022
            };
            float[] block = new float[1024];
            Array.Fill(block, DbToLinear(-85.8));
            block[0] = DbToLinear(-73.2);
            var nr5 = Nr5Diagnostics(
                signalConfidence: 0.240,
                signalProbability: 0.082,
                agcGate: 0.153,
                recoveryDrive: 0.085,
                weakSignalMemory: 0.0,
                outputDbfs: -80.0,
                inputDbfs: -54.4,
                maskSmoothing: 0.374,
                peakEvidence: 0.0,
                outputPeakDbfs: -67.8,
                adjacentNoiseTrust: 0.546,
                adjacentNoiseDrive: 0.507,
                adjacentNoiseUsable: true,
                levelDrive: 0.239);

            DspPipelineService.ApplyRxAudioLeveler(
                block,
                ref state,
                nr5,
                FrontendTopPeak(offsetHz: -187, snrDb: 10.8, dbfs: -93.8, confidence: 0.747),
                filterLowHz: -3_106,
                filterHighHz: 0);

            Assert.True(
                state.Nr5RmNoiseGate,
                $"hybrid={state.Nr5HybridSpeechPrior:F3} prior={state.Nr5NoSignalNoisePrior:F3} " +
                $"profile={state.Nr5NoiseProfilePrior:F3} desired={state.DesiredGainDb:F1} " +
                $"output={state.OutputRmsDbfs:F1}");
            Assert.True(state.Nr5NoProofNoiseCap);
            Assert.InRange(state.Nr5RmNoiseSuppressionDb, 5.0, 14.0);
            Assert.True(state.DesiredGainDb <= -5.0);
            Assert.True(state.OutputRmsDbfs <= -90.0);
            Assert.Equal(0, state.PauseHoldBlocks);
            Assert.InRange(state.Nr5SpeechHoldBlocks, 1, 24);
            Assert.False(state.OutputLimited);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", previous);
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previousExperimental);
        }
    }

    [Fact]
    public void ApplyRxAudioLeveler_RmNoiseHeldQuietTailPreservesPassbandProof()
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE");
        string? previousExperimental = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", null);
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", null);
        try
        {
            var state = new DspPipelineService.RxAudioLevelerState
            {
                PauseHoldBlocks = 17,
                Nr5SpeechHoldBlocks = 25,
                Nr5NoiseProfilePrior = 0.022
            };
            float[] block = new float[1024];
            Array.Fill(block, DbToLinear(-85.8));
            block[0] = DbToLinear(-73.2);
            var nr5 = Nr5Diagnostics(
                signalConfidence: 0.240,
                signalProbability: 0.082,
                agcGate: 0.153,
                recoveryDrive: 0.085,
                weakSignalMemory: 0.0,
                outputDbfs: -80.0,
                inputDbfs: -54.4,
                maskSmoothing: 0.374,
                peakEvidence: 0.0,
                outputPeakDbfs: -67.8,
                adjacentNoiseTrust: 0.546,
                adjacentNoiseDrive: 0.507,
                adjacentNoiseUsable: true,
                levelDrive: 0.239);

            DspPipelineService.ApplyRxAudioLeveler(
                block,
                ref state,
                nr5,
                FrontendTopPeak(offsetHz: -187, snrDb: 18.4, dbfs: -93.8, confidence: 0.850),
                filterLowHz: -3_106,
                filterHighHz: 0);

            Assert.False(state.Nr5RmNoiseGate);
            Assert.True(double.IsNaN(state.Nr5RmNoiseSuppressionDb));
            Assert.True(state.OutputRmsDbfs > -88.0);
            Assert.True(state.Nr5SpeechHoldBlocks > 0);
            Assert.False(state.OutputLimited);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", previous);
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previousExperimental);
        }
    }

    [Fact]
    public void ApplyRxAudioLeveler_RmNoiseGateDefersDuringRecentSpeechHangover()
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE");
        string? previousExperimental = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", null);
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", null);
        try
        {
            var state = StableNoSignalRmNoiseState();
            state.Nr5SpeechHangoverBlocks = 48;
            float[] block = StableNoSignalRmNoiseBlock();

            DspPipelineService.ApplyRxAudioLeveler(block, ref state, StableNoSignalRmNoiseDiagnostics());

            Assert.False(
                state.Nr5RmNoiseGate,
                $"hangover={state.Nr5SpeechHangoverBlocks} hold={state.Nr5SpeechHoldBlocks} " +
                $"prior={state.Nr5NoSignalNoisePrior:F3} profile={state.Nr5NoiseProfilePrior:F3} " +
                $"desired={state.DesiredGainDb:F1} output={state.OutputRmsDbfs:F1}");
            Assert.True(double.IsNaN(state.Nr5RmNoiseSuppressionDb));
            Assert.Equal(0, state.Nr5SpeechHoldBlocks);
            Assert.InRange(state.Nr5SpeechHangoverBlocks, 1, 47);
            Assert.True(state.OutputRmsDbfs > -50.0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", previous);
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previousExperimental);
        }
    }

    [Fact]
    public void ApplyRxAudioLeveler_ProtectsRecentNr5SpeechHangoverFromComfortFade()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 11.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHangoverBlocks = 34,
            Nr5NoiseProfilePrior = 0.350
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-56.6));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.252,
            signalProbability: 0.102,
            agcGate: 0.338,
            recoveryDrive: 0.174,
            weakSignalMemory: 0.039,
            outputDbfs: -50.6,
            inputDbfs: -54.7,
            maskSmoothing: 0.390,
            peakEvidence: 0.0,
            outputPeakDbfs: -40.5,
            adjacentNoiseTrust: 0.542,
            adjacentNoiseDrive: 0.508,
            levelDrive: 0.620);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 55_500, snrDb: 16.1, dbfs: -86.0, confidence: 0.819),
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.False(state.Nr5RmNoiseGate);
        Assert.True(double.IsNaN(state.Nr5RmNoiseSuppressionDb));
        Assert.True(
            state.OutputRmsDbfs is >= -38.5 and <= -34.0,
            $"desired={state.DesiredGainDb:F1} applied={state.AppliedGainDb:F1} output={state.OutputRmsDbfs:F1} " +
            $"input={state.InputRmsDbfs:F1} hold={state.Nr5SpeechHoldBlocks} hang={state.Nr5SpeechHangoverBlocks} " +
            $"hybrid={state.Nr5HybridSpeechPrior:F3} noSignal={state.Nr5NoSignalNoisePrior:F3} " +
            $"profile={state.Nr5NoiseProfilePrior:F3} noCap={state.Nr5NoSignalNoiseCap} " +
            $"farCap={state.Nr5FarPeakNoiseCap} proofCap={state.Nr5NoProofNoiseCap}");
        Assert.True(
            state.AppliedGainDb >= 18.0,
            $"desired={state.DesiredGainDb:F1} applied={state.AppliedGainDb:F1} output={state.OutputRmsDbfs:F1}");
        Assert.Equal(0, state.Nr5SpeechHoldBlocks);
        Assert.InRange(state.Nr5SpeechHangoverBlocks, 1, 33);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_ProtectsLowSmoothingNr5SpeechHangoverFromComfortFade()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 0.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 12,
            Nr5SpeechHangoverBlocks = 58,
            Nr5NoiseProfilePrior = 0.338
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-46.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.263,
            signalProbability: 0.120,
            agcGate: 0.432,
            recoveryDrive: 0.185,
            weakSignalMemory: 0.056,
            outputDbfs: -49.3,
            inputDbfs: -52.3,
            maskSmoothing: 0.135,
            peakEvidence: 0.0,
            outputPeakDbfs: -39.6,
            adjacentNoiseTrust: 0.519,
            adjacentNoiseDrive: 0.464,
            levelDrive: 0.620);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -56_437, snrDb: 20.4, dbfs: -86.0, confidence: 0.877),
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.False(state.Nr5RmNoiseGate);
        Assert.True(double.IsNaN(state.Nr5RmNoiseSuppressionDb));
        Assert.True(
            state.OutputRmsDbfs >= -36.5,
            $"desired={state.DesiredGainDb:F1} applied={state.AppliedGainDb:F1} output={state.OutputRmsDbfs:F1} " +
            $"input={state.InputRmsDbfs:F1} hold={state.Nr5SpeechHoldBlocks} hang={state.Nr5SpeechHangoverBlocks} " +
            $"hybrid={state.Nr5HybridSpeechPrior:F3} noSignal={state.Nr5NoSignalNoisePrior:F3} " +
            $"profile={state.Nr5NoiseProfilePrior:F3} noCap={state.Nr5NoSignalNoiseCap} " +
            $"farCap={state.Nr5FarPeakNoiseCap} proofCap={state.Nr5NoProofNoiseCap}");
        Assert.True(
            state.AppliedGainDb >= 10.0,
            $"desired={state.DesiredGainDb:F1} applied={state.AppliedGainDb:F1} output={state.OutputRmsDbfs:F1}");
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.True(state.Nr5SpeechHangoverBlocks > 0);
        Assert.False(state.OutputLimited);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("off")]
    [InlineData("no")]
    public void ApplyRxAudioLeveler_RmNoiseGateAcceptsExplicitOffValues(string offValue)
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE");
        string? previousExperimental = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", offValue);
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", "1");
        try
        {
            var state = StableNoSignalRmNoiseState();
            float[] block = StableNoSignalRmNoiseBlock();

            DspPipelineService.ApplyRxAudioLeveler(block, ref state, StableNoSignalRmNoiseDiagnostics());

            Assert.False(state.Nr5RmNoiseGate);
            Assert.True(double.IsNaN(state.Nr5RmNoiseSuppressionDb));
            Assert.True(state.Nr5NoSignalNoiseCap);
            Assert.True(state.Nr5NoProofNoiseCap);
            Assert.True(state.OutputRmsDbfs > -50.0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_NR5_RMNOISE_GATE", previous);
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previousExperimental);
        }
    }

    private static DspPipelineService.RxAudioLevelerState StableNoSignalRmNoiseState() =>
        new()
        {
            GainDb = 0.0,
            PauseHoldBlocks = 18,
            Nr5NoiseProfilePrior = 0.620
        };

    private static float[] StableNoSignalRmNoiseBlock()
    {
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-39.1));
        block[0] = DbToLinear(-27.8);
        return block;
    }

    private static Nr5SpnrDiagnosticsDto StableNoSignalRmNoiseDiagnostics() =>
        Nr5Diagnostics(
            signalConfidence: 0.255,
            signalProbability: 0.140,
            agcGate: 0.500,
            recoveryDrive: 0.060,
            weakSignalMemory: 0.050,
            outputDbfs: -24.0,
            inputDbfs: -39.1,
            maskSmoothing: 0.020,
            peakEvidence: 0.0,
            outputPeakDbfs: -13.0,
            adjacentNoiseTrust: 0.850,
            adjacentNoiseDrive: 0.820,
            levelDrive: 0.900);

    [Fact]
    public void ApplyRxAudioLeveler_PassbandSpeechClearsNoSignalProfileCap()
    {
        string? previous = Environment.GetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE");
        Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", "1");
        try
        {
            var state = new DspPipelineService.RxAudioLevelerState
            {
                GainDb = 16.0,
                PauseHoldBlocks = 18,
                Nr5NoiseProfilePrior = 0.620
            };
            float[] block = new float[1024];
            Array.Fill(block, DbToLinear(-45.0));
            block[0] = DbToLinear(-34.0);
            var nr5 = Nr5Diagnostics(
                signalConfidence: 0.360,
                signalProbability: 0.240,
                agcGate: 0.720,
                recoveryDrive: 0.420,
                weakSignalMemory: 0.480,
                outputDbfs: -32.0,
                inputDbfs: -45.0,
                maskSmoothing: 0.340,
                peakEvidence: 0.180,
                outputPeakDbfs: -22.0,
                adjacentNoiseTrust: 0.820,
                adjacentNoiseDrive: 0.800,
                levelDrive: 0.900);

            DspPipelineService.ApplyRxAudioLeveler(
                block,
                ref state,
                nr5,
                FrontendTopPeak(offsetHz: 160, snrDb: 31.0, dbfs: -74.0, confidence: 0.920),
                filterLowHz: 100,
                filterHighHz: 3_100);

            Assert.False(state.Nr5NoSignalNoiseCap);
            Assert.False(state.Nr5FarPeakNoiseCap);
            Assert.False(state.Nr5NoProofNoiseCap);
            Assert.False(state.Nr5RmNoiseGate);
            Assert.True(double.IsNaN(state.Nr5RmNoiseSuppressionDb));
            Assert.True(state.Nr5HybridSpeechPrior > state.Nr5NoSignalNoisePrior);
            Assert.InRange(state.Nr5NoiseProfilePrior, 0.250, 0.360);
            Assert.InRange(state.OutputRmsDbfs, -37.0, -28.0);
            Assert.True(state.Nr5SpeechHangoverBlocks > state.Nr5SpeechHoldBlocks);

            float[] noProofBlock = new float[1024];
            Array.Fill(noProofBlock, DbToLinear(-39.1));
            noProofBlock[0] = DbToLinear(-27.8);
            var noProofNr5 = Nr5Diagnostics(
                signalConfidence: 0.255,
                signalProbability: 0.140,
                agcGate: 0.500,
                recoveryDrive: 0.060,
                weakSignalMemory: 0.050,
                outputDbfs: -24.0,
                inputDbfs: -39.1,
                maskSmoothing: 0.020,
                peakEvidence: 0.0,
                outputPeakDbfs: -13.0,
                adjacentNoiseTrust: 0.850,
                adjacentNoiseDrive: 0.820,
                levelDrive: 0.900);

            DspPipelineService.ApplyRxAudioLeveler(noProofBlock, ref state, noProofNr5);

            Assert.True(state.Nr5SpeechHoldBlocks > 0);
            Assert.True(state.Nr5SpeechHangoverBlocks > state.Nr5SpeechHoldBlocks);
            Assert.False(state.Nr5NoSignalNoiseCap);
            Assert.False(state.Nr5FarPeakNoiseCap);
            Assert.False(state.Nr5NoProofNoiseCap);
            Assert.False(state.Nr5RmNoiseGate);
            Assert.True(double.IsNaN(state.Nr5RmNoiseSuppressionDb));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEUS_EXPERIMENTAL_NR5_RMNOISE_GATE", previous);
        }
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsFarOnly7305HeldTailNoiseOpenHotRow()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 10.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-40.3));
        block[0] = DbToLinear(-29.7);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.315,
            signalProbability: 0.155,
            agcGate: 0.867,
            recoveryDrive: 0.464,
            weakSignalMemory: 0.506,
            outputDbfs: -31.3,
            inputDbfs: -41.4,
            maskSmoothing: 0.332,
            peakEvidence: 0.0,
            outputPeakDbfs: -18.8,
            adjacentNoiseTrust: 0.533,
            adjacentNoiseDrive: 0.482,
            levelDrive: 0.992);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 184_875, snrDb: 45.5, dbfs: -114.0, confidence: 0.940),
            filterLowHz: -4_655,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 5.5, 8.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -35.0, -32.8);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsStrong7337LowProbabilityBoostToSpeechParity()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 12.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-38.3));
        block[0] = DbToLinear(-34.4);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.313,
            signalProbability: 0.153,
            agcGate: 0.809,
            recoveryDrive: 0.0,
            weakSignalMemory: 0.0,
            outputDbfs: -22.8,
            inputDbfs: -9.6,
            maskSmoothing: 0.0,
            peakEvidence: 0.0,
            outputPeakDbfs: -16.6,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 1.000);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -2_062, snrDb: 54.0, dbfs: -38.0, confidence: 0.940),
            filterLowHz: -4_655,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 8.5, 11.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -29.5, -27.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsStrong7337HighProbabilityBoostToSpeechParity()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 9.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-39.6));
        block[0] = DbToLinear(-36.3);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.457,
            signalProbability: 0.495,
            agcGate: 1.000,
            recoveryDrive: 0.0,
            weakSignalMemory: 0.0,
            outputDbfs: -25.5,
            inputDbfs: -16.6,
            maskSmoothing: 0.0,
            peakEvidence: 1.000,
            outputPeakDbfs: -22.2,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 1.000);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -2_062, snrDb: 48.0, dbfs: -38.0, confidence: 0.940),
            filterLowHz: -4_655,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 10.5, 12.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -28.5, -27.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsStrong7337FadedInputBoostToSpeechParity()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 12.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-44.5));
        block[0] = DbToLinear(-35.1);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.283,
            signalProbability: 0.134,
            agcGate: 0.498,
            recoveryDrive: 0.0,
            weakSignalMemory: 0.0,
            outputDbfs: -29.0,
            inputDbfs: -17.4,
            maskSmoothing: 0.0,
            peakEvidence: 0.0,
            outputPeakDbfs: -21.5,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.998);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -2_062, snrDb: 54.2, dbfs: -44.0, confidence: 0.940),
            filterLowHz: -4_655,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 14.0, 17.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -30.5, -28.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldPassbandStrongUnderReportedNr5Syllable()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 3.7,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 19
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-30.2));
        block[0] = DbToLinear(-27.9);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.250,
            signalProbability: 0.016,
            agcGate: 0.586,
            recoveryDrive: 0.102,
            weakSignalMemory: 0.001,
            outputDbfs: -26.1,
            inputDbfs: -11.1,
            maskSmoothing: 0.059,
            peakEvidence: 0.0,
            outputPeakDbfs: -25.0,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 1.000);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -187, snrDb: 40.7, dbfs: -32.9, confidence: 0.940),
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.InRange(state.DesiredGainDb, 9.0, 13.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -21.5, -17.0);
        Assert.True(state.Nr5HybridSpeechPrior > 0.20);
        Assert.False(state.Nr5NoSignalNoiseCap);
        Assert.False(state.Nr5RmNoiseGate);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftHeldUnderReportedNr5SyllableWithoutPassbandProof()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 3.7,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 19
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-30.2));
        block[0] = DbToLinear(-27.9);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.250,
            signalProbability: 0.016,
            agcGate: 0.586,
            recoveryDrive: 0.102,
            weakSignalMemory: 0.001,
            outputDbfs: -26.1,
            inputDbfs: -11.1,
            maskSmoothing: 0.059,
            peakEvidence: 0.0,
            outputPeakDbfs: -25.0,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 1.000);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 0.0, 4.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.OutputRmsDbfs <= -25.0);
        Assert.False(state.Nr5NoSignalNoiseCap);
        Assert.False(state.Nr5RmNoiseGate);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsDeepHeldPassbandStrongUnderReportedNr5Syllable()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 14.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-34.1));
        block[0] = DbToLinear(-27.6);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.210,
            signalProbability: 0.023,
            agcGate: 0.682,
            recoveryDrive: 0.155,
            weakSignalMemory: 0.008,
            outputDbfs: -34.5,
            inputDbfs: -15.1,
            maskSmoothing: 0.191,
            peakEvidence: 0.0,
            outputPeakDbfs: -31.3,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.999);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -187, snrDb: 57.4, dbfs: -41.8, confidence: 0.940),
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.InRange(state.DesiredGainDb, 13.0, 17.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -21.5, -17.0);
        Assert.False(state.Nr5NoSignalNoiseCap);
        Assert.False(state.Nr5RmNoiseGate);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftDeepHeldUnderReportedNr5SyllableWithoutPassbandProof()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 14.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-34.1));
        block[0] = DbToLinear(-27.6);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.210,
            signalProbability: 0.023,
            agcGate: 0.682,
            recoveryDrive: 0.155,
            weakSignalMemory: 0.008,
            outputDbfs: -34.5,
            inputDbfs: -15.1,
            maskSmoothing: 0.191,
            peakEvidence: 0.0,
            outputPeakDbfs: -31.3,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.999);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 0.0, 6.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.OutputRmsDbfs <= -28.0);
        Assert.False(state.Nr5NoSignalNoiseCap);
        Assert.False(state.Nr5RmNoiseGate);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsHeldPassbandWeakUnderReportedNr5Syllable()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 12.1,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 17
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-41.1));
        block[0] = DbToLinear(-34.4);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.289,
            signalProbability: 0.146,
            agcGate: 0.566,
            recoveryDrive: 0.133,
            weakSignalMemory: 0.005,
            outputDbfs: -29.5,
            inputDbfs: -21.8,
            maskSmoothing: 0.386,
            peakEvidence: 0.0,
            outputPeakDbfs: -24.1,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.998);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -187, snrDb: 45.9, dbfs: -45.0, confidence: 0.940),
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.InRange(state.DesiredGainDb, 18.0, 22.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -23.5, -18.5);
        Assert.False(state.Nr5NoSignalNoiseCap);
        Assert.False(state.Nr5RmNoiseGate);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsPassbandLowProofWeakBoundaryFromLiveTrace()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 20.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-42.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.298,
            signalProbability: 0.147,
            agcGate: 0.718,
            recoveryDrive: 0.479,
            weakSignalMemory: 0.530,
            outputDbfs: -30.6,
            inputDbfs: -42.9,
            maskSmoothing: 0.0,
            peakEvidence: 0.103,
            outputPeakDbfs: -18.2,
            adjacentNoiseTrust: 0.0,
            adjacentNoiseDrive: 0.0,
            levelDrive: 0.999);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -187, snrDb: 33.0, dbfs: -60.3, confidence: 0.940),
            filterLowHz: -4_189,
            filterHighHz: -100);

        Assert.InRange(state.DesiredGainDb, 7.0, 9.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -36.0, -33.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsPassbandLowProofContinuityPumpFromLive40mTrace()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 32.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-64.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.320,
            signalProbability: 0.149,
            agcGate: 0.294,
            recoveryDrive: 0.172,
            weakSignalMemory: 0.036,
            outputDbfs: -37.6,
            inputDbfs: -40.2,
            maskSmoothing: 0.0,
            peakEvidence: 0.0,
            outputPeakDbfs: -29.9,
            adjacentNoiseTrust: 0.538,
            adjacentNoiseDrive: 0.494,
            levelDrive: 0.511);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -1_500, snrDb: 25.6, dbfs: -79.2, confidence: 0.940),
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.InRange(state.DesiredGainDb, 23.6, 24.2);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -40.6, -40.0);
        Assert.True(
            state.GainDeltaDb < -7.0,
            $"desired={state.DesiredGainDb:F1} applied={state.AppliedGainDb:F1} delta={state.GainDeltaDb:F1}");
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotCapPassbandContinuityWithNativeProof()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 32.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 25
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-64.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.340,
            signalProbability: 0.205,
            agcGate: 0.460,
            recoveryDrive: 0.340,
            weakSignalMemory: 0.260,
            outputDbfs: -36.8,
            inputDbfs: -40.2,
            maskSmoothing: 0.260,
            peakEvidence: 0.045,
            outputPeakDbfs: -29.2,
            adjacentNoiseTrust: 0.538,
            adjacentNoiseDrive: 0.494,
            levelDrive: 0.620);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -1_500, snrDb: 25.6, dbfs: -79.2, confidence: 0.940),
            filterLowHz: -3_106,
            filterHighHz: 0);

        Assert.True(
            state.DesiredGainDb > 25.5,
            $"desired={state.DesiredGainDb:F1} applied={state.AppliedGainDb:F1} output={state.OutputRmsDbfs:F1}");
        Assert.True(
            state.OutputRmsDbfs > -38.5,
            $"desired={state.DesiredGainDb:F1} applied={state.AppliedGainDb:F1} output={state.OutputRmsDbfs:F1}");
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsLiveNr5RecoveryOvershootBelowStrongInputBoundary()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 35.5,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-56.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.308,
            signalProbability: 0.074,
            agcGate: 0.763,
            recoveryDrive: 0.488,
            weakSignalMemory: 0.545,
            outputDbfs: -29.5,
            inputDbfs: -56.7,
            maskSmoothing: 0.320,
            peakEvidence: 0.0,
            outputPeakDbfs: -23.3,
            adjacentNoiseTrust: 0.602,
            adjacentNoiseDrive: 0.492,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 141, snrDb: 25.8, dbfs: -88.0, confidence: 0.820),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 22.0, 26.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -35.0, -30.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsPeakProvenMidWeakNr5Overshoot()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 36.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-61.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.293,
            signalProbability: 0.152,
            agcGate: 0.694,
            recoveryDrive: 0.283,
            weakSignalMemory: 0.215,
            outputDbfs: -35.9,
            inputDbfs: -61.2,
            maskSmoothing: 0.320,
            peakEvidence: 0.0,
            outputPeakDbfs: -28.0,
            adjacentNoiseTrust: 0.403,
            adjacentNoiseDrive: 0.294,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 141, snrDb: 17.7, dbfs: -91.0, confidence: 0.720),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 27.0, 31.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -34.5, -29.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsAdjacentProfiledMidWeakNr5Overshoot()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 41.4,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-61.4));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.319,
            signalProbability: 0.167,
            agcGate: 0.503,
            recoveryDrive: 0.265,
            weakSignalMemory: 0.186,
            outputDbfs: -38.0,
            inputDbfs: -61.4,
            maskSmoothing: 0.320,
            peakEvidence: 0.018,
            outputPeakDbfs: -26.7,
            adjacentNoiseTrust: 0.847,
            adjacentNoiseDrive: 0.829,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 141, snrDb: 23.1, dbfs: -90.0, confidence: 0.760),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 28.0, 33.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -33.5, -28.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsLowConfidencePeakProvenWeakNr5Overshoot()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 29.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-54.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.274,
            signalProbability: 0.138,
            agcGate: 0.784,
            recoveryDrive: 0.429,
            weakSignalMemory: 0.449,
            outputDbfs: -34.4,
            inputDbfs: -54.7,
            maskSmoothing: 0.320,
            peakEvidence: 0.0,
            outputPeakDbfs: -23.8,
            adjacentNoiseTrust: 0.522,
            adjacentNoiseDrive: 0.387,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 141, snrDb: 24.5, dbfs: -90.0, confidence: 0.760),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 23.0, 27.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -32.0, -27.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsLowConfidencePeakProvenWeakBoundaryBelowStrongInput()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 38.8,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-58.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.272,
            signalProbability: 0.122,
            agcGate: 0.670,
            recoveryDrive: 0.398,
            weakSignalMemory: 0.400,
            outputDbfs: -31.7,
            inputDbfs: -58.2,
            maskSmoothing: 0.320,
            peakEvidence: 0.0,
            outputPeakDbfs: -22.6,
            adjacentNoiseTrust: 0.741,
            adjacentNoiseDrive: 0.658,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 1_453, snrDb: 26.0, dbfs: -88.0, confidence: 0.940),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 27.0, 31.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.0, -27.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CapsPassbandPeakWeakBoundaryWithCoolerNativeOutput()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 37.7,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-57.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.273,
            signalProbability: 0.141,
            agcGate: 0.698,
            recoveryDrive: 0.356,
            weakSignalMemory: 0.332,
            outputDbfs: -36.2,
            inputDbfs: -57.2,
            maskSmoothing: 0.320,
            peakEvidence: 0.0,
            outputPeakDbfs: -27.7,
            adjacentNoiseTrust: 0.741,
            adjacentNoiseDrive: 0.658,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 1_547, snrDb: 26.8, dbfs: -88.0, confidence: 0.940),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 26.5, 30.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -31.5, -27.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsPassbandConfirmedHotNr5SpeechAboveComfortCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 4.6,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-47.6));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.325,
            signalProbability: 0.338,
            agcGate: 0.881,
            recoveryDrive: 0.586,
            weakSignalMemory: 0.586,
            outputDbfs: -22.9,
            inputDbfs: -34.0,
            maskSmoothing: 0.014,
            peakEvidence: 0.425,
            outputPeakDbfs: -13.6,
            adjacentNoiseTrust: 0.738,
            adjacentNoiseDrive: 0.635,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -47, snrDb: 38.7, dbfs: -66.2, confidence: 0.940),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 14.0, 18.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -34.0, -30.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsNativeHotNr5SpeechAboveFarPeakComfortCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 13.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-57.2));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.345,
            signalProbability: 0.181,
            agcGate: 0.475,
            recoveryDrive: 0.304,
            weakSignalMemory: 0.230,
            outputDbfs: -25.7,
            inputDbfs: -32.6,
            maskSmoothing: 0.0,
            peakEvidence: 0.099,
            outputPeakDbfs: -18.4,
            adjacentNoiseTrust: 0.740,
            adjacentNoiseDrive: 0.658,
            levelDrive: 0.800);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 12_844, snrDb: 21.8, dbfs: -82.2, confidence: 0.820),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 21.0, 26.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -36.5, -31.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsAdjacentBackedNativeContinuityNr5SpeechAboveFadeCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 10.9,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-55.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.305,
            signalProbability: 0.131,
            agcGate: 0.773,
            recoveryDrive: 0.400,
            weakSignalMemory: 0.404,
            outputDbfs: -34.4,
            inputDbfs: -46.1,
            maskSmoothing: 0.284,
            peakEvidence: 0.0,
            outputPeakDbfs: -25.2,
            adjacentNoiseTrust: 0.714,
            adjacentNoiseDrive: 0.642,
            levelDrive: 0.969);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 15_625, snrDb: 21.4, dbfs: -79.3, confidence: 0.891),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 19.0, 24.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -37.0, -31.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsWeakMemoryNativeContinuityNr5SpeechAboveFadeCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 4.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-48.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.326,
            signalProbability: 0.162,
            agcGate: 0.633,
            recoveryDrive: 0.323,
            weakSignalMemory: 0.278,
            outputDbfs: -29.6,
            inputDbfs: -35.6,
            maskSmoothing: 0.018,
            peakEvidence: 0.020,
            outputPeakDbfs: -21.7,
            adjacentNoiseTrust: 0.717,
            adjacentNoiseDrive: 0.663,
            levelDrive: 0.947);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 23_250, snrDb: 26.9, dbfs: -73.2, confidence: 0.940),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 14.0, 19.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -35.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsNativeOnlyRecoveredNr5SpeechAboveFadeCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 5.1,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-48.9));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.325,
            signalProbability: 0.166,
            agcGate: 0.807,
            recoveryDrive: 0.431,
            weakSignalMemory: 0.453,
            outputDbfs: -29.3,
            inputDbfs: -32.6,
            maskSmoothing: 0.053,
            peakEvidence: 0.020,
            outputPeakDbfs: -20.5,
            adjacentNoiseTrust: 0.302,
            adjacentNoiseDrive: 0.234,
            levelDrive: 0.982);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -148_802, snrDb: 35.0, dbfs: -62.5, confidence: 0.940),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 14.0, 19.0);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -35.0, -29.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsLowAdjacentNativeContinuityNr5SpeechAboveFadeCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 10.7,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-55.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.318,
            signalProbability: 0.160,
            agcGate: 0.489,
            recoveryDrive: 0.303,
            weakSignalMemory: 0.247,
            outputDbfs: -37.6,
            inputDbfs: -43.9,
            maskSmoothing: 0.129,
            peakEvidence: 0.001,
            outputPeakDbfs: -26.1,
            adjacentNoiseTrust: 0.354,
            adjacentNoiseDrive: 0.286,
            levelDrive: 0.922);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: 141_823, snrDb: 28.8, dbfs: -71.2, confidence: 0.940),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.InRange(state.DesiredGainDb, 18.0, 23.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -38.0, -31.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftNoPassbandDeepNr5FloorAcrossHold()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 48.0,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 26
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-102.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.245,
            signalProbability: 0.110,
            agcGate: 0.122,
            recoveryDrive: 0.041,
            weakSignalMemory: 0.0,
            outputDbfs: -79.0,
            inputDbfs: -51.7,
            maskSmoothing: 0.320,
            peakEvidence: 0.0,
            outputPeakDbfs: -68.7,
            adjacentNoiseTrust: 0.403,
            adjacentNoiseDrive: 0.296,
            levelDrive: 0.433);

        DspPipelineService.ApplyRxAudioLeveler(
            block,
            ref state,
            nr5,
            FrontendTopPeak(offsetHz: -10_266, snrDb: 11.8, dbfs: -93.0, confidence: 0.720),
            filterLowHz: 100,
            filterHighHz: 3_100);

        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -103.5, -101.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotNormalizeStrongLookingNr5FloorWithoutProof()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 32.0 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-54.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.097,
            signalProbability: 0.018,
            agcGate: 0.0,
            recoveryDrive: 0.0,
            weakSignalMemory: 0.0,
            outputDbfs: -79.3,
            inputDbfs: -29.9,
            maskSmoothing: 0.020,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.InRange(state.DesiredGainDb, 9.5, 10.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -45.5, -43.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_RampsSmallCutsAcrossBlock()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 6.0 };
        float[] block = new float[1024];

        Array.Fill(block, 0.07943282f); // -22 dBFS, so target level asks for +4 dB.
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.Equal(4.0, state.GainDb, precision: 3);
        Assert.False(state.BoostSlewLimited);
        Assert.Equal(-2.0, state.GainDeltaDb, precision: 3);
        Assert.InRange(block[0], 0.157f, 0.159f);
        Assert.InRange(block[255], 0.125f, 0.127f);
        Assert.InRange(block[^1], 0.125f, 0.127f);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftBelowGateSilence()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, 0.00001f);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.InRange(Rms(block), 0.000009f, 0.000011f);
        Assert.Equal(0.0, state.GainDb);
        Assert.True(state.DiagnosticsValid);
        Assert.Equal(0.0, state.DesiredGainDb);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsBoostMemoryAcrossShortMutedGapWithoutLiftingFloor()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        for (int i = 0; i < 12; i++)
        {
            Array.Fill(block, 0.01f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        double heldGainDb = state.GainDb;
        Assert.True(heldGainDb > 20.0);
        Assert.True(state.PauseHoldBlocks > 0);

        Array.Fill(block, 0.00001f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.InRange(Rms(block), 0.000009f, 0.000011f);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.Equal(heldGainDb, state.GainDb, precision: 6);
        Assert.True(state.PauseHoldBlocks > 0);

        Array.Fill(block, 0.01f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.False(state.BoostSlewLimited);
        Assert.Equal(heldGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(Rms(block), 0.10f, 0.15f);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsBoostMemoryAcrossSsbWordGapWithoutLiftingFloor()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        for (int i = 0; i < 12; i++)
        {
            Array.Fill(block, 0.01f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        double heldGainDb = state.GainDb;
        Assert.True(heldGainDb > 20.0);

        for (int i = 0; i < 14; i++)
        {
            Array.Fill(block, 0.00001f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);

            Assert.InRange(Rms(block), 0.000009f, 0.000011f);
            Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
            Assert.Equal(heldGainDb, state.GainDb, precision: 6);
            Assert.True(state.PauseHoldBlocks > 0);
        }

        Array.Fill(block, 0.01f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.False(state.BoostSlewLimited);
        Assert.Equal(heldGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(Rms(block), 0.10f, 0.15f);
    }

    [Fact]
    public void ApplyRxAudioLeveler_CatchesUpWeakSpeechAfterLongMutedGapWithoutLiftingFloor()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        for (int i = 0; i < 12; i++)
        {
            Array.Fill(block, 0.01f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        double heldGainDb = state.GainDb;
        Assert.True(heldGainDb > 20.0);

        for (int i = 0; i < 20; i++)
        {
            Array.Fill(block, 0.00001f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);

            Assert.InRange(Rms(block), 0.000009f, 0.000011f);
            Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        }

        double decayedGainDb = state.GainDb;
        Assert.InRange(decayedGainDb, heldGainDb - 9.1, heldGainDb - 8.9);
        Assert.Equal(0, state.PauseHoldBlocks);

        for (int i = 0; i < block.Length; i++)
            block[i] = i % 16 == 0 ? 0.0032f : 0.0008f;

        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.True(state.BoostSlewLimited);
        Assert.Equal(decayedGainDb + 7.5, state.AppliedGainDb, precision: 6);
        Assert.Equal(7.5, state.GainDeltaDb, precision: 6);
        Assert.InRange(state.InputRmsDbfs, -59.5, -58.5);
        Assert.InRange(state.InputPeakDbfs, -50.1, -49.8);
        Assert.InRange(Rms(block), 0.0105f, 0.0145f);
    }

    [Fact]
    public void ApplyRxAudioLeveler_UsesNr5NativeLiftEvidenceForFasterWeakSpeechCatchup()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 3.5 };
        float[] block = new float[1024];
        for (int i = 0; i < block.Length; i++)
            block[i] = i % 16 == 0 ? DbToLinear(-31.4) : DbToLinear(-43.4);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.335,
            signalProbability: 0.148,
            agcGate: 0.236,
            recoveryDrive: 0.326,
            weakSignalMemory: 0.285,
            outputDbfs: -46.7,
            inputDbfs: -62.1,
            maskSmoothing: 0.300,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.BoostSlewLimited);
        Assert.Equal(12.5, state.AppliedGainDb, precision: 6);
        Assert.Equal(9.0, state.GainDeltaDb, precision: 6);
        Assert.True(state.DesiredGainDb > state.AppliedGainDb);
        Assert.InRange(state.InputRmsDbfs, -41.0, -40.0);
        Assert.InRange(state.InputPeakDbfs, -31.5, -31.3);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotUseNr5FastCatchupForLowProofFloor()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 3.5 };
        float[] block = new float[1024];
        for (int i = 0; i < block.Length; i++)
            block[i] = i % 16 == 0 ? DbToLinear(-31.4) : DbToLinear(-43.4);
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.260,
            signalProbability: 0.100,
            agcGate: 0.200,
            recoveryDrive: 0.240,
            weakSignalMemory: 0.200,
            outputDbfs: -46.7,
            inputDbfs: -62.1,
            maskSmoothing: 0.050,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.BoostSlewLimited);
        Assert.Equal(11.0, state.AppliedGainDb, precision: 6);
        Assert.Equal(7.5, state.GainDeltaDb, precision: 6);
        Assert.True(state.DesiredGainDb > state.AppliedGainDb);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_ExpiresMutedGapBoostMemory()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        for (int i = 0; i < 12; i++)
        {
            Array.Fill(block, 0.01f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        for (int i = 0; i < 28; i++)
        {
            Array.Fill(block, 0.00001f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        Assert.Equal(0.0, state.GainDb, precision: 6);
        Assert.Equal(0, state.PauseHoldBlocks);
        Assert.InRange(Rms(block), 0.000009f, 0.000011f);
        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
    }

    [Fact]
    public void ApplyRxAudioLeveler_BridgesNr5SpeechTailBelowGate()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 22.5,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 10
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-73.7));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.292,
            signalProbability: 0.176,
            agcGate: 0.55,
            recoveryDrive: 0.34,
            weakSignalMemory: 0.26,
            outputDbfs: -40.2,
            inputDbfs: -54.5,
            maskSmoothing: 0.29,
            peakEvidence: 0.02);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.AppliedGainDb > 0.0);
        Assert.True(state.DesiredGainDb > 0.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.InRange(state.OutputRmsDbfs, -58.0, -42.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotBridgeDeepNr5Floor()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 22.5,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 10
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-99.5));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.22,
            signalProbability: 0.09,
            agcGate: 0.36,
            recoveryDrive: 0.08,
            weakSignalMemory: 0.0,
            outputDbfs: -80.8,
            inputDbfs: -55.4,
            maskSmoothing: 0.04,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.Equal(0.0, state.AppliedGainDb, precision: 6);
        Assert.Equal(0.0, state.DesiredGainDb, precision: 6);
        Assert.InRange(state.OutputRmsDbfs, -100.5, -98.5);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_SmoothsNr5WeakSpeechRelease()
    {
        var state = new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 31.4,
            PauseHoldBlocks = 18,
            Nr5SpeechHoldBlocks = 10
        };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-56.8));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.266,
            signalProbability: 0.121,
            agcGate: 0.673,
            recoveryDrive: 0.447,
            weakSignalMemory: 0.479,
            outputDbfs: -35.7,
            inputDbfs: -49.9,
            maskSmoothing: 0.272,
            peakEvidence: 0.048);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.InRange(state.GainDeltaDb, -2.0, -1.0);
        Assert.InRange(state.AppliedGainDb, 29.9, 30.2);
        Assert.InRange(state.OutputRmsDbfs, -28.0, -25.0);
        Assert.False(state.PeakLimited);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_HoldsNr5SpeechIslandWhenPeakEvidenceDrops()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 31.2 };
        float[] block = new float[1024];
        Array.Fill(block, DbToLinear(-58.0));
        var nr5 = Nr5Diagnostics(
            signalConfidence: 0.295,
            signalProbability: 0.135,
            agcGate: 0.849,
            recoveryDrive: 0.400,
            weakSignalMemory: 0.403,
            outputDbfs: -36.6,
            inputDbfs: -49.1,
            maskSmoothing: 0.341,
            peakEvidence: 0.0);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state, nr5);

        Assert.True(state.DesiredGainDb > 29.0);
        Assert.True(state.AppliedGainDb > 28.0);
        Assert.True(state.Nr5SpeechHoldBlocks > 0);
        Assert.InRange(state.OutputRmsDbfs, -30.5, -21.0);
        Assert.False(state.OutputLimited);
    }

    [Fact]
    public void ApplyRxAudioLeveler_StrongSignalAfterWeakSignalDoesNotBlast()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        for (int i = 0; i < 18; i++)
        {
            Array.Fill(block, 0.01f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        Assert.True(state.GainDb > 20.0);

        Array.Fill(block, 0.9f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.InRange(Rms(block), 0.12f, 0.14f);
        Assert.InRange(state.GainDb, -18.0, -16.0);
        Assert.False(state.PeakLimited);
        Assert.True(state.PeakHeadroomDb < -1.0);
    }

    [Fact]
    public void ApplyRxAudioLeveler_ReportsPeakHeadroomConstraint()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, 0.01f);
        block[64] = 0.9f;

        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.True(state.DiagnosticsValid);
        Assert.True(state.PeakLimited);
        Assert.False(state.BoostSlewLimited);
        Assert.False(state.OutputLimited);
        Assert.Equal(0, state.OutputLimitSampleCount);
        Assert.Equal(0.0, state.OutputLimitReductionDb, precision: 6);
        Assert.InRange(state.AppliedGainDb, -1.8, -1.6);
        Assert.True(state.PeakHeadroomDb < -1.5);
        Assert.InRange(PeakAbs(block), 0.73f, 0.741f);
    }

    [Fact]
    public void ApplyRxAudioLeveler_PeakSafetyCutDoesNotSmoothIntoCrestCap()
    {
        var state = new DspPipelineService.RxAudioLevelerState { GainDb = 8.0 };
        float[] block = new float[1024];
        Array.Fill(block, 0.01f);
        block[128] = 0.47f;

        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.True(state.PeakLimited);
        Assert.False(state.OutputLimited);
        Assert.Equal(0, state.OutputLimitSampleCount);
        Assert.InRange(state.AppliedGainDb, 3.8, 4.1);
        Assert.InRange(PeakAbs(block), 0.73f, 0.741f);
    }

    [Fact]
    public void AdaptiveSquelch_LearnsFloorAndOpensAboveMargin()
    {
        var state = new DspPipelineService.AdaptiveSquelchState();
        var cfg = new SquelchConfig(Enabled: true, Level: 20, Adaptive: true);

        for (int i = 0; i < 3; i++)
            DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -100.0);

        Assert.False(state.Open);
        Assert.InRange(state.NoiseFloorDbm, -101.0, -99.0);

        DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -97.0);

        Assert.True(state.Open);
        Assert.Equal(2.5, DspPipelineService.AdaptiveSquelchMarginDb(), precision: 6);
    }

    [Fact]
    public void AdaptiveSquelch_AppliesMuteUntilGateOpens()
    {
        var state = new DspPipelineService.AdaptiveSquelchState();
        var cfg = new SquelchConfig(Enabled: true, Level: 20, Adaptive: true);
        float[] block = new float[256];

        for (int i = 0; i < 3; i++)
            DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -100.0);

        Array.Fill(block, 0.25f);
        DspPipelineService.ApplyAdaptiveSquelch(block, cfg, state);
        Assert.Equal(0f, Rms(block));
        Assert.Equal(0.0, state.Gain);

        DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -97.0);
        Array.Fill(block, 0.25f);
        DspPipelineService.ApplyAdaptiveSquelch(block, cfg, state);

        Assert.InRange(Rms(block), 0.16f, 0.19f);
        Assert.Equal(1.0, state.Gain, precision: 6);
    }

    [Fact]
    public void AdaptiveSquelch_HoldsTailAcrossShortSpeechPause()
    {
        var state = new DspPipelineService.AdaptiveSquelchState();
        var cfg = new SquelchConfig(Enabled: true, Level: 20, Adaptive: true);
        float[] block = new float[256];

        for (int i = 0; i < 3; i++)
            DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -100.0);

        DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -97.0);
        Array.Fill(block, 0.25f);
        DspPipelineService.ApplyAdaptiveSquelch(block, cfg, state);
        Assert.True(state.Open);
        Assert.Equal(1.0, state.Gain, precision: 6);

        for (int i = 0; i < 6; i++)
            DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -120.0);

        Array.Fill(block, 0.25f);
        DspPipelineService.ApplyAdaptiveSquelch(block, cfg, state);
        Assert.True(state.Open);
        Assert.True(Rms(block) > 0.20f);

        for (int i = 0; i < 10; i++)
            DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -120.0);

        Assert.False(state.Open);
    }

    [Fact]
    public void AdaptiveSquelch_FloorRisesSlowlyThroughStrongSignals()
    {
        var state = new DspPipelineService.AdaptiveSquelchState();
        var cfg = new SquelchConfig(Enabled: true, Level: 20, Adaptive: true);

        for (int i = 0; i < 6; i++)
            DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -100.0);
        double learnedFloor = state.NoiseFloorDbm;

        for (int i = 0; i < 6; i++)
            DspPipelineService.UpdateAdaptiveSquelchMeter(state, cfg, -70.0);

        Assert.True(state.NoiseFloorDbm <= learnedFloor + 3.0);
    }
}
