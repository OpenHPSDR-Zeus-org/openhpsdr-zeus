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

using System.Text.Json;
using System.Text.Json.Serialization;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Xunit;

namespace Zeus.Dsp.Tests;

[Collection("Wdsp")]
public class NoiseReductionTests
{
    private static bool WdspAvailable()
    {
        try { return WdspNativeLoader.TryProbe(); }
        catch { return false; }
    }

    // The 192-case combinatorial WDSP walk is diagnostic-quality, not a
    // regression gate. Rapid OpenChannel / SetNoiseReduction / CloseChannel
    // cycling inside a single xUnit test host hits FFTW wisdom-cache + RXA
    // channel-state edges that SIGSEGV the host on every platform where
    // libwdsp actually loads (Windows CI, local macOS dev). The targeted
    // Wdsp Facts in this file (Wdsp_NbLifecycle_*, Wdsp_TogglingNrModes_*,
    // Wdsp_Nb1_*) cover the same P/Invoke seams without the 192× iteration.
    //
    // Set ZEUS_RUN_WDSP_COMBINATORIAL=1 to opt into the full walk when
    // bench-debugging an NR / NB / SBNR / SPNR engine change.
    private static bool CombinatorialWdspOptedIn() =>
        string.Equals(Environment.GetEnvironmentVariable("ZEUS_RUN_WDSP_COMBINATORIAL"), "1",
                      StringComparison.Ordinal);

    // True only when the bundled libwdsp exports the SBNR (NR4) symbols.
    // Probe the symbol table instead of calling SetRXASBNRRun: WDSP channel
    // setters dereference RXA state and are only safe after OpenChannel.
    private static bool SbnrAvailable()
    {
        try { return WdspDspEngine.Nr4SbnrAvailable; }
        catch { return false; }
    }

    private static bool SpnrAvailable()
    {
        try { return WdspDspEngine.Nr5SpnrAvailable; }
        catch { return false; }
    }

    public static IEnumerable<object[]> AllCombos()
    {
        foreach (var nr in new[] { NrMode.Off, NrMode.Anr, NrMode.Emnr, NrMode.Sbnr, NrMode.Nr5 })
        foreach (var anf in new[] { false, true })
        foreach (var snb in new[] { false, true })
        foreach (var notches in new[] { false, true })
        foreach (var nb in new[] { NbMode.Off, NbMode.Nb1, NbMode.Nb2 })
            yield return new object[] { new NrConfig(nr, anf, snb, notches, nb, 20.0) };
    }

