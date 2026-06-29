// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Endpoint validation tests for POST /api/rx/agc. Drives the real handler via
// WebApplicationFactory and asserts that:
//   - AgcMode ordinals stay pinned to WDSP / Thetis wire values.
//   - Valid AgcMode strings (PascalCase) are accepted with 200.
//   - Valid AgcMode numeric wire values (0..5) are accepted with 200.
//   - An out-of-range / unknown AgcMode (the Enum.IsDefined guard) returns 400
//     and does not mutate RadioService state.
//
// Pattern mirrors LevelerMaxGainEndpointTests, reusing MicGainEndpointTests.StubEngine
// and its TestPipeline helper so no new stub is needed.

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
/// Tests the <c>Enum.IsDefined</c> guard on <c>POST /api/rx/agc</c>.
/// Valid <see cref="AgcMode"/> strings and ordinals must map to the same state,
/// while unknown values must be rejected without mutating <see cref="RadioService"/>.
/// </summary>
public class AgcEndpointValidationTests : IClassFixture<AgcEndpointValidationTests.Factory>
{
    private readonly Factory _factory;
    public AgcEndpointValidationTests(Factory factory) => _factory = factory;

    [Theory]
    [InlineData(0, AgcMode.Fixed)]
    [InlineData(1, AgcMode.Long)]
    [InlineData(2, AgcMode.Slow)]
    [InlineData(3, AgcMode.Med)]
    [InlineData(4, AgcMode.Fast)]
    [InlineData(5, AgcMode.Custom)]
    public void AgcMode_OrdinalsMatchWdspThetisWireValues(int ordinal, AgcMode mode)
    {
        Assert.Equal(ordinal, (int)mode);
    }

    [Theory]
    [InlineData("Fixed")]
    [InlineData("Long")]
    [InlineData("Slow")]
    [InlineData("Med")]
    [InlineData("Fast")]
    [InlineData("Custom")]
    public async Task PostValidMode_Returns200_AndUpdatesState(string mode)
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/rx/agc", new { agc = new { mode } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(mode, radio.Snapshot().Agc?.Mode.ToString());
    }

    [Theory]
    [InlineData(0, AgcMode.Fixed)]
    [InlineData(1, AgcMode.Long)]
    [InlineData(2, AgcMode.Slow)]
    [InlineData(3, AgcMode.Med)]
    [InlineData(4, AgcMode.Fast)]
    [InlineData(5, AgcMode.Custom)]
    public async Task PostValidNumericMode_Returns200_AndUpdatesState(int modeValue, AgcMode expected)
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        using var content = new StringContent(
            $"{{\"agc\":{{\"mode\":{modeValue}}}}}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/rx/agc", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(expected, radio.Snapshot().Agc?.Mode);
    }

    [Fact]
    public async Task PostOutOfRangeMode_Returns400_AndDoesNotMutateState()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        var before = radio.Snapshot().Agc?.Mode;
        using var client = _factory.CreateClient();

        // Post a body where "mode" is an unknown string — the server must reject it.
        using var content = new StringContent(
            "{\"agc\":{\"mode\":\"Bogus\"}}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/rx/agc", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // State must be unchanged.
        Assert.Equal(before, radio.Snapshot().Agc?.Mode);
    }

    [Fact]
    public async Task PostNumericOutOfRangeMode_Returns400_AndDoesNotMutateState()
    {
        // When the server is configured WITHOUT JsonStringEnumConverter, a numeric
        // 99 would be deserialized to (AgcMode)99. The Enum.IsDefined guard catches
        // that case. Post as a numeric to exercise this separate path.
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        var before = radio.Snapshot().Agc?.Mode;
        using var client = _factory.CreateClient();

        // Send raw numeric 99 as the mode value — bypasses string enum converter.
        using var content = new StringContent(
            "{\"agc\":{\"mode\":99}}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/rx/agc", content);

        // Either 400 (guard fires) or 400 (JsonStringEnumConverter rejects unknown
        // numeric) — either way the state must not have mutated.
        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            Assert.Equal(before, radio.Snapshot().Agc?.Mode);
        }
        else
        {
            // If the framework accepted the request (mode serialized as numeric
            // and Enum.IsDefined fired), it's still a 400.
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
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
