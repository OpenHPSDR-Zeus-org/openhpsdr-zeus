// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for /api/dsp/display-intelligence. This route persists the
// existing frontend Signal Intelligence weak-signal policy without moving the
// display DSP math out of the frontend.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DisplayIntelligenceEndpointTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-display-intel-endpoint-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task GetFirstRun_ReturnsBalancedDefaults()
    {
        using var factory = new Factory(_dbPath);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/dsp/display-intelligence");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        Assert.Equal("balanced", body.RootElement.GetProperty("profileId").GetString());
        Assert.False(body.RootElement.GetProperty("popEnabled").GetBoolean());
        Assert.False(body.RootElement.GetProperty("autoNotchEnabled").GetBoolean());
        Assert.True(body.RootElement.GetProperty("visualAgcEnabled").GetBoolean());
        Assert.Equal(92, body.RootElement.GetProperty("waterfallReliefDepth").GetInt32());
        Assert.Equal(64, body.RootElement.GetProperty("waterfallSmoothness").GetInt32());
        Assert.Equal(4000, body.RootElement.GetProperty("snapRadiusHz").GetInt32());
    }

    [Fact]
    public async Task PutValidSettings_PersistsAfterRestart()
    {
        var request = new
        {
            profileId = "dx",
            popEnabled = true,
            snapEnabled = true,
            autoNotchEnabled = true,
            autoProfileEnabled = true,
            visualAgcEnabled = true,
            impulseRejectEnabled = true,
            popFloorDb = 2.0,
            popSpanDb = 24.0,
            popGamma = 0.42,
            popRenderIntensity = 92,
            waterfallReliefDepth = 64,
            waterfallSmoothness = 52,
            coherenceHoldGate = 0.38,
            coherenceBoostDb = 5.5,
            ridgeBoost = 0.5,
            ridgeMaxBoostDb = 10.0,
            visualAgcStrength = 70,
            impulseRejectDb = 16,
            snapRadiusHz = 5000,
            snapMinSnrDb = 5.0,
            peakMinSnrDb = 7.0,
        };

        using (var first = new Factory(_dbPath))
        using (var client = first.CreateClient())
        {
            var resp = await client.PutAsJsonAsync("/api/dsp/display-intelligence", request);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            Assert.Equal("dx", body.RootElement.GetProperty("profileId").GetString());
            Assert.Equal(92, body.RootElement.GetProperty("popRenderIntensity").GetInt32());
            Assert.Equal(64, body.RootElement.GetProperty("waterfallReliefDepth").GetInt32());
            Assert.Equal(52, body.RootElement.GetProperty("waterfallSmoothness").GetInt32());
            Assert.True(body.RootElement.GetProperty("autoNotchEnabled").GetBoolean());
        }

        using (var restarted = new Factory(_dbPath))
        using (var client = restarted.CreateClient())
        {
            var resp = await client.GetAsync("/api/dsp/display-intelligence");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            Assert.Equal("dx", body.RootElement.GetProperty("profileId").GetString());
            Assert.True(body.RootElement.GetProperty("popEnabled").GetBoolean());
            Assert.True(body.RootElement.GetProperty("autoNotchEnabled").GetBoolean());
            Assert.Equal(64, body.RootElement.GetProperty("waterfallReliefDepth").GetInt32());
            Assert.Equal(52, body.RootElement.GetProperty("waterfallSmoothness").GetInt32());
            Assert.Equal(5000, body.RootElement.GetProperty("snapRadiusHz").GetInt32());
        }
    }

    [Theory]
    [InlineData("{\"profileId\":\"wide\",\"popEnabled\":false,\"snapEnabled\":false,\"autoNotchEnabled\":false,\"autoProfileEnabled\":false,\"visualAgcEnabled\":true,\"impulseRejectEnabled\":true,\"popFloorDb\":3,\"popSpanDb\":30,\"popGamma\":0.5,\"popRenderIntensity\":72,\"waterfallReliefDepth\":72,\"waterfallSmoothness\":56,\"coherenceHoldGate\":0.45,\"coherenceBoostDb\":4,\"ridgeBoost\":0.35,\"ridgeMaxBoostDb\":8,\"visualAgcStrength\":45,\"impulseRejectDb\":18,\"snapRadiusHz\":4000,\"snapMinSnrDb\":6,\"peakMinSnrDb\":8}")]
    [InlineData("{\"profileId\":\"balanced\",\"popEnabled\":false,\"snapEnabled\":false,\"autoNotchEnabled\":false,\"autoProfileEnabled\":false,\"visualAgcEnabled\":true,\"impulseRejectEnabled\":true,\"popFloorDb\":99,\"popSpanDb\":30,\"popGamma\":0.5,\"popRenderIntensity\":72,\"waterfallReliefDepth\":72,\"waterfallSmoothness\":56,\"coherenceHoldGate\":0.45,\"coherenceBoostDb\":4,\"ridgeBoost\":0.35,\"ridgeMaxBoostDb\":8,\"visualAgcStrength\":45,\"impulseRejectDb\":18,\"snapRadiusHz\":4000,\"snapMinSnrDb\":6,\"peakMinSnrDb\":8}")]
    [InlineData("{\"profileId\":\"balanced\",\"popEnabled\":false,\"snapEnabled\":false,\"autoNotchEnabled\":false,\"autoProfileEnabled\":false,\"visualAgcEnabled\":true,\"impulseRejectEnabled\":true,\"popFloorDb\":3,\"popSpanDb\":30,\"popGamma\":0.5,\"popRenderIntensity\":72,\"waterfallReliefDepth\":72,\"waterfallSmoothness\":56,\"coherenceHoldGate\":0.45,\"coherenceBoostDb\":4,\"ridgeBoost\":0.35,\"ridgeMaxBoostDb\":8,\"visualAgcStrength\":45,\"impulseRejectDb\":18,\"snapRadiusHz\":50,\"snapMinSnrDb\":6,\"peakMinSnrDb\":8}")]
    public async Task PutInvalidSettings_Returns400(string json)
    {
        using var factory = new Factory(_dbPath);
        using var client = factory.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await client.PutAsync("/api/dsp/display-intelligence", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed class Factory(string dbPath) : IsolatedPrefsFactory
    {
        protected override void ConfigureExtra(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DisplayIntelligenceSettingsStore>();
                services.AddSingleton(sp => new DisplayIntelligenceSettingsStore(
                    sp.GetRequiredService<ILogger<DisplayIntelligenceSettingsStore>>(),
                    dbPath));
            });
        }
    }
}
