// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Endpoint validation tests for POST /api/tx/leveling. Drives the real handler
// via WebApplicationFactory and asserts that:
//   - A valid in-range payload is accepted with 200 and updates RadioService
//     state.
//   - An out-of-range field (alcMaxGainDb / alcDecayMs / levelerDecayMs /
//     compressorGainDb) returns 400 and does not mutate RadioService state.
//
// Pattern mirrors SquelchEndpointValidationTests, reusing
// MicGainEndpointTests.StubEngine so no new stub is needed.

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
/// Tests the range guards on <c>POST /api/tx/leveling</c>. Any field outside its
/// Thetis range must be rejected with 400 and must not mutate
/// <see cref="RadioService"/> state.
/// </summary>
public class TxLevelingEndpointValidationTests : IClassFixture<TxLevelingEndpointValidationTests.Factory>
{
    private readonly Factory _factory;
    public TxLevelingEndpointValidationTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task PostValidConfig_Returns200_AndUpdatesState()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/tx/leveling",
            new
            {
                txLeveling = new
                {
                    alcMaxGainDb = 6.0,
                    alcDecayMs = 20,
                    levelerEnabled = false,
                    levelerDecayMs = 250,
                    compressorEnabled = true,
                    compressorGainDb = 9.0,
                },
            });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var snap = radio.Snapshot().TxLeveling;
        Assert.NotNull(snap);
        Assert.Equal(6.0, snap!.AlcMaxGainDb);
        Assert.Equal(20, snap.AlcDecayMs);
        Assert.False(snap.LevelerEnabled);
        Assert.Equal(250, snap.LevelerDecayMs);
        Assert.True(snap.CompressorEnabled);
        Assert.Equal(9.0, snap.CompressorGainDb);
    }

    // Each row drives one field out of range; the others stay valid so only the
    // field under test trips the 400.
    [Theory]
    [InlineData("alcMaxGainDb", "-1")]
    [InlineData("alcMaxGainDb", "121")]
    [InlineData("alcDecayMs", "0")]
    [InlineData("alcDecayMs", "51")]
    [InlineData("levelerDecayMs", "0")]
    [InlineData("levelerDecayMs", "5001")]
    [InlineData("compressorGainDb", "-1")]
    [InlineData("compressorGainDb", "21")]
    public async Task PostOutOfRange_Returns400_AndDoesNotMutateState(string field, string value)
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        var before = radio.Snapshot().TxLeveling;
        using var client = _factory.CreateClient();

        // Build a payload with every field valid, then override the one under
        // test with the out-of-range value.
        double alc = 3.0;
        int alcDecay = 10;
        int lvlrDecay = 100;
        double comp = 0.0;
        var json =
            $"{{\"txLeveling\":{{" +
            $"\"alcMaxGainDb\":{(field == "alcMaxGainDb" ? value : alc.ToString(System.Globalization.CultureInfo.InvariantCulture))}," +
            $"\"alcDecayMs\":{(field == "alcDecayMs" ? value : alcDecay.ToString())}," +
            $"\"levelerEnabled\":true," +
            $"\"levelerDecayMs\":{(field == "levelerDecayMs" ? value : lvlrDecay.ToString())}," +
            $"\"compressorEnabled\":false," +
            $"\"compressorGainDb\":{(field == "compressorGainDb" ? value : comp.ToString(System.Globalization.CultureInfo.InvariantCulture))}" +
            $"}}}}";

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/tx/leveling", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // State must be unchanged.
        var after = radio.Snapshot().TxLeveling;
        Assert.Equal(before?.AlcMaxGainDb, after?.AlcMaxGainDb);
        Assert.Equal(before?.AlcDecayMs, after?.AlcDecayMs);
        Assert.Equal(before?.LevelerDecayMs, after?.LevelerDecayMs);
        Assert.Equal(before?.CompressorGainDb, after?.CompressorGainDb);
    }

    public sealed class Factory : IsolatedPrefsFactory
    {
        private readonly MicGainEndpointTests.StubEngine _engine = new();

        protected override void ConfigureExtra(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DspPipelineService>();
                services.AddSingleton<DspPipelineService>(sp =>
                    new TestPipeline(
                        sp.GetRequiredService<RadioService>(),
                        sp.GetRequiredService<StreamingHub>(),
                        sp.GetRequiredService<ILoggerFactory>(),
                        _engine));
            });
        }

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
