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
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public sealed class SmartNrConditionEndpointTests
{
    [Fact]
    public void BuildSmartNrRxChainRuntime_ExposesAgcAttenuationAndAdcState()
    {
        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.0.2.10:1024",
            VfoHz: 7_262_000,
            Mode: RxMode.LSB,
            FilterLowHz: -3500,
            FilterHighHz: -100,
            SampleRate: 384_000,
            AgcTopDb: 83,
            Agc: new AgcConfig(Mode: AgcMode.Fast),
            Squelch: new SquelchConfig(Enabled: true, Level: 18, Adaptive: true),
            AttenDb: 1,
            AutoAttEnabled: true,
            AttOffsetDb: 2,
            AdcOverloadWarning: true,
            AutoAgcEnabled: true,
            AgcOffsetDb: -31,
            PreampOn: true);
        var adc = new AdcProtectionStatusDto(
            Config: new AdcProtectionConfig(Enabled: true),
            AttenDb: 1,
            OffsetDb: 2,
            EffectiveDb: 3,
            Warning: true,
            OverloadLevel: 4,
            LastOverloadBits: 0x03,
            Adc0MaxMagnitude: 44_000,
            Adc1MaxMagnitude: null,
            Adc0MaxMagnitudeAtOverload: 50_000,
            Adc1MaxMagnitudeAtOverload: 0,
            LastTelemetryUtc: DateTimeOffset.UtcNow);

        var runtime = ZeusEndpoints.BuildSmartNrRxChainRuntime(state, adc);

        Assert.Equal("backend-radio-state", runtime.Source);
        Assert.True(runtime.AutoAgcEnabled);
        Assert.Equal("Fast", runtime.AgcMode);
        Assert.Equal(52, runtime.EffectiveAgcTopDb);
        Assert.True(runtime.AutoAttEnabled);
        Assert.Equal(3, runtime.EffectiveAttenDb);
        Assert.True(runtime.AdcOverloadWarning);
        Assert.Equal(4, runtime.AdcOverloadLevel);
        Assert.Equal((ushort)44_000, runtime.Adc0MaxMagnitude);
        Assert.Null(runtime.Adc1MaxMagnitude);
        Assert.True(runtime.SquelchEnabled);
        Assert.Equal(18, runtime.SquelchLevel);
        Assert.True(runtime.PreampOn);
    }

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
        Assert.Contains(root.GetProperty("requestedNrMode").GetString(), new[] { "Off", "Anr", "Emnr", "Sbnr", "Nr5" });
        Assert.Contains(root.GetProperty("effectiveNrMode").GetString(), new[] { "Off", "Anr", "Emnr", "Sbnr", "Nr5" });
        Assert.Contains(
            root.GetProperty("nr4Readiness").GetString(),
            new[] { "available", "missing-sbnr-exports", "wdsp-native-unloadable" });
        Assert.Contains(
            root.GetProperty("nr5Readiness").GetString(),
            new[] { "available", "missing-spnr-exports", "wdsp-native-unloadable" });
        var rxChain = root.GetProperty("rxChain");
        Assert.Equal("backend-radio-state", rxChain.GetProperty("source").GetString());
        Assert.True(double.IsFinite(rxChain.GetProperty("agcTopDb").GetDouble()));
        Assert.True(double.IsFinite(rxChain.GetProperty("effectiveAgcTopDb").GetDouble()));
        Assert.True(rxChain.GetProperty("autoAttEnabled").GetBoolean());
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
            smartNrRxChainRecommendation = "Add 3-6 dB attenuation",
            smartNrRxChainTone = "protect",
            smartNrRxChainScore = 62,
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
        Assert.Equal("Add 3-6 dB attenuation", root.GetProperty("rxChainRecommendation").GetString());
        Assert.Equal("protect", root.GetProperty("rxChainTone").GetString());
        Assert.Equal(62, root.GetProperty("rxChainScore").GetInt32());
        Assert.Equal(7.1, root.GetProperty("maxSnrDb").GetDouble());
        Assert.Equal(6.8, root.GetProperty("coherentMaxSnrDb").GetDouble());
        Assert.Equal(1, root.GetProperty("peakCount").GetInt32());
        Assert.True(root.GetProperty("coherentSubthresholdSignal").GetBoolean());
        Assert.Contains(root.GetProperty("effectiveNrMode").GetString(), new[] { "Off", "Anr", "Emnr", "Sbnr", "Nr5" });
        var rxChain = root.GetProperty("rxChain");
        Assert.Equal("backend-radio-state", rxChain.GetProperty("source").GetString());
        Assert.Contains(rxChain.GetProperty("agcMode").GetString(), new[] { "Fixed", "Long", "Slow", "Med", "Fast", "Custom" });
        Assert.True(rxChain.GetProperty("adcProtectionEnabled").GetBoolean());
        Assert.Contains(rxChain.GetProperty("squelchEnabled").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Contains("constrained by RX-chain health", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task GetDspSceneAfterFrontendPost_ReturnsSceneSnapshot()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var post = await client.PostAsJsonAsync("/api/radio/diagnostics/dsp-scene", new
        {
            sourceClientId = "frontend-live",
            mode = "LSB",
            signalProfile = "weak-sparse",
            signalReason = "single coherent ridge",
            smartNrProfile = "NR2",
            smartNrRecommendation = "Keep RX headroom and use gentle NR2",
            smartNrRxChainLabel = "AGC stressed",
            smartNrRxChainRecommendation = "Auto AGC reducing AGC-T under ADC pressure",
            smartNrRxChainTone = "optimize",
            smartNrRxChainScore = 68,
            maxSnrDb = 5.64,
            coherentMaxSnrDb = 5.31,
            occupiedPct = 0.74,
            coherentOccupiedPct = 0.62,
            peakCount = 1,
            coherentPeakCount = 1,
            coherentSubthresholdSignal = true,
            sourceAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var resp = await client.GetAsync("/api/radio/diagnostics/dsp-scene");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("fresh", root.GetProperty("status").GetString());
        Assert.Equal("frontend-live", root.GetProperty("sourceClientId").GetString());
        Assert.Equal("LSB", root.GetProperty("mode").GetString());
        Assert.Equal("weak-sparse", root.GetProperty("signalProfile").GetString());
        Assert.Equal("NR2", root.GetProperty("smartNrProfile").GetString());
        Assert.Equal("AGC stressed", root.GetProperty("smartNrRxChainLabel").GetString());
        Assert.Equal("Auto AGC reducing AGC-T under ADC pressure", root.GetProperty("smartNrRxChainRecommendation").GetString());
        Assert.Equal("optimize", root.GetProperty("smartNrRxChainTone").GetString());
        Assert.Equal(68, root.GetProperty("smartNrRxChainScore").GetInt32());
        Assert.Equal(5.6, root.GetProperty("maxSnrDb").GetDouble());
        Assert.Equal(5.3, root.GetProperty("coherentMaxSnrDb").GetDouble());
        Assert.True(root.GetProperty("coherentSubthresholdSignal").GetBoolean());
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
        Assert.Contains("/api/radio/diagnostics/dsp-scene", controls);
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
