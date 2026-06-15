// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;

namespace Zeus.Dsp.Tests;

// WDSP's FFTW planner state is process-global and its wisdom cache asserts
// SLVNDX(slot) == slvndx when two threads enter create_fftplan at once (see
// fftw kernel/planner.c:261). xUnit parallelises test classes by default, so
// every WDSP-backed test class must join this shared collection to serialize
// against each other. Synthetic tests stay parallel.
[CollectionDefinition("Wdsp")]
public class WdspCollection { }

[Collection("Wdsp")]
public class WdspDspEngineTests
{
    [Fact]
    public void FixedSquelchMapping_UsesSensitiveOperatorRange()
    {
        Assert.Equal(0.0, WdspDspEngine.MapFixedSsqlThreshold(0), precision: 6);
        Assert.InRange(WdspDspEngine.MapFixedSsqlThreshold(10), 0.012, 0.016);
        Assert.InRange(WdspDspEngine.MapFixedSsqlThreshold(50), 0.12, 0.13);
        Assert.Equal(0.32, WdspDspEngine.MapFixedSsqlThreshold(100), precision: 6);

        Assert.Equal(-150.0, WdspDspEngine.MapFixedAmsqThresholdDb(0), precision: 6);
        Assert.InRange(WdspDspEngine.MapFixedAmsqThresholdDb(10), -147.0, -144.0);
        Assert.InRange(WdspDspEngine.MapFixedAmsqThresholdDb(50), -112.0, -109.0);
        Assert.Equal(-50.0, WdspDspEngine.MapFixedAmsqThresholdDb(100), precision: 6);

        Assert.Equal(1.0, WdspDspEngine.MapFixedFmsqThreshold(0), precision: 6);
        Assert.InRange(WdspDspEngine.MapFixedFmsqThreshold(10), 0.95, 0.98);
        Assert.InRange(WdspDspEngine.MapFixedFmsqThreshold(50), 0.68, 0.70);
        Assert.Equal(0.2, WdspDspEngine.MapFixedFmsqThreshold(100), precision: 6);
    }

    [Fact]
    public void FixedSquelchRun_LevelZeroIsOpenPassThrough()
    {
        Assert.False(WdspDspEngine.ShouldRunFixedSquelch(
            new SquelchConfig(Enabled: true, Level: 0, Adaptive: false)));
        Assert.False(WdspDspEngine.ShouldRunFixedSquelch(
            new SquelchConfig(Enabled: true, Level: -10, Adaptive: false)));
        Assert.True(WdspDspEngine.ShouldRunFixedSquelch(
            new SquelchConfig(Enabled: true, Level: 1, Adaptive: false)));
        Assert.False(WdspDspEngine.ShouldRunFixedSquelch(
            new SquelchConfig(Enabled: true, Level: 1, Adaptive: true)));
        Assert.False(WdspDspEngine.ShouldRunFixedSquelch(
            new SquelchConfig(Enabled: false, Level: 1, Adaptive: false)));
    }

    private static bool WdspAvailable()
    {
        try { return WdspNativeLoader.TryProbe(); }
        catch { return false; }
    }

    [SkippableFact]
    public void Phase1_AcceptanceSmoke()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available — builder has not dropped the .dylib yet");

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(sampleRateHz: 192_000, pixelWidth: 2048);

        engine.SetMode(channel, RxMode.USB);
        engine.SetFilter(channel, 150, 2850);
        engine.SetVfoHz(channel, 14_200_000);

        var iq = new double[2 * 1024];
        engine.FeedIq(channel, iq);

        Thread.Sleep(120);

        var pan = new float[2048];
        _ = engine.TryGetDisplayPixels(channel, DisplayPixout.Panadapter, pan);

