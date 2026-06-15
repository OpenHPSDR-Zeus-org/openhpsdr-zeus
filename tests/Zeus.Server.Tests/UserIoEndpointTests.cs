// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for read-only P2 user analog/digital I/O diagnostics.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Zeus.Server.Tests;

public sealed class UserIoEndpointTests
{
    [Fact]
    public async Task UserIoLabels_ReturnsDefaultLinesBeforeTelemetry()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/user-io/labels");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;
        var lines = root.GetProperty("lines").EnumerateArray().ToArray();

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.GetProperty("p2Attached").GetBoolean());
        Assert.Equal(0, root.GetProperty("p2Packets").GetInt64());
        Assert.Equal(12, lines.Length);
        Assert.Equal("userAdc0", lines[0].GetProperty("id").GetString());
        Assert.Equal("analog", lines[0].GetProperty("kind").GetString());
        Assert.Equal("User ADC 0", lines[0].GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("rawAdc").ValueKind);
        Assert.Equal("userDigital7", lines[^1].GetProperty("id").GetString());
        Assert.Equal("digital", lines[^1].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, lines[^1].GetProperty("digitalState").ValueKind);
        Assert.Contains("No P2 user I/O sample", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task UserIoActions_ReturnsUnarmedActionReadiness()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/user-io/actions");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.GetProperty("actionBindingsConfigured").GetBoolean());
        Assert.Equal(12, root.GetProperty("lines").GetArrayLength());
        Assert.Contains("remain unarmed", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesLiveUserIoEndpoints()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var userIo = body.RootElement
            .GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "hardware.user-io");
        var controls = userIo.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("/api/radio/user-io/labels", controls);
        Assert.Contains("/api/radio/user-io/actions", controls);
        Assert.DoesNotContain("planned:/api/radio/user-io/labels", controls);
        Assert.DoesNotContain("planned:/api/radio/user-io/actions", controls);
    }

    private sealed class Factory : WebApplicationFactory<Program>
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
