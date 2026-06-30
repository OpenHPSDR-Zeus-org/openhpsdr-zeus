//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//

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

public sealed class TxPhaseRotatorEndpointValidationTests : IClassFixture<TxPhaseRotatorEndpointValidationTests.Factory>
{
    private readonly Factory _factory;
    public TxPhaseRotatorEndpointValidationTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task PostValidConfig_Returns200_AndUpdatesState()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/api/tx/phase-rotator",
            new
            {
                txPhaseRotator = new
                {
                    enabled = true,
                    cornerHz = 472,
                    stages = 10,
                    reverse = true,
                },
            });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var snap = radio.Snapshot().TxPhaseRotator;
        Assert.NotNull(snap);
        Assert.True(snap!.Enabled);
        Assert.Equal(472, snap.CornerHz);
        Assert.Equal(10, snap.Stages);
        Assert.True(snap.Reverse);
    }

    [Theory]
    [InlineData("cornerHz", "19")]
    [InlineData("cornerHz", "2001")]
    [InlineData("stages", "0")]
    [InlineData("stages", "17")]
    public async Task PostOutOfRange_Returns400_AndDoesNotMutateState(string field, string value)
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        var before = radio.Snapshot().TxPhaseRotator;
        using var client = _factory.CreateClient();

        var json =
            $"{{\"txPhaseRotator\":{{" +
            $"\"enabled\":true," +
            $"\"cornerHz\":{(field == "cornerHz" ? value : "472")}," +
            $"\"stages\":{(field == "stages" ? value : "10")}," +
            $"\"reverse\":false" +
            $"}}}}";

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/tx/phase-rotator", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var after = radio.Snapshot().TxPhaseRotator;
        Assert.Equal(before?.Enabled, after?.Enabled);
        Assert.Equal(before?.CornerHz, after?.CornerHz);
        Assert.Equal(before?.Stages, after?.Stages);
        Assert.Equal(before?.Reverse, after?.Reverse);
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
