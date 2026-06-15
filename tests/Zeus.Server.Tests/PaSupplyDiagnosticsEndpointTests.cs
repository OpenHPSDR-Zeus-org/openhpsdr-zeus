// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for the read-only PA power and supply telemetry snapshots.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class PaSupplyDiagnosticsEndpointTests
{
    [Fact]
    public async Task PowerCalibration_ReturnsExistingMeterCalibrationAndEmptyTelemetry()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/power-calibration");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Unknown", root.GetProperty("connectedBoard").GetString());
        Assert.Equal("Unknown", root.GetProperty("effectiveBoard").GetString());
        Assert.Equal("G2", root.GetProperty("orionMkIIVariant").GetString());
        Assert.Equal(1.5, root.GetProperty("bridgeVolt").GetDouble());
        Assert.Equal(3.3, root.GetProperty("refVoltage").GetDouble());
        Assert.Equal(6, root.GetProperty("adcCalOffset").GetInt32());
        Assert.True(root.GetProperty("calibrationFallbackApplied").GetBoolean());
        Assert.Equal(100, root.GetProperty("capabilityMaxPowerWatts").GetDouble());
        Assert.Equal(0, root.GetProperty("p1").GetProperty("packets").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("p1").GetProperty("fwdWatts").ValueKind);
        Assert.Equal(0, root.GetProperty("p2").GetProperty("packets").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("p2").GetProperty("swr").ValueKind);
        Assert.Contains("No PA forward/reflected telemetry", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task SupplyAlarms_ReturnsAdvisoryStatusBeforeTelemetry()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/supply-alarms");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Unknown", root.GetProperty("effectiveBoard").GetString());
        Assert.False(root.GetProperty("supportsSupplyTelemetry").GetBoolean());
        Assert.Equal(33, root.GetProperty("adcSupplyMv").GetInt32());
        Assert.False(root.GetProperty("activeThresholdsConfigured").GetBoolean());
        Assert.False(root.GetProperty("alarmActive").GetBoolean());
        Assert.Equal("unsupported", root.GetProperty("alarmStatus").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("p1").GetProperty("supplyVolts").ValueKind);
        Assert.Equal("missing", root.GetProperty("p1").GetProperty("scaleStatus").GetString());
        Assert.Contains("does not advertise supply-voltage telemetry", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public void SupplyAlarms_FlagsLiveG2P2StaticScaleAsUnverified()
    {
        var p1 = HardwareDiagnosticsService.BuildSupplyReading(
            packets: 0,
            lastUpdatedUtc: null,
            supplyVoltsAdc: null,
            adcSupplyMv: 50,
            effectiveBoard: HpsdrBoardKind.OrionMkII,
            variant: OrionMkIIVariant.G2);
        var p2 = HardwareDiagnosticsService.BuildSupplyReading(
            packets: 25,
            lastUpdatedUtc: DateTimeOffset.UtcNow,
            supplyVoltsAdc: 1600,
            adcSupplyMv: 50,
            effectiveBoard: HpsdrBoardKind.OrionMkII,
            variant: OrionMkIIVariant.G2);
        var caps = BoardCapabilitiesTable.For(HpsdrBoardKind.OrionMkII, OrionMkIIVariant.G2);

        var result = HardwareDiagnosticsService.EvaluateSupplyTelemetry(caps, p1, p2);

        Assert.Equal(80.0, p2.RawScaledSupplyVolts);
        Assert.Null(p2.SupplyVolts);
        Assert.False(p2.SupplyVoltsTrusted);
        Assert.Equal("scale-unverified", p2.ScaleStatus);
        Assert.False(result.AlarmActive);
        Assert.Equal("scale-unverified", result.AlarmStatus);
        Assert.Contains("implausible voltage", result.Recommendation);
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesLivePaSupplyEndpoints()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var pa = body.RootElement
            .GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "pa.telemetry.power-supply");
        var controls = pa.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("/api/radio/power-calibration", controls);
        Assert.Contains("/api/radio/supply-alarms", controls);
        Assert.Contains("/api/pa-settings", controls);
        Assert.DoesNotContain("planned:/api/radio/power-calibration", controls);
        Assert.DoesNotContain("planned:/api/radio/supply-alarms", controls);
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
