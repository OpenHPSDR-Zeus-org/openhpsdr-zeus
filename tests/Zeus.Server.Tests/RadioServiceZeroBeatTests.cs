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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class RadioServiceZeroBeatTests
{
    // Number of frames in phase 1 (mirrors RadioService.ZeroBeatPhase1Frames).
    private const int Phase1Frames = 15;

    /// <summary>
    /// Engine stub that returns a synthetic FFT with a single peak at a
    /// chosen offset from DC for every snap call.
    ///
    /// <para>For tests that need phase 2 to be a silent no-op (so the
    /// assertion only measures the phase-1 move), set
    /// <see cref="Phase2PeakDb"/> to a value less than
    /// <see cref="FloorDb"/> + 6 so the SNR gate blocks the second
    /// refinement step.</para>
    /// </summary>
    private sealed class PeakAtBinEngine : IDspEngine
    {
        // Phase 1 (first ZeroBeatPhase1Frames calls = 15)
        public int Phase1PeakBin { get; init; } = 8192;
        public double Phase1PeakDb { get; init; } = -30;

        // Phase 2 (calls 16..60 = next 45)
        public int? Phase2PeakBin { get; init; } = null;   // null = same as phase 1
        public double? Phase2PeakDb { get; init; } = null; // null = same as phase 1

        public double FloorDb { get; init; } = -90;

        // Phase boundary at 15 frames (matches RadioService.ZeroBeatPhase1Frames).
        private const int Phase1Boundary = 15;
        private int _callCount;

        public bool TrySnapRawSpectrum(int channelId, Span<double> outMagnitudesDb)
        {
            _callCount++;
            bool inPhase2 = _callCount > Phase1Boundary;
            int peakBin = inPhase2 ? (Phase2PeakBin ?? Phase1PeakBin) : Phase1PeakBin;
            double peakDb = inPhase2 ? (Phase2PeakDb ?? Phase1PeakDb) : Phase1PeakDb;

            for (int i = 0; i < outMagnitudesDb.Length; i++) outMagnitudesDb[i] = FloorDb;
            outMagnitudesDb[peakBin] = peakDb;
            return true;
        }

        // --- Unused IDspEngine members — safe no-ops or throws; Zero Beat
        //     tests only exercise TrySnapRawSpectrum. Pattern copied from
        //     TxAudioIngestTests.StubEngine in the same test assembly. ---
        public int TxBlockSamples => 1024;
        public int TxOutputSamples => 1024;
        public int OpenChannel(int sampleRateHz, int pixelWidth) => 0;
        public void CloseChannel(int channelId) { }
        public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples) { }
        public void SetMode(int channelId, RxMode mode) { }
        public void SetFilter(int channelId, int lowHz, int highHz) { }
        public void SetVfoHz(int channelId, long vfoHz) { }
        public void SetAgcTop(int channelId, double topDb) { }
        public void SetRxAfGainDb(int channelId, double db) { }
        public void SetNoiseReduction(int channelId, NrConfig cfg) { }
        public void SetZoom(int channelId, int level) { }
        public int ReadAudio(int channelId, Span<float> output) => 0;
        public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public int OpenTxChannel(int outputRateHz = 48_000) => 0;
        public void SetMox(bool moxOn) { }
        public double GetRxaSignalDbm(int channelId) => -140.0;
        public RxStageMeters GetRxStageMeters(int channelId) => RxStageMeters.Silent;
        public void SetTxMode(RxMode mode) { }
        public void SetTxFilter(int lowHz, int highHz) { }
        public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved) => 0;
        public void SetTxPanelGain(double linearGain) { }
        public void SetTxLevelerMaxGain(double maxGainDb) { }
        public void SetTxTune(bool on) { }
        public TxStageMeters GetTxStageMeters() => TxStageMeters.Silent;
        public void SetTwoTone(bool on, double freq1, double freq2, double mag) { }
        public void SetPsEnabled(bool enabled) { }
        public void SetPsControl(bool autoCal, bool singleCal) { }
        public void SetPsAdvanced(bool ptol, double moxDelaySec, double loopDelaySec,
                                  double ampDelayNs, double hwPeak, int ints, int spi) { }
        public void SetPsHwPeak(double hwPeak) { }
        public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
                                        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ) { }
        public PsStageMeters GetPsStageMeters() => PsStageMeters.Silent;
        public void ResetPs() { }
        public void SavePsCorrection(string path) { }
        public void RestorePsCorrection(string path) { }
        public void SetCfcConfig(CfcConfig cfg) { }
        public bool ProcessRxVstChain(Span<float> audio, int frames, int sampleRateHz) => false;
        public bool ProcessTxMicVstChain(Span<float> audio, int frames, int sampleRateHz) => false;
        public void SetTxMonitorEnabled(bool enabled) { }
        public int ReadTxMonitorAudio(Span<float> output) => 0;
        public bool IsTxMonitorOn => false;
        public void SetCtunShift(int channelId, int shiftHz) { /* CTUN: irrelevant for Zero Beat tests */ }
        public void Dispose() { }
    }

    // -------------------------------------------------------------------------
    // Test 1: Peak exactly at DC — no VFO movement expected.
    //
    // With PeakBin = ZeroBeatDcBin (8192), deltaHz = (8192 + 0 - 8192) * hzPerBin = 0.
    // After both phases the VFO must be unchanged and ZeroBeat must return
    // a non-null StateDto (the "phase 1 SNR gate passed but delta=0" branch
    // returns Snapshot() at the end rather than null).
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_in_CWU_with_on_frequency_peak_does_not_move_VFO()
    {
        var engine = new PeakAtBinEngine { Phase1PeakBin = 8192, Phase2PeakBin = 8192 };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.CWU, vfoHz: 14_060_000);

        long before = radio.Snapshot().VfoHz;
        var result = radio.ZeroBeat();
        long after = radio.Snapshot().VfoHz;

        Assert.Equal(before, after);   // peak already at DC → zero delta
        Assert.NotNull(result);
    }

    // -------------------------------------------------------------------------
    // Test 2: Peak 10 bins above DC → coarse tune of ~+29 Hz.
    //
    // 48 kHz / 16 384 bins → hzPerBin ≈ 2.93 Hz.  10 bins × 2.93 ≈ 29.3 Hz.
    // Phase 1 fires (SNR 60 dB >> 6 dB gate) and moves VFO by round(29.3) = 29 Hz.
    // Phase 2 is silenced by the PeakAtBinEngine returning floor-level after the
    // first Phase1Frames calls, so the SNR gate blocks the second refinement step
    // and the net delta is just the phase-1 move.
    //
    // Allow ±2 Hz tolerance for floating-point rounding (2.93 Hz/bin is
    // irrational at exactly 48000/16384).
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_with_peak_above_DC_moves_VFO_up()
    {
        // Phase 2: carrier has shifted to DC after phase-1 tune; SNR gate blocks second move.
        var engine = new PeakAtBinEngine { Phase1PeakBin = 8202, Phase2PeakBin = 8192, Phase2PeakDb = -30 };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.CWU, vfoHz: 14_060_000);

        radio.ZeroBeat();
        long after = radio.Snapshot().VfoHz;
        long delta = after - 14_060_000;

        // 10 bins × (48000/16384) ≈ 29.3 Hz → rounds to 29.
        // ±2 Hz tolerance accounts for floating-point imprecision.
        Assert.InRange(delta, 27, 31);
    }

    // -------------------------------------------------------------------------
    // Test 3: Non-CW mode → ZeroBeat is a silent no-op.
    //
    // Mode-gate: only CWL/CWU invoke the algorithm. Any other mode must
    // return null and leave the VFO unchanged.
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_outside_CW_modes_is_noop()
    {
        var engine = new PeakAtBinEngine { Phase1PeakBin = 8500 };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.USB, vfoHz: 14_200_000);

        var result = radio.ZeroBeat();

        Assert.Null(result);
        Assert.Equal(14_200_000, radio.Snapshot().VfoHz);
    }

    // -------------------------------------------------------------------------
    // Test 4: Flat noise floor (peak − floor < 6 dB SNR gate) → no VFO move.
    //
    // Both phases see peak − floor = 2 dB which is below the 6 dB threshold.
    // Neither phase moves the VFO and ZeroBeat returns null (no-signal path).
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_with_flat_noise_floor_does_not_move_VFO()
    {
        var engine = new PeakAtBinEngine
        {
            Phase1PeakBin = 8500,
            Phase1PeakDb  = -88,   // peak − floor = −88 − (−90) = 2 dB < 6 dB SNR gate
            Phase2PeakDb  = -88,   // phase 2 also below threshold
            FloorDb       = -90,
        };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.CWU, vfoHz: 14_060_000);

        var result = radio.ZeroBeat();

        Assert.Null(result);
        Assert.Equal(14_060_000, radio.Snapshot().VfoHz);
    }

    // -------------------------------------------------------------------------
    // Test 5: Phase 1 quiet, phase 2 has a real carrier.
    //
    // Phase 1: only noise (peak = floor → SNR 0 dB, gate fails → no Δ₁).
    // Phase 2: carrier appears 10 bins above DC (Δ₂ ≈ +29 Hz at 48 kHz).
    // Expected: VFO moves +29 Hz from phase 2 alone, despite phase 1's failure.
    // This exercises the previously-unreachable fall-through path.
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_phase1_quiet_phase2_signal_moves_VFO_from_phase2_only()
    {
        // Phase 1: only noise (peak = floor, SNR = 0 dB, gate fails → no Δ₁).
        // Phase 2: a real carrier appears 10 bins above DC (Δ₂ ≈ +29 Hz at 48 kHz).
        // Expected: VFO moves +29 Hz from phase 2 alone, despite phase 1's failure.
        var engine = new PeakAtBinEngine
        {
            Phase1PeakBin = 8192,
            Phase1PeakDb  = -90,   // = FloorDb → SNR 0 dB → phase 1 gate fails
            Phase2PeakBin = 8202,  // 10 bins above DC ≈ +29 Hz
            Phase2PeakDb  = -30,   // strong signal
            FloorDb       = -90,
        };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.CWU, vfoHz: 14_060_000);

        var result = radio.ZeroBeat();
        long after = radio.Snapshot().VfoHz;

        Assert.NotNull(result);
        Assert.InRange(after - 14_060_000, 27, 31);
    }
}

