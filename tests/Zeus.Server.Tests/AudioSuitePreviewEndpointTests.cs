// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioSuitePreviewEndpointTests : IClassFixture<AudioSuitePreviewEndpointTests.Factory>
{
    private readonly Factory _factory;
    public AudioSuitePreviewEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task GetReportsTxMonitorState()
    {
        using var client = _factory.CreateClient();

        using var json = await client.GetFromJsonAsync<JsonDocument>("/api/audio-suite/preview");

        Assert.NotNull(json);
        Assert.True(json!.RootElement.GetProperty("supported").GetBoolean());
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task PutTogglesTxMonitorState()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var on = await client.PutAsJsonAsync("/api/audio-suite/preview", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(radio.Snapshot().TxMonitorEnabled);

        var off = await client.PutAsJsonAsync("/api/audio-suite/preview", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(radio.Snapshot().TxMonitorEnabled);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }
    }
}
