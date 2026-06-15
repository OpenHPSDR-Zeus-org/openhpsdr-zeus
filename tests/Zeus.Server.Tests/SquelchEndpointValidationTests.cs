// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Endpoint validation tests for POST /api/rx/squelch. Drives the real handler
// via WebApplicationFactory and asserts that:
//   - A valid (enabled + in-range level) payload is accepted with 200 and
//     updates RadioService state.
//   - An out-of-range Level (< 0 or > 100) returns 400 and does not mutate
//     RadioService state.
//
// Pattern mirrors AgcEndpointValidationTests, reusing MicGainEndpointTests.StubEngine
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
/// Tests the Level range guard on <c>POST /api/rx/squelch</c>. A Level outside
/// 0..100 must be rejected with 400 and must not mutate
/// <see cref="RadioService"/> state.
/// </summary>
public class SquelchEndpointValidationTests : IClassFixture<SquelchEndpointValidationTests.Factory>
{
    private readonly Factory _factory;
    public SquelchEndpointValidationTests(Factory factory) => _factory = factory;

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 50)]
    [InlineData(true, 100)]
    [InlineData(false, 0)]
    public async Task PostValidConfig_Returns200_AndUpdatesState(bool enabled, int level)
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/rx/squelch", new { squelch = new { enabled, level } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var snap = radio.Snapshot().Squelch;
        Assert.NotNull(snap);
        Assert.Equal(enabled, snap!.Enabled);
        Assert.Equal(level, snap.Level);
        Assert.True(snap.Adaptive);
    }

    [Fact]
    public async Task PostFixedConfig_Returns200_AndUpdatesAdaptiveFlag()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/rx/squelch", new { squelch = new { enabled = true, level = 42, adaptive = false } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var snap = radio.Snapshot().Squelch;
        Assert.NotNull(snap);
        Assert.True(snap!.Enabled);
        Assert.Equal(42, snap.Level);
        Assert.False(snap.Adaptive);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(999)]
    public async Task PostOutOfRangeLevel_Returns400_AndDoesNotMutateState(int level)
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        var before = radio.Snapshot().Squelch;
        using var client = _factory.CreateClient();

        using var content = new StringContent(
            $"{{\"squelch\":{{\"enabled\":true,\"level\":{level}}}}}",
            Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/rx/squelch", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // State must be unchanged.
        var after = radio.Snapshot().Squelch;
        Assert.Equal(before?.Enabled, after?.Enabled);
        Assert.Equal(before?.Level, after?.Level);
        Assert.Equal(before?.Adaptive, after?.Adaptive);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly MicGainEndpointTests.StubEngine _engine = new();

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
