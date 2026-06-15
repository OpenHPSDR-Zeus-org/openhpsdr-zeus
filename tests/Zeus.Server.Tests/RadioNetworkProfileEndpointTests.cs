// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for the read-only radio network profile snapshot.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Zeus.Server.Tests;

public sealed class RadioNetworkProfileEndpointTests
{
    [Fact]
    public async Task NetworkProfile_ReturnsDisconnectedBaseline()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/network-profile");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Disconnected", root.GetProperty("connectionStatus").GetString());
        Assert.Equal("udp", root.GetProperty("transport").GetString());
        Assert.Equal("disconnected", root.GetProperty("healthStatus").GetString());
        Assert.False(root.GetProperty("p1").GetProperty("attached").GetBoolean());
        Assert.False(root.GetProperty("p2").GetProperty("attached").GetBoolean());
        Assert.Equal(0, root.GetProperty("p1").GetProperty("dropRatioPct").GetDouble());
        Assert.Contains("No radio transport is active", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task NetworkProfile_ReportsStateOnlyConnectedWhenNoClientAttached()
    {
        using var factory = new Factory();
        using (var scope = factory.Services.CreateScope())
        {
            var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
            radio.MarkProtocol2Connected("192.168.1.10:1024", 384_000);
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/radio/network-profile");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal("Connected", root.GetProperty("connectionStatus").GetString());
        Assert.Equal("192.168.1.10:1024", root.GetProperty("endpoint").GetString());
        Assert.Equal(384_000, root.GetProperty("sampleRateHz").GetInt32());
        Assert.Equal("state-only", root.GetProperty("healthStatus").GetString());
        Assert.False(root.GetProperty("p2").GetProperty("attached").GetBoolean());
        Assert.Contains("no protocol client is attached", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesLiveNetworkProfileEndpoint()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var inventory = body.RootElement
            .GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "hardware.inventory.discovery");
        var controls = inventory.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("/api/radio/network-profile", controls);
        Assert.DoesNotContain("planned:/api/radio/network-profile", controls);
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
