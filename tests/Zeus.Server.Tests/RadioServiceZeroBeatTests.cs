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
    /// <summary>
    /// Engine stub that returns a synthetic FFT with a single peak at a
    /// chosen bin for every snap call.
    ///
    /// <para>ZeroBeat collects <c>ZeroBeatPhase1Frames</c> (30) frames and
    /// max-holds per bin before measuring the peak. To simulate a signal that
    /// only appears partway through the accumulation window, give the first
    /// half (calls 1..15) and the second half (calls 16..30) different
    /// peak bins / levels via the <c>Phase2*</c> properties; max-hold keeps
    /// whichever is louder per bin.</para>
    /// </summary>
    private sealed class PeakAtBinEngine : IDspEngine
    {
        // First half of the peak-hold window (calls 1..15)
        public int Phase1PeakBin { get; init; } = 8192;
        public double Phase1PeakDb { get; init; } = -30;

        // Second half of the peak-hold window (calls 16..30)
        public int? Phase2PeakBin { get; init; } = null;   // null = same as first half
        public double? Phase2PeakDb { get; init; } = null; // null = same as first half

        public double FloorDb { get; init; } = -90;

        // Halfway through the 30-frame ZeroBeat peak-hold window.
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
    // Test 1: Carrier already at the CW pitch — no meaningful VFO movement.
    //
    // The zero-beat target is NOT DC (bin 8192). In CW the LO is offset by the
    // CW pitch (600 Hz) from the dial, so a carrier the operator is already
    // perfectly zero-beat on lands at +cw_pitch in the baseband FFT, which on
    // the inverted analyzer axis is bin 8192 − round(600 / 2.93) = 7987.
    // deltaHz = rawOffsetHz − cw_pitch ≈ 600.6 − 600 = 0.6 Hz → rounds to a
    // sub-bin residual of at most a couple Hz (2.93 Hz/bin grid at 48 kHz).
    // ZeroBeat must return a non-null StateDto (gate passed) and leave the VFO
    // effectively where it was.
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_in_CWU_with_on_frequency_peak_does_not_move_VFO()
    {
        // Bin nearest +600 Hz on the inverted axis (8192 − round(600/2.93) = 7987).
        var engine = new PeakAtBinEngine { Phase1PeakBin = 7987, Phase2PeakBin = 7987 };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.CWU, vfoHz: 14_060_000);

        long before = radio.Snapshot().VfoHz;
        var result = radio.ZeroBeat();
        long after = radio.Snapshot().VfoHz;

        // Carrier already at the CW pitch → only the ±1-bin grid residual.
        Assert.InRange(after - before, -2, 2);
        Assert.NotNull(result);
    }

    // -------------------------------------------------------------------------
    // Test 2: Carrier ~30 Hz above the CW pitch → VFO tunes up ~+30 Hz.
    //
    // 48 kHz / 16 384 bins → hzPerBin ≈ 2.93 Hz. The zero-beat target is at
    // +cw_pitch (600 Hz). A peak at bin 7977 sits at rawOffsetHz =
    // (8192 − 7977) × 2.93 ≈ 629.9 Hz, so deltaHz = 629.9 − 600 ≈ +29.9 Hz →
    // the dial moves UP onto the carrier. SNR 60 dB ≫ 6 dB gate, so the move
    // fires. ±2 Hz tolerance for the 2.93 Hz/bin grid quantisation.
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_with_peak_above_DC_moves_VFO_up()
    {
        // Bin 7977 ≈ +630 Hz on the inverted axis = 30 Hz above the 600 Hz pitch.
        var engine = new PeakAtBinEngine { Phase1PeakBin = 7977, Phase2PeakBin = 7977 };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.CWU, vfoHz: 14_060_000);

        radio.ZeroBeat();
        long after = radio.Snapshot().VfoHz;
        long delta = after - 14_060_000;

        // (8192 − 7977) × (48000/16384) − 600 ≈ +29.9 Hz → rounds to +30.
        // ±2 Hz tolerance accounts for floating-point / grid imprecision.
        Assert.InRange(delta, 28, 32);
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
    // The passband peak − floor = 2 dB, below the 6 dB threshold for the whole
    // peak-hold window. ZeroBeat never moves the VFO and returns null (no-signal
    // path). Bin 8500 is inside the (-1000, +1000) test filter at 48 kHz.
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_with_flat_noise_floor_does_not_move_VFO()
    {
        var engine = new PeakAtBinEngine
        {
            Phase1PeakBin = 8500,
            Phase1PeakDb  = -88,   // peak − floor = −88 − (−90) = 2 dB < 6 dB SNR gate
            Phase2PeakDb  = -88,   // still below threshold in the second half
            FloorDb       = -90,
        };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.CWU, vfoHz: 14_060_000);

        var result = radio.ZeroBeat();

        Assert.Null(result);
        Assert.Equal(14_060_000, radio.Snapshot().VfoHz);
    }

    // -------------------------------------------------------------------------
    // Test 5: Quiet start, carrier appears partway through the peak-hold window.
    //
    // The first half of the accumulation window is only noise (peak = floor),
    // then a real carrier shows up ~30 Hz above the CW pitch. Because ZeroBeat
    // max-holds per bin across the whole window, the late carrier is still
    // captured and clears the SNR gate. Expected: VFO tunes up ~+30 Hz onto it.
    // This exercises the "signal that wasn't there at button-press" path.
    // -------------------------------------------------------------------------
    [Fact]
    public void ZeroBeat_phase1_quiet_phase2_signal_moves_VFO_from_phase2_only()
    {
        // First half: only noise (peak = floor, SNR 0 dB, nothing to lock onto).
        // Second half: a real carrier at bin 7977 ≈ +630 Hz = 30 Hz above the
        // 600 Hz pitch. Max-hold preserves it → VFO moves up ~+30 Hz.
        var engine = new PeakAtBinEngine
        {
            Phase1PeakBin = 7987,  // bin at the pitch but at floor level (no signal yet)
            Phase1PeakDb  = -90,   // = FloorDb → SNR 0 dB → nothing in the first half
            Phase2PeakBin = 7977,  // carrier 30 Hz above the pitch
            Phase2PeakDb  = -30,   // strong signal
            FloorDb       = -90,
        };
        using var radio = TestRadioServiceFactory.WithEngine(engine, mode: RxMode.CWU, vfoHz: 14_060_000);

        var result = radio.ZeroBeat();
        long after = radio.Snapshot().VfoHz;

        Assert.NotNull(result);
        Assert.InRange(after - 14_060_000, 28, 32);
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
    // 422 path: mode CWU, wide filter, near-flat noise floor (SNR 1 dB, below
    // the 2 dB effective gate at 192 kHz). The peak in the passband never
    // clears the SNR gate → ZeroBeat returns null → endpoint 422.
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Post_returns_422_when_no_signal()
    {
        using var client = _noSignal.CreateClient();

        // Put the radio into CWU so the mode gate passes (the test verifies
        // the SNR gate, not the mode gate).
        var modeResp = await client.PostAsJsonAsync("/api/mode", new { mode = "CWU" });
        Assert.Equal(HttpStatusCode.OK, modeResp.StatusCode);

        // Set a wide CWU filter so the peak at bin 8100 is inside the passband.
        // No radio is connected, so the snapshot sample rate is the 192 kHz
        // default → hzPerBin ≈ 11.72 Hz. Filter (0, 3000) maps to passband bins
        // [DC − round(3000/11.72), DC] = [7936, 8192]; bin 8100 sits inside it.
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

        // Set a wide CWU filter so the peak at bin 8100 is inside the passband
        // ([7936, 8192] at the 192 kHz default sample rate).
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

    // Flat noise. Peak bin 8100 inside the wide CWU filter (0..3000 Hz =
    // passband bins [7936, 8192] at 192 kHz) set by the test, so the SNR gate
    // — not a passband miss — is what's exercised. peakDb -89 dB, FloorDb
    // -90 dB → SNR 1 dB. At 192 kHz the gate floors at 2 dB (6 dB − 10·log10(4)
    // ≈ −0.02 dB → clamped to 2.0), so 1 dB < 2.0 → ZeroBeat returns null →
    // endpoint 422.
    public sealed class NoSignalFactory : WebApplicationFactory<Program>
    {
        private static readonly PeakAtBinEngine _engine = new()
        {
            Phase1PeakBin = 8100,
            Phase1PeakDb  = -89,
            Phase2PeakBin = 8100,
            Phase2PeakDb  = -89,
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

    // Strong carrier. Peak bin 8100 inside the wide CWU filter (0..3000 Hz =
    // passband bins [7936, 8192] at 192 kHz) set by the test. Phase1PeakDb
    // -30 dB, FloorDb -90 dB → SNR 60 dB >> 6 dB gate → ZeroBeat returns
    // non-null → endpoint 200.
    public sealed class StrongSignalFactory : WebApplicationFactory<Program>
    {
        private static readonly PeakAtBinEngine _engine = new()
        {
            Phase1PeakBin = 8100,   // inside the passband [7936, 8192] at 192 kHz
            Phase2PeakBin = 8100,
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