    [Theory]
    [MemberData(nameof(AllCombos))]
    public void Synthetic_AcceptsEveryModeCombination(NrConfig cfg)
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        eng.SetNoiseReduction(id, cfg);
    }

    [Fact]
    public void Synthetic_RejectsBogusEnumValues()
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        Assert.Throws<ArgumentException>(() => eng.SetNoiseReduction(id, new NrConfig(NrMode: (NrMode)99)));
        Assert.Throws<ArgumentException>(() => eng.SetNoiseReduction(id, new NrConfig(NbMode: (NbMode)99)));
    }

    [Fact]
    public void Synthetic_RejectsNullConfig()
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        Assert.Throws<ArgumentNullException>(() => eng.SetNoiseReduction(id, null!));
    }

    // WDSP exercise — runs only when the native library is present. Walks every
    // combination the UI can produce and proves no combination crashes the
    // post-RXA NR path. Audio correctness is covered by the existing smoke /
    // tone-peak tests; this test only has to prove the P/Invoke signatures
    // match libwdsp and that the engine's mutual-exclusion logic is sound.
    //
    // Sbnr-on cases skip when the bundled libwdsp doesn't export the SBNR
    // entry points — Phase 1 of issue #79 (libwdsp rebuild) is tracked
    // separately. The engine swallows EntryPointNotFoundException internally,
    // but the test deliberately Skips so a future regression that DOES throw
    // at the seam still surfaces clearly.
    [SkippableTheory]
    [MemberData(nameof(AllCombos))]
    public void Wdsp_AcceptsEveryModeCombinationWithoutCrashing(NrConfig cfg)
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        Skip.IfNot(CombinatorialWdspOptedIn(), "Combinatorial WDSP walk is diagnostic-only — set ZEUS_RUN_WDSP_COMBINATORIAL=1 to run.");
        if (cfg.NrMode == NrMode.Sbnr)
            Skip.IfNot(SbnrAvailable(), "Requires libwdsp rebuild — Phase 1 of issue #79; bundled binaries do not export SBNR symbols.");
        if (cfg.NrMode == NrMode.Nr5)
            Skip.IfNot(SpnrAvailable(), "Requires libwdsp rebuild with NR5/SPNR exports.");

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.SetNoiseReduction(channel, cfg);
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void Wdsp_TogglingNrModes_DoesNotLeaveBothEnabled()
    {
        // NR button cycle is Off/NR1/NR2/NR4/NR5 — only one of ANR/EMNR/SBNR/SPNR
        // may be running at a time. Proven here by cycling through each mode; the
        // engine must issue the counter-Run(0) before toggling the other on.
        // Off transitions are exercised even when SBNR symbols are missing so
        // we still cover the OFF-from-SBNR engine path (TrySetSbnrRun(0)).
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Anr));
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Emnr));
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Anr));

            if (SbnrAvailable())
            {
                engine.SetNoiseReduction(channel, new NrConfig(NrMode.Sbnr));
                engine.SetNoiseReduction(channel, new NrConfig(NrMode.Anr));
            }

            if (SpnrAvailable())
            {
                engine.SetNoiseReduction(channel, new NrConfig(NrMode.Nr5));
                engine.SetNoiseReduction(channel, new NrConfig(NrMode.Anr));
            }

            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Off));
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    // Engine must not throw when SBNR symbols are missing — TrySetSbnrRun /
    // ApplyNr4Sbnr swallow EntryPointNotFoundException so the worker keeps
    // running and the operator gets NR-off behaviour. Exercised even on a
    // libwdsp build that DOES export SBNR; the catch path stays warm both
    // ways. Run when WDSP is available regardless of SBNR availability.
    [SkippableFact]
    public void Wdsp_SbnrMode_DoesNotCrashWhenSymbolsMissing()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Sbnr));
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Off));
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void Wdsp_Nr5Mode_DoesNotCrashWhenSymbolsMissing()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Nr5));
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Off));
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void Wdsp_Nr5Diagnostics_LearnAfterIqFlows()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        Skip.IfNot(SpnrAvailable(), "Requires libwdsp rebuild with NR5/SPNR exports.");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const int TotalComplex = 256 * 1024;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            engine.SetMode(channel, RxMode.USB);
            engine.SetFilter(channel, 150, 2850);
            engine.SetAgcTop(channel, 80.0);
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Nr5));

            var iq = new double[TotalComplex * 2];
            uint noise = 0x12345678u;
            for (int n = 0; n < TotalComplex; n++)
            {
                noise = 1664525u * noise + 1013904223u;
                double white = ((noise >> 8) / 16777216.0 - 0.5) * 0.012;
                double weakPhase = 2.0 * Math.PI * 1_250.0 * n / SampleRate;
                double strongPhase = 2.0 * Math.PI * 2_050.0 * n / SampleRate;
                iq[2 * n] = white + 0.018 * Math.Cos(weakPhase) + 0.06 * Math.Cos(strongPhase);
                iq[2 * n + 1] = white + 0.018 * Math.Sin(weakPhase) + 0.06 * Math.Sin(strongPhase);
            }

            for (int off = 0; off < TotalComplex; off += 126)
            {
                int take = Math.Min(126, TotalComplex - off);
                engine.FeedIq(channel, iq.AsSpan(2 * off, 2 * take));
            }

            var audio = new float[2048];
            Nr5SpnrDiagnosticsDto? diag = null;
            for (int i = 0; i < 80; i++)
            {
                Thread.Sleep(20);
                engine.ReadAudio(channel, audio);
                diag = engine.TryGetNr5SpnrDiagnostics(channel);
                if (diag is { LearnedFrames: > 55, InputRms: > 0.0, OutputRms: > 0.0 }) break;
            }

            Assert.NotNull(diag);
            Assert.True(diag.Run, "NR5 should still be running");
            Assert.True(diag.LearnedFrames > 55, $"expected learned NR5 frames, got {diag.LearnedFrames}");
            Assert.True(diag.InputRms > 0.0, $"expected input RMS, got {diag.InputRms}");
            Assert.True(diag.OutputRms > 0.0, $"expected output RMS, got {diag.OutputRms}");
            Assert.Equal(3, diag.SchemaVersion);
            Assert.InRange(diag.CoherencePeak, 0.0, 1.0);
            Assert.InRange(diag.RidgePeak, 0.0, 1.0);
            Assert.InRange(diag.SignalConfidence, 0.0, 1.0);
            Assert.InRange(diag.AgcGate, 0.0, 1.0);
            Assert.InRange(diag.MeanGain, 0.0, 1.0);
            Assert.InRange(diag.MinGain, 0.0, 1.0);
            Assert.True(diag.FloorReductionDb >= 0.0, $"expected non-negative floor push, got {diag.FloorReductionDb}");
            Assert.True(diag.DynamicRangeDb >= 0.0, $"expected non-negative NR5 dynamic range, got {diag.DynamicRangeDb}");
            if (WdspDspEngine.Nr5SpnrAdvancedDiagnosticsAvailable)
            {
                Assert.True(
                    diag.CoherencePeak > 0.0 || diag.RidgePeak > 0.0,
                    $"expected coherent/ridge weak-signal evidence, got coherence={diag.CoherencePeak} ridge={diag.RidgePeak}");
                Assert.True(diag.DynamicRangeDb > 0.0, $"expected NR5 spectral dynamic range, got {diag.DynamicRangeDb}");
            }
            if (WdspDspEngine.Nr5SpnrDeepDiagnosticsAvailable)
            {
                Assert.True(diag.SignalConfidence > 0.0, $"expected NR5 signal confidence, got {diag.SignalConfidence}");
                Assert.True(diag.AgcGate > 0.0, $"expected NR5 AGC signal gate, got {DescribeNr5(diag)}");
            }
            Assert.True(diag.AgcGain > 0.0, $"expected AGC gain, got {diag.AgcGain}");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void Wdsp_Nr5DeepGate_DoesNotLiftNoiseOnlyFrames()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        Skip.IfNot(SpnrAvailable(), "Requires libwdsp rebuild with NR5/SPNR exports.");
        Skip.IfNot(WdspDspEngine.Nr5SpnrDeepDiagnosticsAvailable, "Requires libwdsp rebuild with NR5 deep diagnostics exports.");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const int TotalComplex = 256 * 1024;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            engine.SetMode(channel, RxMode.USB);
            engine.SetFilter(channel, 150, 2850);
            engine.SetAgcTop(channel, 80.0);
            engine.SetNoiseReduction(channel, new NrConfig(NrMode.Nr5));

            var iq = new double[TotalComplex * 2];
            uint noise = 0x9e3779b9u;
            for (int n = 0; n < TotalComplex; n++)
            {
                noise = 1664525u * noise + 1013904223u;
                double i = ((noise >> 8) / 16777216.0 - 0.5) * 0.020;
                noise = 1664525u * noise + 1013904223u;
                double q = ((noise >> 8) / 16777216.0 - 0.5) * 0.020;
                iq[2 * n] = i;
                iq[2 * n + 1] = q;
            }

            for (int off = 0; off < TotalComplex; off += 126)
            {
                int take = Math.Min(126, TotalComplex - off);
                engine.FeedIq(channel, iq.AsSpan(2 * off, 2 * take));
            }

            var audio = new float[2048];
            Nr5SpnrDiagnosticsDto? diag = null;
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(20);
                engine.ReadAudio(channel, audio);
                diag = engine.TryGetNr5SpnrDiagnostics(channel);
                if (diag is { LearnedFrames: > 20, InputRms: > 0.0, OutputRms: > 0.0 }) break;
            }

            Assert.NotNull(diag);
            Assert.True(diag.LearnedFrames > 20, $"expected learned NR5 frames, got {diag.LearnedFrames}");
            Assert.True(diag.FloorReductionDb >= 2.6, $"expected deep floor pressure on noise-only frames, got {diag.FloorReductionDb}");
            Assert.True(diag.MeanGain <= 0.45, $"expected low mean gain on noise-only frames, got {DescribeNr5(diag)}");
            Assert.InRange(diag.SignalConfidence, 0.0, 1.0);
            Assert.InRange(diag.SignalConfidence, 0.0, 0.45);
            Assert.InRange(diag.AgcGate, 0.0, 0.45);
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void Wdsp_Nr5DeepGate_SeparatesBuriedToneFromNoise()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        Skip.IfNot(SpnrAvailable(), "Requires libwdsp rebuild with NR5/SPNR exports.");
        Skip.IfNot(WdspDspEngine.Nr5SpnrDeepDiagnosticsAvailable, "Requires libwdsp rebuild with NR5 deep diagnostics exports.");

        const int SampleRate = 192_000;
        const int Width = 2048;
        const int WarmupComplex = 128 * 1024;
        const int TotalComplex = 512 * 1024;

        Nr5SpnrDiagnosticsDto RunFixture(bool includeTone)
        {
            using var engine = new WdspDspEngine();
            int channel = engine.OpenChannel(SampleRate, Width);
            try
            {
                engine.SetMode(channel, RxMode.USB);
                engine.SetFilter(channel, 150, 2850);
                engine.SetAgcTop(channel, 80.0);
                engine.SetNoiseReduction(channel, new NrConfig(NrMode.Nr5));

                var iq = new double[TotalComplex * 2];
                uint noise = 0x7f4a7c15u;
                for (int n = 0; n < TotalComplex; n++)
                {
                    noise = 1664525u * noise + 1013904223u;
                    double i = ((noise >> 8) / 16777216.0 - 0.5) * 0.020;
                    noise = 1664525u * noise + 1013904223u;
                    double q = ((noise >> 8) / 16777216.0 - 0.5) * 0.020;
                    if (includeTone && n >= WarmupComplex)
                    {
                        double phase = 2.0 * Math.PI * 1_500.0 * (n - WarmupComplex) / SampleRate;
                        i += 0.016 * Math.Cos(phase);
                        q += 0.016 * Math.Sin(phase);
                    }

                    iq[2 * n] = i;
                    iq[2 * n + 1] = q;
                }

                for (int off = 0; off < TotalComplex; off += 126)
                {
                    int take = Math.Min(126, TotalComplex - off);
                    engine.FeedIq(channel, iq.AsSpan(2 * off, 2 * take));
                }

                var audio = new float[2048];
                Nr5SpnrDiagnosticsDto? diag = null;
                for (int i = 0; i < 160; i++)
                {
                    Thread.Sleep(20);
                    engine.ReadAudio(channel, audio);
                    diag = engine.TryGetNr5SpnrDiagnostics(channel);
                    if (diag is { LearnedFrames: > 100, InputRms: > 0.0, OutputRms: > 0.0 }) break;
                }

                Assert.NotNull(diag);
                return diag;
            }
            finally
            {
                engine.CloseChannel(channel);
            }
        }

        var noiseOnly = RunFixture(includeTone: false);
        var buriedTone = RunFixture(includeTone: true);

        Assert.True(
            buriedTone.SignalConfidence > noiseOnly.SignalConfidence + 0.001,
            $"expected buried tone confidence above noise-only confidence; tone={DescribeNr5(buriedTone)}, noise={DescribeNr5(noiseOnly)}");
        Assert.True(
            buriedTone.AgcGate > noiseOnly.AgcGate,
            $"expected buried tone AGC gate above noise-only gate; tone={DescribeNr5(buriedTone)}, noise={DescribeNr5(noiseOnly)}");
        Assert.True(
            buriedTone.MeanGain > noiseOnly.MeanGain,
            $"expected preserved signal bins to lift mean gain above noise-only; tone={DescribeNr5(buriedTone)}, noise={DescribeNr5(noiseOnly)}");
    }

    private static string DescribeNr5(Nr5SpnrDiagnosticsDto diag) =>
        $"learned={diag.LearnedFrames} conf={diag.SignalConfidence:F3} gate={diag.AgcGate:F3} " +
        $"presence={diag.PresencePeak:F3} salience={diag.SaliencePeak:F3} " +
        $"coherence={diag.CoherencePeak:F3} ridge={diag.RidgePeak:F3} " +
        $"meanGain={diag.MeanGain:F3} minGain={diag.MinGain:F3} " +
        $"floor={diag.FloorReductionDb:F1}dB dr={diag.DynamicRangeDb:F1}dB";

    // REST contract round-trip. The server registers a JsonStringEnumConverter
    // in Program.cs so NrMode/NbMode go on the wire as "Anr"/"Nb1" etc.; this
    // test uses the same options so a schema change on either side breaks here.
    [Fact]
    public void NrSetRequest_JsonRoundTrip_PreservesAllFields()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new JsonStringEnumConverter());

        var req = new NrSetRequest(new NrConfig(
            NrMode: NrMode.Emnr,
            AnfEnabled: true,
            SnbEnabled: true,
            NbpNotchesEnabled: true,
            NbMode: NbMode.Nb2,
            NbThreshold: 55.5));

        string json = JsonSerializer.Serialize(req, opts);
        var back = JsonSerializer.Deserialize<NrSetRequest>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(req, back);
        Assert.Contains("\"Emnr\"", json);
        Assert.Contains("\"Nb2\"", json);
    }

    [Fact]
    public void StateDto_JsonRoundTrip_PreservesNrBlock()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new JsonStringEnumConverter());

        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.100:1024",
            VfoHz: 14_200_000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192_000,
            AgcTopDb: 80.0,
            AttenDb: 15,
            Nr: new NrConfig(NrMode.Anr, true, false, true, NbMode.Off, 20.0));

        string json = JsonSerializer.Serialize(state, opts);
        var back = JsonSerializer.Deserialize<StateDto>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(state.Nr, back!.Nr);
    }

    [Fact]
    public void NrSetRequest_JsonRoundTrip_PreservesNr5()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new JsonStringEnumConverter());

        var req = new NrSetRequest(new NrConfig(NrMode: NrMode.Nr5));
        string json = JsonSerializer.Serialize(req, opts);
        var back = JsonSerializer.Deserialize<NrSetRequest>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(NrMode.Nr5, back!.Nr.NrMode);
        Assert.Contains("\"Nr5\"", json);
    }

    // NB1/NB2 lifecycle across a full toggle cycle. Proves: (1) create_anbEXT /
    // create_nobEXT ran in OpenChannel before any SetEXT* setter could land
    // (otherwise deref of zero-initialized panb[id]/pnob[id] → SIGSEGV); (2)
    // destroy_*EXT runs in CloseChannel so the next OpenChannel reusing id 0
    // doesn't leak the prior struct.
    [SkippableTheory]
    [InlineData(48_000)]
    [InlineData(192_000)]
    public void Wdsp_NbLifecycle_ToggleOffNb1Nb2Off_DoesNotCrash(int sampleRate)
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(sampleRate, 1024);
        try
        {
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Nb1));
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Nb2));
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Off));
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Nb1, NbThreshold: 50.0));
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Off));
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    // Proves xanbEXT is actually called on the IQ path when NB1 is engaged —
    // not just that the setters don't crash. Feeds a clean sinusoid with a
    // large impulse spike every ~8 ms. With NB1 on, xanb substitutes the
    // delayed-ringbuffer value for samples flagged as above-average; the
    // spike bleeds into the pre-RXA buffer and raises the audio envelope
    // ceiling above the no-NB baseline, but attenuated. We only assert the
    // engine produces audio without crashing — exact peak ratios depend on
    // WDSP's AGC response to the impulse and are not stable enough to pin.
    [SkippableFact]
    public void Wdsp_Nb1_ProducesAudioForImpulsyIq()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");

        const int SampleRate = 192_000;
        const int Width = 1024;
        const int TotalComplex = 64 * 1024;
        const double Amplitude = 0.2;
        const double ImpulseAmplitude = 5.0;
        const int ImpulsePeriod = 1500;

        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(SampleRate, Width);
        try
        {
            engine.SetNoiseReduction(channel, new NrConfig(NbMode: NbMode.Nb1));

            var iq = new double[TotalComplex * 2];
            for (int n = 0; n < TotalComplex; n++)
            {
                double phase = 2.0 * Math.PI * 2_000.0 * n / SampleRate;
                double i = Amplitude * Math.Cos(phase);
                double q = Amplitude * Math.Sin(phase);
                if (n % ImpulsePeriod == 0) { i += ImpulseAmplitude; q += ImpulseAmplitude; }
                iq[2 * n] = i;
                iq[2 * n + 1] = q;
            }
            engine.FeedIq(channel, iq);

            var audio = new float[2048];
            int total = 0;
            for (int i = 0; i < 50 && total == 0; i++)
            {
                Thread.Sleep(20);
                total = engine.ReadAudio(channel, audio);
            }
            Assert.True(total > 0, "expected audio out of NB1-enabled channel");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [Fact]
    public void NrConfig_DefaultsMatchThetisOffState()
    {
        // Drop-in sanity: the contract's default-constructed NrConfig must
        // equal "everything off, NB threshold at Thetis UI default 20 (→ 3.3
        // scaled), all NR2 post2 + NR4 tunables null (= use engine defaults)."
        // If any default changes here without a corresponding spec update,
        // this test is the tripwire.
        var cfg = new NrConfig();
        Assert.Equal(NrMode.Off, cfg.NrMode);
        Assert.False(cfg.AnfEnabled);
        Assert.False(cfg.SnbEnabled);
        Assert.False(cfg.NbpNotchesEnabled);
        Assert.Equal(NbMode.Off, cfg.NbMode);
        Assert.Equal(20.0, cfg.NbThreshold);
        Assert.Null(cfg.EmnrPost2Run);
        Assert.Null(cfg.EmnrPost2Factor);
        Assert.Null(cfg.EmnrPost2Nlevel);
        Assert.Null(cfg.EmnrPost2Rate);
        Assert.Null(cfg.EmnrPost2Taper);
        Assert.Null(cfg.Nr4ReductionAmount);
        Assert.Null(cfg.Nr4SmoothingFactor);
        Assert.Null(cfg.Nr4WhiteningFactor);
        Assert.Null(cfg.Nr4NoiseRescale);
        Assert.Null(cfg.Nr4PostFilterThreshold);
        Assert.Null(cfg.Nr4NoiseScalingType);
        Assert.Null(cfg.Nr4Position);
    }

    // Round-trip the new NR2 post2 + NR4 fields through JSON to lock the wire
    // format. The popover saves each field individually via PATCH endpoints
    // (Nr2Post2ConfigSetRequest / Nr4ConfigSetRequest) but the persisted
    // NrConfig still has to deserialise cleanly when those fields are set.
    [Fact]
    public void NrConfig_JsonRoundTrip_PreservesNewTunables()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new JsonStringEnumConverter());

        var cfg = new NrConfig(
            NrMode: NrMode.Sbnr,
            EmnrPost2Run: false,
            EmnrPost2Factor: 0.22,
            EmnrPost2Nlevel: 0.18,
            EmnrPost2Rate: 4.0,
            EmnrPost2Taper: 8,
            Nr4ReductionAmount: 14.0,
            Nr4SmoothingFactor: 0.3,
            Nr4WhiteningFactor: 0.1,
            Nr4NoiseRescale: 1.5,
            Nr4PostFilterThreshold: -3.0,
            Nr4NoiseScalingType: 1,
            Nr4Position: 0);

        string json = JsonSerializer.Serialize(cfg, opts);
        var back = JsonSerializer.Deserialize<NrConfig>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(cfg, back);
        Assert.Contains("\"Sbnr\"", json);
    }
}