        engine.CloseChannel(channel);
    }

    [SkippableFact]
    public void FeedIq_AcceptsVariableChunkSizes()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            var small = new double[64];
            var big = new double[2 * 2048];
            for (int i = 0; i < 20; i++) engine.FeedIq(channel, small);
            engine.FeedIq(channel, big);
            Thread.Sleep(50);
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    // Phase 1 validation: feed a known complex sine, prove the analyzer produces a
    // sharp peak in the expected pixel half. Exercises OpenChannel, fexchange0,
    // Spectrum0, SetAnalyzer (n_pixout=2), GetPixels — the whole P/Invoke chain
    // end-to-end. Signature or buffer errors would either kill the peak or land
    // it in the wrong place.
    //
    // WDSP axis convention (empirically verified, Phase 1):
    //   pixel 0       = highest positive frequency
    //   pixel Width/2 = DC (0 Hz)
    //   pixel Width-1 = most negative frequency
    //
    // Expectations were inverted when the analyzer feed started conjugating Q
    // to match the HL2's effective IQ convention (RunWorker in WdspDspEngine).
    // Synthetic +offset IQ fed to the engine now becomes a -offset tone inside
    // the analyzer, so the peak lands in the opposite half from what the pure
    // WDSP convention would predict. The test still validates the end-to-end
    // pipeline orientation — just for the real-radio-facing signal polarity.
    [SkippableTheory]
    [InlineData(+10_000.0, /*expectBelowCentre*/ false)]
    [InlineData(-10_000.0, /*expectBelowCentre*/ true)]
    public void ComplexTone_ProducesPeakAtExpectedSideOfCentre(double offsetHz, bool expectBelowCentre)
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const double Amplitude = 0.3;
        const int TotalComplex = 64 * 1024;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            var iq = new double[TotalComplex * 2];
            for (int n = 0; n < TotalComplex; n++)
            {
                double phase = 2.0 * Math.PI * offsetHz * n / SampleRate;
                iq[2 * n] = Amplitude * Math.Cos(phase);
                iq[2 * n + 1] = Amplitude * Math.Sin(phase);
            }
            engine.FeedIq(channel, iq);

            var pan = new float[Width];
            Assert.True(WaitForPixels(engine, channel, pan),
                "analyzer never produced a pixel frame");

            int peakIdx = 0;
            float peakDb = float.NegativeInfinity;
            for (int i = 0; i < pan.Length; i++)
            {
                if (pan[i] > peakDb) { peakDb = pan[i]; peakIdx = i; }
            }

            var sorted = (float[])pan.Clone();
            Array.Sort(sorted);
            float median = sorted[sorted.Length / 2];

            Assert.True(peakDb - median > 40.0f,
                $"peak not sharp enough: peakDb={peakDb:F1}, median={median:F1}");

            bool peakBelowCentre = peakIdx < Width / 2;
            Assert.Equal(expectBelowCentre, peakBelowCentre);

            // Tight bound: peak should land within 5 % of centre for a ±10 kHz
            // signal on a 192 kHz span (10/96 ≈ 10.4 % of half-span → ~213 pixels
            // from centre). Allow generous 400-pixel tolerance for FFT leakage /
            // pixel aggregation.
            int peakDistanceFromCentre = Math.Abs(peakIdx - Width / 2);
            Assert.InRange(peakDistanceFromCentre, 50, 400);
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void ZeroIq_ProducesFlatSpectrum()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int Width = 2048;
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, Width);
        try
        {
            engine.FeedIq(channel, new double[2 * 32_768]);

            var pan = new float[Width];
            Assert.True(WaitForPixels(engine, channel, pan),
                "analyzer never produced a pixel frame");

            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = 0; i < pan.Length; i++)
            {
                if (pan[i] < min) min = pan[i];
                if (pan[i] > max) max = pan[i];
            }
            Assert.True(max - min < 30.0f,
                $"expected flat spectrum for zero input, got dynamic range {max - min:F1} dB");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    private static bool WaitForPixels(WdspDspEngine engine, int channel, Span<float> pan)
    {
        for (int i = 0; i < 50; i++)
        {
            if (engine.TryGetDisplayPixels(channel, DisplayPixout.Panadapter, pan))
                return true;
            Thread.Sleep(20);
        }
        return false;
    }

    [SkippableFact]
    public void ReadAudio_DrainsSamplesAfterIqFed()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const double Amplitude = 0.3;
        const int TotalComplex = 32 * 1024;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            var iq = new double[TotalComplex * 2];
            for (int n = 0; n < TotalComplex; n++)
            {
                double phase = 2.0 * Math.PI * 1_500.0 * n / SampleRate;
                iq[2 * n] = Amplitude * Math.Cos(phase);
                iq[2 * n + 1] = Amplitude * Math.Sin(phase);
            }
            engine.FeedIq(channel, iq);

            var audio = new float[2048];
            int drained = 0;
            for (int i = 0; i < 50 && drained == 0; i++)
            {
                Thread.Sleep(20);
                drained = engine.ReadAudio(channel, audio);
            }

            Assert.True(drained > 0, "expected ReadAudio to drain samples after IQ fed");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [Fact]
    public void Factory_Auto_ReturnsSynthetic_UntilPhase3WiresIq()
    {
        using var engine = DspEngineFactory.Create(DspEngineKind.Auto);
        Assert.IsType<SyntheticDspEngine>(engine);
    }

    [SkippableFact]
    public void Factory_Wdsp_ReturnsWdspWhenAvailable()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = DspEngineFactory.Create(DspEngineKind.Wdsp);
        Assert.IsType<WdspDspEngine>(engine);
    }

    [Fact]
    public void Factory_Synthetic_ReturnsSynthetic()
    {
        using var engine = DspEngineFactory.Create(DspEngineKind.Synthetic);
        Assert.IsType<SyntheticDspEngine>(engine);
    }

    [SkippableFact]
    public void OpenTxChannel_ReturnsPositiveId_AfterOpenRxChannel()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(192_000, 1024);
        try
        {
            int tx = engine.OpenTxChannel();
            Assert.True(tx >= 0);
            Assert.NotEqual(rx, tx);
        }
        finally
        {
            engine.CloseChannel(rx);
        }
    }

    [SkippableFact]
    public void OpenTxChannel_IsIdempotent()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(192_000, 1024);
        try
        {
            int first = engine.OpenTxChannel();
            int second = engine.OpenTxChannel();
            Assert.Equal(first, second);
        }
        finally
        {
            engine.CloseChannel(rx);
        }
    }

    [SkippableFact]
    public void SetMox_DoesNotThrow_AfterOpenTxChannel()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.OpenTxChannel();
            engine.SetMox(true);
            engine.SetMox(false);
        }
        finally
        {
            engine.CloseChannel(rx);
        }
    }

    [SkippableFact]
    public void SetMox_IsNoOp_BeforeOpenTxChannel()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.SetMox(true);
            engine.SetMox(false);
        }
        finally
        {
            engine.CloseChannel(rx);
        }
    }

    // Regression guard for the #14 symptom: "after MOX cycle, RX audio no
    // longer plays." Runs a full MOX round-trip and confirms the RXA resumes
    // producing audio. Exercises SetChannelState(RXA,0,1) → (RXA,1,0) against
    // WDSP's flushflag/exchange-bit machinery (channel.c:259-297, iobuffs.c
    // :465-516) to ensure the damp-down and re-arm both propagate cleanly.
    [SkippableFact]
    public void ReadAudio_ResumesAfterMoxCycle()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const double Amplitude = 0.3;
        const int TotalComplex = 32 * 1024;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            engine.OpenTxChannel();
            engine.SetMode(channel, RxMode.USB);
            engine.SetFilter(channel, 150, 2850);

            var iq = new double[TotalComplex * 2];
            for (int n = 0; n < TotalComplex; n++)
            {
                double phase = 2.0 * Math.PI * 1_500.0 * n / SampleRate;
                iq[2 * n] = Amplitude * Math.Cos(phase);
                iq[2 * n + 1] = Amplitude * Math.Sin(phase);
            }

            // Pre-MOX: confirm the RX path is live before we disturb it.
            engine.FeedIq(channel, iq);
            var audio = new float[2048];
            int preDrained = 0;
            for (int i = 0; i < 50 && preDrained == 0; i++)
            {
                Thread.Sleep(20);
                preDrained = engine.ReadAudio(channel, audio);
            }
            Assert.True(preDrained > 0, "pre-MOX RXA never produced audio");

            // MOX cycle — mirror DspPipelineService.SetMox path exactly.
            engine.SetMox(true);
            Thread.Sleep(50);
            engine.SetMox(false);

            // Post-MOX: RXA must produce audio again. Re-feed IQ since the
            // pre-MOX chunk was already consumed, then drain.
            engine.FeedIq(channel, iq);
            int postDrained = 0;
            for (int i = 0; i < 100 && postDrained == 0; i++)
            {
                Thread.Sleep(20);
                postDrained = engine.ReadAudio(channel, audio);
            }
            Assert.True(postDrained > 0,
                "post-MOX RXA never resumed — SetMox(false) did not re-arm the exchange bit");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void ReadAudio_DrainsSamples_WhenTxChannelAlsoOpen()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const double Amplitude = 0.3;
        const int TotalComplex = 32 * 1024;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            engine.OpenTxChannel();
            var iq = new double[TotalComplex * 2];
            for (int n = 0; n < TotalComplex; n++)
            {
                double phase = 2.0 * Math.PI * 1_500.0 * n / SampleRate;
                iq[2 * n] = Amplitude * Math.Cos(phase);
                iq[2 * n + 1] = Amplitude * Math.Sin(phase);
            }
            engine.FeedIq(channel, iq);

            var audio = new float[2048];
            int drained = 0;
            for (int i = 0; i < 50 && drained == 0; i++)
            {
                Thread.Sleep(20);
                drained = engine.ReadAudio(channel, audio);
            }

            Assert.True(drained > 0, "expected ReadAudio to drain samples even with TxChannel open");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void GetRXAMeter_SAv_EscapesSentinel_AfterIqFlows_WithTxChannelAndProductionState()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const double Amplitude = 0.3;
        const int TotalComplex = 32 * 1024;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            engine.OpenTxChannel();
            engine.SetMode(channel, RxMode.USB);
            engine.SetFilter(channel, 150, 2850);
            engine.SetVfoHz(channel, 14_200_000);
            engine.SetAgcTop(channel, 80.0);
            engine.SetNoiseReduction(channel, new NrConfig());
            engine.SetZoom(channel, 1);

            var iq = new double[TotalComplex * 2];
            for (int n = 0; n < TotalComplex; n++)
            {
                double phase = 2.0 * Math.PI * 1_500.0 * n / SampleRate;
                iq[2 * n] = Amplitude * Math.Cos(phase);
                iq[2 * n + 1] = Amplitude * Math.Sin(phase);
            }

            // Feed in 126-complex-sample chunks — matches Protocol1 packet size.
            const int ChunkComplex = 126;
            for (int off = 0; off < TotalComplex; off += ChunkComplex)
            {
                int take = Math.Min(ChunkComplex, TotalComplex - off);
                engine.FeedIq(channel, iq.AsSpan(2 * off, 2 * take));
            }

            // Drain audio; allow time for the worker and wdspmain thread to run.
            var audio = new float[2048];
            int totalDrained = 0;
            for (int i = 0; i < 50; i++)
            {
                Thread.Sleep(20);
                int drained = engine.ReadAudio(channel, audio);
                totalDrained += drained;
                if (totalDrained >= 1024) break;
            }

            double sAv = NativeMethods.GetRXAMeter(channel, 1);
            double adcAv = NativeMethods.GetRXAMeter(channel, 3);
            Assert.True(totalDrained > 0, $"expected audio to drain; S_AV={sAv:F1} ADC_AV={adcAv:F1}");
            Assert.True(sAv > -399.0, $"RXA_S_AV still at -400 sentinel; ADC_AV={adcAv:F1}");

            // Meters Panel PR 1: assert that the full RxStageMeters snapshot
            // also escapes the −400 sentinel once IQ has flowed. Each of the
            // 7 indices comes from a different point in the WDSP RXA chain
            // (smeter / adcmeter / wcpAGC), so a sentinel on any one of them
            // would mean that stage hasn't ticked. AgcGain is signed and can
            // legitimately read 0 when AGC is disengaged, so we use a wider
            // bound for it (anything above −300 means it ticked).
            var rx = engine.GetRxStageMeters(channel);
            Assert.True(rx.SignalPk > -399.0, $"RxStageMeters.SignalPk at sentinel ({rx.SignalPk:F1})");
            Assert.True(rx.SignalAv > -399.0, $"RxStageMeters.SignalAv at sentinel ({rx.SignalAv:F1})");
            Assert.True(rx.AdcPk > -399.0, $"RxStageMeters.AdcPk at sentinel ({rx.AdcPk:F1})");
            Assert.True(rx.AdcAv > -399.0, $"RxStageMeters.AdcAv at sentinel ({rx.AdcAv:F1})");
            // AgcGain is signed dB (can be 0 when AGC is off, +30 boosting,
            // −12 cutting). Sentinel for this index would be ~ −400 like the
            // others; assert it's above −300 to stay conservative.
            Assert.True(rx.AgcGain > -300.0, $"RxStageMeters.AgcGain at sentinel ({rx.AgcGain:F1})");
            Assert.True(rx.AgcEnvPk > -399.0, $"RxStageMeters.AgcEnvPk at sentinel ({rx.AgcEnvPk:F1})");
            Assert.True(rx.AgcEnvAv > -399.0, $"RxStageMeters.AgcEnvAv at sentinel ({rx.AgcEnvAv:F1})");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void ReadAudio_DrainsSamples_WhenApplyingProductionStateSequence()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const double Amplitude = 0.3;
        const int TotalComplex = 32 * 1024;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            // Mirror DspPipelineService.OnRadioConnected exactly:
            //   OpenChannel -> OpenTxChannel -> ApplyStateToNewChannel
            engine.OpenTxChannel();
            engine.SetMode(channel, RxMode.USB);
            engine.SetFilter(channel, 150, 2850);
            engine.SetVfoHz(channel, 14_200_000);
            engine.SetAgcTop(channel, 80.0);
            engine.SetNoiseReduction(channel, new NrConfig());
            engine.SetZoom(channel, 1);

            var iq = new double[TotalComplex * 2];
            for (int n = 0; n < TotalComplex; n++)
            {
                double phase = 2.0 * Math.PI * 1_500.0 * n / SampleRate;
                iq[2 * n] = Amplitude * Math.Cos(phase);
                iq[2 * n + 1] = Amplitude * Math.Sin(phase);
            }
            engine.FeedIq(channel, iq);

            var audio = new float[2048];
            int drained = 0;
            for (int i = 0; i < 50 && drained == 0; i++)
            {
                Thread.Sleep(20);
                drained = engine.ReadAudio(channel, audio);
            }

            Assert.True(drained > 0, "expected ReadAudio to drain samples after production state sequence applied");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void ProcessTxBlock_USB_SilentInput_ProducesSilentIq()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(48_000, 1024);
        try
        {
            int tx = engine.OpenTxChannel();
            Assert.True(tx >= 0, "expected valid TXA id");
            engine.SetTxMode(RxMode.USB);
            engine.SetMox(true);

            // Feed several blocks of silence — TXA internal filters need to
            // settle before the output steady-state is reached. Assert the
            // *last* block is silent, not the first.
            int block = engine.TxBlockSamples;
            var mic = new float[block];
            var iq = new float[2 * block];
            int produced = 0;
            for (int k = 0; k < 16; k++)
                produced = engine.ProcessTxBlock(mic, iq);

            Assert.Equal(block, produced);

            double peak = 0.0;
            for (int i = 0; i < iq.Length; i++)
            {
                double m = Math.Abs(iq[i]);
                if (m > peak) peak = m;
            }
            // USB suppresses the carrier; silent mic → output ≈ 0. Allow a
            // tiny leak margin for residual DSP state.
            Assert.True(peak < 1e-3, $"expected silent IQ, got peak {peak}");
        }
        finally
        {
            engine.SetMox(false);
            engine.CloseChannel(rx);
        }
    }

    [SkippableFact]
    public void ProcessTxBlock_FM_SilentInput_ProducesNonZeroCarrier()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(48_000, 1024);
        try
        {
            engine.OpenTxChannel();
            engine.SetTxMode(RxMode.FM);
            engine.SetMox(true);

            int block = engine.TxBlockSamples;
            var mic = new float[block];
            var iq = new float[2 * block];
            int produced = 0;
            for (int k = 0; k < 16; k++)
                produced = engine.ProcessTxBlock(mic, iq);

            Assert.Equal(block, produced);

            double energy = 0.0;
            for (int i = 0; i < iq.Length; i++) energy += iq[i] * iq[i];
            // FM doesn't suppress the carrier — silent mic still produces a
            // non-zero IQ stream at the modulator's rest frequency.
            Assert.True(energy > 0.01, $"expected non-zero FM carrier, got energy {energy}");
        }
        finally
        {
            engine.SetMox(false);
            engine.CloseChannel(rx);
        }
    }

    [SkippableFact]
    public void ProcessTxBlock_InvalidLength_Throws()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(48_000, 1024);
        try
        {
            engine.OpenTxChannel();
            int block = engine.TxBlockSamples;
            var mic = new float[block - 1];
            var iq = new float[2 * block];
            Assert.Throws<ArgumentException>(() => engine.ProcessTxBlock(mic, iq));
        }
        finally
        {
            engine.CloseChannel(rx);
        }
    }

    [SkippableFact]
    public void ProcessTxBlock_SetTxPanelGain_ScalesOutputByLinearGain()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        using var engine = new WdspDspEngine();
        int rx = engine.OpenChannel(48_000, 1024);
        try
        {
            engine.OpenTxChannel();
            engine.SetTxMode(RxMode.USB);
            engine.SetMox(true);

            int block = engine.TxBlockSamples;
            var mic = new float[block];
            var iq = new float[2 * block];

            // Build a synthetic mic block: 1 kHz tone at amplitude 0.01.
            //
            // Input amplitude is kept ≤ 0.05 so both the 1x and 10x cases
            // stay below the Leveler's compression knee (WDSP defaults:
            // out_targ = 1.05, max_gain = 1.778 — see TXA.c:169,173).
            // Testing panel-gain linearity above that point would be
            // testing the Leveler, not the gain. The Leveler is on by
            // default to match Thetis (radio.cs:3018 tx_leveler_on = true),
            // so this amplitude choice is tied to that default — if the
            // Leveler default ever flips off, the amplitude can be raised
            // back to 0.1 without loss of meaning.
            const double Amplitude = 0.01;
            const double ToneHz = 1000.0;
            const int SampleRate = 48_000;
            for (int n = 0; n < block; n++)
            {
                double phase = 2.0 * Math.PI * ToneHz * n / SampleRate;
                mic[n] = (float)(Amplitude * Math.Cos(phase));
            }

            // Feed several blocks through at gain=1.0 to settle filters
            engine.SetTxPanelGain(1.0);
            for (int k = 0; k < 16; k++)
                engine.ProcessTxBlock(mic, iq);

            // Measure RMS of IQ output at gain=1.0
            double rms1 = ComputeRms(iq);

            // Now set gain to 10.0 (20 dB) and process again. With
            // Amplitude = 0.01, the 10x intermediate signal peaks near 0.1,
            // well under the Leveler's out_targ of 1.05, so the Leveler
            // stays in unity-gain pass-through and the ratio reflects
            // panel-gain alone.
            engine.SetTxPanelGain(10.0);
            for (int k = 0; k < 16; k++)
                engine.ProcessTxBlock(mic, iq);

            // Measure RMS of IQ output at gain=10.0
            double rms10 = ComputeRms(iq);

            // The ratio of RMS outputs should be approximately 10.0
            // (allow 10% tolerance for DSP filter effects)
            double ratio = rms10 / rms1;
            Assert.InRange(ratio, 9.0, 11.0);
        }
        finally
        {
            engine.SetMox(false);
            engine.CloseChannel(rx);
        }
    }

    private static double ComputeRms(ReadOnlySpan<float> samples)
    {
        double sumSquares = 0.0;
        for (int i = 0; i < samples.Length; i++)
            sumSquares += samples[i] * samples[i];
        return Math.Sqrt(sumSquares / samples.Length);
    }
}