/// <summary>
/// Endpoint integration tests for POST /api/rx/zero-beat. Drives the real
/// endpoint via <see cref="WebApplicationFactory{TEntryPoint}"/>, asserting
/// both response shapes: 200 OK with <see cref="StateDto"/> when a signal is
/// found, and 422 Unprocessable Entity when the SNR gate rejects the spectrum
/// (flat noise / no signal / no radio connected).
///
/// The test factory replaces <see cref="DspPipelineService"/> with a stub
/// whose <c>CurrentEngine</c> returns a <see cref="PeakAtBinEngine"/> wired
/// for each scenario. Because <see cref="RadioService"/> is now registered via
/// a factory lambda that defers <c>GetRequiredService&lt;DspPipelineService&gt;</c>
/// until the first <c>ZeroBeat</c> call, the stub flows through cleanly
/// without circular-dependency issues during container construction.
/// </summary>
public class ZeroBeatEndpointTests : IClassFixture<ZeroBeatEndpointTests.NoSignalFactory>,
                                     IClassFixture<ZeroBeatEndpointTests.StrongSignalFactory>
{
    private readonly NoSignalFactory _noSignal;
    private readonly StrongSignalFactory _strong;

    public ZeroBeatEndpointTests(NoSignalFactory noSignal, StrongSignalFactory strong)
    {
        _noSignal = noSignal;
        _strong   = strong;
    }

    // -------------------------------------------------------------------------
    // 422 path: mode CWU, wide filter, flat noise floor (SNR 2 dB < 6 dB gate).
    // Both phases fail the SNR gate → ZeroBeat returns null → 422.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Post_returns_422_when_no_signal()
    {
        using var client = _noSignal.CreateClient();

        // Put the radio into CWU so the mode gate passes (the test verifies
        // the SNR gate, not the mode gate).
        var modeResp = await client.PostAsJsonAsync("/api/mode", new { mode = "CWU" });
        Assert.Equal(HttpStatusCode.OK, modeResp.StatusCode);

        // Set a wide CWU filter so the peak at bin 8300 is inside the passband
        // regardless of the default CW pitch / family-filter stored state.
        // FilterLowHz=0 → lo = DC; FilterHighHz=3000 → hi ≈ DC+255 bins at 192 kHz.
        var bwResp = await client.PostAsJsonAsync("/api/bandwidth", new { low = 0, high = 3000 });
        Assert.Equal(HttpStatusCode.OK, bwResp.StatusCode);

        var resp = await client.PostAsync("/api/rx/zero-beat", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no-signal", body.GetProperty("error").GetString());
    }

    // -------------------------------------------------------------------------
    // 200 path: mode CWU, wide filter, strong carrier at a fixed bin.
    // ZeroBeat sees peak − floor = 60 dB >> 6 dB SNR → returns non-null → 200.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Post_returns_200_with_state_when_signal_present()
    {
        using var client = _strong.CreateClient();

        // Put the radio into CWU so the mode gate passes.
        var modeResp = await client.PostAsJsonAsync("/api/mode", new { mode = "CWU" });
        Assert.Equal(HttpStatusCode.OK, modeResp.StatusCode);

        // Set a wide CWU filter so the peak at bin 8300 is inside the passband.
        var bwResp = await client.PostAsJsonAsync("/api/bandwidth", new { low = 0, high = 3000 });
        Assert.Equal(HttpStatusCode.OK, bwResp.StatusCode);

        var resp = await client.PostAsync("/api/rx/zero-beat", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        // StateDto has a VfoHz field — confirm shape is correct.
        Assert.True(body.TryGetProperty("vfoHz", out _),
            "Expected 'vfoHz' in the 200 response body (StateDto shape).");
    }

    // -------------------------------------------------------------------------
    // Test factories
    // -------------------------------------------------------------------------

    // Flat noise. Peak bin 8300 inside the wide CWU filter (0..3000 Hz) set by
    // the test. peakDb -88 dB, FloorDb -90 dB → SNR 2 dB < 6 dB gate →
    // ZeroBeat returns null → endpoint 422.
    public sealed class NoSignalFactory : WebApplicationFactory<Program>
    {
        private static readonly PeakAtBinEngine _engine = new()
        {
            Phase1PeakBin = 8300,
            Phase1PeakDb  = -88,
            Phase2PeakDb  = -88,
            FloorDb       = -90,
        };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DspPipelineService>();
                services.AddSingleton<DspPipelineService>(sp =>
                    new TestPipeline(
                        sp.GetRequiredService<RadioService>(),
                        sp.GetRequiredService<StreamingHub>(),
                        sp.GetRequiredService<ILoggerFactory>(),
                        _engine));
            });
        }
    }

    // Strong carrier. Peak bin 8300 inside the wide CWU filter (0..3000 Hz) set
    // by the test. Phase1PeakDb -30 dB, FloorDb -90 dB → SNR 60 dB >> 6 dB
    // gate → ZeroBeat returns non-null → endpoint 200.
    public sealed class StrongSignalFactory : WebApplicationFactory<Program>
    {
        private static readonly PeakAtBinEngine _engine = new()
        {
            Phase1PeakBin = 8300,   // inside the wide CWU filter set by the test
            Phase2PeakBin = 8300,
            Phase1PeakDb  = -30,
            FloorDb       = -90,
        };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<DspPipelineService>();
                services.AddSingleton<DspPipelineService>(sp =>
                    new TestPipeline(
                        sp.GetRequiredService<RadioService>(),
                        sp.GetRequiredService<StreamingHub>(),
                        sp.GetRequiredService<ILoggerFactory>(),
                        _engine));
            });
        }
    }

    // Non-hosted TestPipeline that overrides CurrentEngine to return the
    // caller-supplied stub. Mirrors the pattern in MicGainEndpointTests.
    private sealed class TestPipeline(
        RadioService radio,
        StreamingHub hub,
        ILoggerFactory logs,
        IDspEngine engine) : DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), logs)
    {
        public override IDspEngine? CurrentEngine => engine;
    }

    // Minimal IDspEngine stub — only TrySnapRawSpectrum matters for ZeroBeat.
    // Reuses the same PeakAtBinEngine pattern from RadioServiceZeroBeatTests.
    private sealed class PeakAtBinEngine : IDspEngine
    {
        public int Phase1PeakBin { get; init; } = 8192;
        public double Phase1PeakDb { get; init; } = -30;
        public int? Phase2PeakBin { get; init; } = null;
        public double? Phase2PeakDb { get; init; } = null;
        public double FloorDb { get; init; } = -90;

        private const int Phase1Boundary = 15;
        private int _callCount;

        public bool TrySnapRawSpectrum(int channelId, Span<double> outMagnitudesDb)
        {
            _callCount++;
            bool inPhase2 = _callCount > Phase1Boundary;
            int peakBin   = inPhase2 ? (Phase2PeakBin ?? Phase1PeakBin) : Phase1PeakBin;
            double peakDb = inPhase2 ? (Phase2PeakDb  ?? Phase1PeakDb)  : Phase1PeakDb;
            for (int i = 0; i < outMagnitudesDb.Length; i++) outMagnitudesDb[i] = FloorDb;
            outMagnitudesDb[peakBin] = peakDb;
            return true;
        }

        public int TxBlockSamples => 1024;
        public int TxOutputSamples => 1024;
        public int OpenChannel(int sampleRateHz, int pixelWidth) => 0;
        public void CloseChannel(int channelId) { }
        public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples) { }
        public void SetMode(int channelId, RxMode mode) { }
        public void SetFilter(int channelId, int lowHz, int highHz) { }
        public void SetVfoHz(int channelId, long vfoHz) { }
        public void SetAgcTop(int channelId, double topDb) { }
        public void SetRxAfGainDb(int channelId, double db) { }
        public void SetNoiseReduction(int channelId, NrConfig cfg) { }
        public void SetZoom(int channelId, int level) { }
        public int ReadAudio(int channelId, Span<float> output) => 0;
        public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;
        public int OpenTxChannel(int outputRateHz = 48_000) => 0;
        public void SetMox(bool moxOn) { }
        public double GetRxaSignalDbm(int channelId) => -140.0;
        public RxStageMeters GetRxStageMeters(int channelId) => RxStageMeters.Silent;
        public void SetTxMode(RxMode mode) { }
        public void SetTxFilter(int lowHz, int highHz) { }
        public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved) => 0;
        public void SetTxPanelGain(double linearGain) { }
        public void SetTxLevelerMaxGain(double maxGainDb) { }
        public void SetTxTune(bool on) { }
        public TxStageMeters GetTxStageMeters() => TxStageMeters.Silent;
        public void SetTwoTone(bool on, double freq1, double freq2, double mag) { }
        public void SetPsEnabled(bool enabled) { }
        public void SetPsControl(bool autoCal, bool singleCal) { }
        public void SetPsAdvanced(bool ptol, double moxDelaySec, double loopDelaySec,
                                  double ampDelayNs, double hwPeak, int ints, int spi) { }
        public void SetPsHwPeak(double hwPeak) { }
        public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
                                        ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ) { }
        public PsStageMeters GetPsStageMeters() => PsStageMeters.Silent;
        public void ResetPs() { }
        public void SavePsCorrection(string path) { }
        public void RestorePsCorrection(string path) { }
        public void SetCfcConfig(CfcConfig cfg) { }
        public bool ProcessRxVstChain(Span<float> audio, int frames, int sampleRateHz) => false;
        public bool ProcessTxMicVstChain(Span<float> audio, int frames, int sampleRateHz) => false;
        public void SetTxMonitorEnabled(bool enabled) { }
        public int ReadTxMonitorAudio(Span<float> output) => 0;
        public bool IsTxMonitorOn => false;
        public void SetCtunShift(int channelId, int shiftHz) { /* CTUN: irrelevant for Zero Beat endpoint tests */ }
        public void Dispose() { }
    }
}
