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

using System.Net;
using System.Net.Http.Json;
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

/// <summary>
/// End-to-end endpoint test for <c>POST /api/mic-gain</c>: drives the real
/// endpoint via <see cref="WebApplicationFactory{TEntryPoint}"/> and asserts
/// that <c>{db}</c> ends up in <see cref="StateDto.MicGainDb"/>, clamped to
/// the endpoint's <c>[-40, +10]</c> range.
///
/// PRD FR-3 (<c>docs/prd/12-tx-feature.md</c>) still requires db → linear
/// gain via <c>10^(db/20)</c>; that conversion now lives at the engine seam
/// in <see cref="DspPipelineService"/> so the persisted form stays in
/// operator-friendly dB. The seam's dB → linear math is exercised inline in
/// <see cref="DspPipelineService"/>'s broadcast path (no engine open in this
/// test factory, so the seam stays intentionally dormant here).
/// </summary>
public class MicGainEndpointTests : IClassFixture<MicGainEndpointTests.Factory>
{
    private readonly Factory _factory;
    public MicGainEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Post0db_PersistsZeroOnState()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/mic-gain", new { db = 0 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0, radio.Snapshot().MicGainDb);
    }

    [Fact]
    public async Task PostPlus10db_PersistsPlus10()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/mic-gain", new { db = 10 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(10, radio.Snapshot().MicGainDb);
    }

    [Fact]
    public async Task PostMinus20db_PersistsMinus20()
    {
        // -20 dB lands in the negative half (attenuation) — verifies the range
        // doesn't get clamped to 0 / unity by an off-by-one in the endpoint.
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/mic-gain", new { db = -20 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(-20, radio.Snapshot().MicGainDb);
    }

    [Fact]
    public async Task PostOutOfRange_ClampsToMinus40AndPlus10()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        // db=-100 clamps to -40.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/mic-gain", new { db = -100 })).StatusCode);
        Assert.Equal(-40, radio.Snapshot().MicGainDb);

        // db=50 clamps to +10.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/mic-gain", new { db = 50 })).StatusCode);
        Assert.Equal(10, radio.Snapshot().MicGainDb);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public StubEngine TestEngine { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                // Replace every IHostedService registration so the real
                // DspPipelineService, TxMetersService, TxAudioIngestStartup
                // and TxTuneDriver do not spin up — we're only testing
                // the HTTP handler.
                services.RemoveAll<IHostedService>();

                // Swap the DspPipelineService singleton for a stubbed
                // subclass whose CurrentEngine is our recording stub.
                services.RemoveAll<DspPipelineService>();
                services.AddSingleton<DspPipelineService>(sp =>
                    new TestPipeline(
                        sp.GetRequiredService<RadioService>(),
                        sp.GetRequiredService<StreamingHub>(),
                        sp.GetRequiredService<ILoggerFactory>(),
                        TestEngine));
            });
        }
    }

    // Minimal IDspEngine that records SetTxPanelGain and SetTxLevelerMaxGain
    // calls for assertion. All other members are safe no-ops because the
    // endpoints under test never call them.
    public sealed class StubEngine : IDspEngine
    {
        public List<double> GainCalls { get; } = new();
        public List<double> LevelerMaxGainCalls { get; } = new();

        public void SetTxPanelGain(double linearGain) => GainCalls.Add(linearGain);
        public void SetTxLevelerMaxGain(double maxGainDb) => LevelerMaxGainCalls.Add(maxGainDb);

        public int TxBlockSamples => 1024;
        public int TxOutputSamples => 1024;
        public int OpenChannel(int sampleRateHz, int pixelWidth) => 0;
        public void CloseChannel(int channelId) { }
        public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples) { }
        public void SetMode(int channelId, RxMode mode) { }
        public void SetFilter(int channelId, int lowHz, int highHz) { }
        public void SetVfoHz(int channelId, long vfoHz) { }
        public void SetCtunShift(int channelId, int shiftHz) { }
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
        public void SetTxMonitorEnabled(bool enabled) { }
        public int ReadTxMonitorAudio(Span<float> output) => 0;
        public bool IsTxMonitorOn => false;
        public void Dispose() { }
    }

    // Non-hosted subclass used only in tests. Overrides CurrentEngine so the
    // endpoint sees the StubEngine; leaves the base ExecuteAsync out of the
    // picture (the test factory removes all IHostedService registrations).
    private sealed class TestPipeline(
        RadioService radio,
        StreamingHub hub,
        ILoggerFactory logs,
        StubEngine engine) : DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), logs)
    {
        public override IDspEngine CurrentEngine => engine;
    }
}
