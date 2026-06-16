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
        Assert.Null(diag.RmsDbfs);
        Assert.Contains("adaptive squelch", diag.DiagnosticRecommendation);
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

        Assert.Equal(5.5, state.GainDb, precision: 6);
        Assert.True(state.BoostSlewLimited);
        Assert.Equal(5.5, state.GainDeltaDb, precision: 6);
        Assert.InRange(block[0], 0.0099f, 0.0102f);
        Assert.InRange(block[255], 0.0188f, 0.0189f);
        Assert.InRange(block[^1], 0.0188f, 0.0189f);

        Array.Fill(block, 0.01f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.Equal(11.0, state.GainDb, precision: 6);
        Assert.True(state.BoostSlewLimited);
        Assert.InRange(block[0], 0.0188f, 0.0190f);
        Assert.InRange(block[255], 0.0354f, 0.0356f);
        Assert.InRange(block[^1], 0.0354f, 0.0356f);
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
