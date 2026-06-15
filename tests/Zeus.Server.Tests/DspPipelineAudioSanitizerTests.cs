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
        double peakEvidence = 0.0) =>
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
            LevelDrive: 0.8,
            RecoveryDrive: recoveryDrive,
            WeakSignalMemory: weakSignalMemory,
            MakeupGain: 1.0,
            MakeupGainDb: 0.0,
            InputRms: 0.01,
            InputDbfs: inputDbfs,
            OutputRms: Math.Pow(10.0, outputDbfs / 20.0),
            OutputDbfs: outputDbfs,
            OutputPeak: 0.02,
            OutputPeakDbfs: -34.0,
            PeakEvidence: peakEvidence,
            PeakLimit: 0.6,
            PeakLimitDbfs: -4.5,
            PeakReductionDb: 0.0,
            AdjacentNoiseUsable: true,
            AdjacentNoiseBins: 72,
            AdjacentNoiseFloorDb: -105.0,
            AdjacentNoiseTrust: 0.62,
            AdjacentNoiseDrive: 0.18,
            AdjacentNoiseRejectedPct: 8.0,
            AdjacentNoiseLeftBins: 34,
            AdjacentNoiseRightBins: 38,
            AdjacentNoiseLeftFloorDb: -105.2,
            AdjacentNoiseRightFloorDb: -104.9,
            AdjacentNoiseSideBalance: 0.895,
            AdjacentNoiseAsymmetryDb: 0.3);

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

        Assert.InRange(state.DesiredGainDb, 33.5, 34.5);
        Assert.Equal(state.DesiredGainDb, state.AppliedGainDb, precision: 6);
        Assert.True(state.GainDeltaDb > 0.0);
        Assert.InRange(state.OutputRmsDbfs, -29.5, -26.5);
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

        Assert.InRange(state.DesiredGainDb, 27.0, 28.5);
        Assert.True(state.AppliedGainDb >= state.DesiredGainDb);
        Assert.InRange(state.AppliedGainDb, 28.4, 28.6);
        Assert.True(state.GainDeltaDb < 0.0);
        Assert.InRange(state.OutputRmsDbfs, -22.0, -19.3);
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
