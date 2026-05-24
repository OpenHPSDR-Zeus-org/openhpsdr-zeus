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
using System.Text;
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
/// End-to-end endpoint test for <c>POST /api/tx/leveler-max-gain</c>: drives
/// the real handler via <see cref="WebApplicationFactory{TEntryPoint}"/>,
/// asserting that a JSON <c>{gain}</c> body updates the persisted
/// <see cref="StateDto.LevelerMaxGainDb"/>, and that out-of-range values
/// are rejected with a 400.
///
/// Post-persistence migration the endpoint routes through
/// <c>RadioService.SetTxLevelerMaxGain</c> → <c>Mutate</c> → the StateChanged
/// listener that pushes WDSP's <c>SetTXALevelerTop</c>. The HTTP test pins
/// the request/response contract and the state-mutation hop; the engine-seam
/// behaviour is covered by <see cref="DspPipelineService"/>'s broadcast path
/// (no engine open in this test factory so the seam is intentionally inert).
/// </summary>
public class LevelerMaxGainEndpointTests : IClassFixture<LevelerMaxGainEndpointTests.Factory>
{
    private readonly Factory _factory;
    public LevelerMaxGainEndpointTests(Factory factory) => _factory = factory;

    [Theory]
    [InlineData(0.0)]   // band floor
    [InlineData(5.0)]   // W1AEX / softerhardware default
    [InlineData(15.0)]  // band ceiling (Thetis stock)
    public async Task PostInRange_UpdatesRadioServiceState(double gain)
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/tx/leveler-max-gain", new { gain });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(gain, radio.Snapshot().LevelerMaxGainDb, precision: 6);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(15.1)]
    public async Task PostOutOfRange_Returns400_AndDoesNotMutateState(double gain)
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        double before = radio.Snapshot().LevelerMaxGainDb;
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/tx/leveler-max-gain", new { gain });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(before, radio.Snapshot().LevelerMaxGainDb, precision: 6);
    }

    [Fact]
    public async Task PostNaN_Returns400_AndDoesNotMutateState()
    {
        // System.Text.Json refuses to serialize NaN via PostAsJsonAsync, so
        // simulate a rogue client sending the JavaScript literal `NaN` by
        // posting raw string content. The server's guard
        // (double.IsNaN(req.Gain)) is defensive belt-and-braces against a
        // deserializer that later enables AllowNamedFloatingPointLiterals —
        // the test pins the rejection shape in place now.
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        double before = radio.Snapshot().LevelerMaxGainDb;
        using var client = _factory.CreateClient();
        using var content = new StringContent(
            "{\"gain\":NaN}", Encoding.UTF8, "application/json");

        var resp = await client.PostAsync("/api/tx/leveler-max-gain", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(before, radio.Snapshot().LevelerMaxGainDb, precision: 6);
    }


    public sealed class Factory : WebApplicationFactory<Program>
    {
        public MicGainEndpointTests.StubEngine TestEngine { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                // Strip every IHostedService so the real DspPipelineService /
                // TxMetersService / TxAudioIngestStartup / TxTuneDriver never
                // spin up — we're only testing the HTTP handler.
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

        // Non-hosted subclass used only in tests. Mirrors
        // MicGainEndpointTests.TestPipeline so the stub drives CurrentEngine
        // while the base class's ExecuteAsync stays dormant.
        private sealed class TestPipeline(
            RadioService radio,
            StreamingHub hub,
            ILoggerFactory logs,
            MicGainEndpointTests.StubEngine engine) : DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), logs)
        {
            public override IDspEngine CurrentEngine => engine;
        }
    }
}
