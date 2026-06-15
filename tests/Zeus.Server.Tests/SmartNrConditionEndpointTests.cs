// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for /api/dsp/nr-condition. The route exposes the existing
// frontend Smart NR condition feed plus backend NR runtime readiness.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Zeus.Server.Tests;

public sealed class SmartNrConditionEndpointTests
{
    [Fact]
    public async Task GetBeforeFrontendScene_ReturnsMissingConditionWithNrRuntime()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dsp/nr-condition");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal("missing", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal("Off", root.GetProperty("requestedNrMode").GetString());
        Assert.Equal("Off", root.GetProperty("effectiveNrMode").GetString());
        Assert.Contains(
            root.GetProperty("nr4Readiness").GetString(),
            new[] { "available", "missing-sbnr-exports", "wdsp-native-unloadable" });
        Assert.Contains("No frontend DSP scene", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task GetAfterFrontendScene_ReturnsFreshSmartNrCondition()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/radio/diagnostics/dsp-scene", new
        {
            sourceClientId = "frontend-test",
            mode = "USB",
            signalProfile = "dx",
            signalReason = "coherent weak ridge",
            smartNrProfile = "NR2",
            smartNrReason = "weak sparse signal",
            smartNrRecommendation = "Keep RX headroom and use gentle NR2",
            smartNrHeldByRxChain = true,
            smartNrRxChainLabel = "ADC headroom limited",
            maxSnrDb = 7.14,
            coherentMaxSnrDb = 6.83,
            occupiedPct = 1.23,
            coherentOccupiedPct = 0.84,
            impulsivePct = 0.12,
            peakCount = 1,
            coherentPeakCount = 1,
            coherentSubthresholdSignal = true,
            sourceAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var resp = await client.GetAsync("/api/dsp/nr-condition");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("fresh", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("fresh").GetBoolean());
        Assert.False(root.GetProperty("stale").GetBoolean());
        Assert.Equal("USB", root.GetProperty("mode").GetString());
        Assert.Equal("NR2", root.GetProperty("profile").GetString());
        Assert.Equal("weak sparse signal", root.GetProperty("reason").GetString());
        Assert.Equal("Keep RX headroom and use gentle NR2", root.GetProperty("recommendation").GetString());
        Assert.True(root.GetProperty("heldByRxChain").GetBoolean());
        Assert.Equal("ADC headroom limited", root.GetProperty("rxChainLabel").GetString());
        Assert.Equal(7.1, root.GetProperty("maxSnrDb").GetDouble());
        Assert.Equal(6.8, root.GetProperty("coherentMaxSnrDb").GetDouble());
        Assert.Equal(1, root.GetProperty("peakCount").GetInt32());
        Assert.True(root.GetProperty("coherentSubthresholdSignal").GetBoolean());
        Assert.Equal("Off", root.GetProperty("effectiveNrMode").GetString());
        Assert.Contains("constrained by RX-chain health", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesLiveNrConditionEndpoint()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var smartNr = body.RootElement
            .GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "rx.smart-nr.adaptive");
        var controls = smartNr.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("/api/dsp/nr-condition", controls);
        Assert.DoesNotContain("planned:/api/dsp/nr-condition", controls);
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
