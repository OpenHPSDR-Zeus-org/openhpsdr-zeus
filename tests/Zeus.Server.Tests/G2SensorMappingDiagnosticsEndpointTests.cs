// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for ANAN G2 manual sensor mapping diagnostics.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol2;

namespace Zeus.Server.Tests;

public sealed class G2SensorMappingDiagnosticsEndpointTests
{
    [Fact]
    public async Task G2Sensors_ReturnsP2G2MappingPlanBeforeTelemetry()
    {
        using var factory = new Factory();

        using (var scope = factory.Services.CreateScope())
        {
            _ = scope.ServiceProvider.GetRequiredService<HardwareDiagnosticsService>();
            var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
            var p2 = new Protocol2Client(NullLogger<Protocol2Client>.Instance);
            radio.MarkProtocol2Connected(
                "192.168.1.25:1024",
                384_000,
                client: p2,
                boardKind: HpsdrBoardKind.OrionMkII);
        }

        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/g2-sensors");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("P2", root.GetProperty("activeProtocol").GetString());
        Assert.Equal("OrionMkII", root.GetProperty("effectiveBoard").GetString());
        Assert.Equal("G2", root.GetProperty("orionMkIIVariant").GetString());
        Assert.True(root.GetProperty("g2Class").GetBoolean());
        Assert.True(root.GetProperty("p2Attached").GetBoolean());
        Assert.Equal("waiting-for-telemetry", root.GetProperty("status").GetString());
        Assert.Equal(10, root.GetProperty("mappedSensors").GetArrayLength());
        var unmapped = root.GetProperty("unmappedManualSensors")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();
        Assert.Contains("pa-temperature", unmapped);
        Assert.Contains("pa-current", unmapped);
        Assert.Contains("driver-current", unmapped);
        Assert.Contains("fan-state", unmapped);
        Assert.Contains("biascheck", root.GetProperty("manualReference").GetString());
        Assert.Contains("hi-priority sample", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public void G2SensorMapping_BuildsLiveMappedAndUnmappedSensorInventory()
    {
        var diag = HardwareDiagnosticsService.BuildG2SensorMappingDiagnostics(
            activeProtocol: "P2",
            p2Attached: true,
            p2Packets: 12,
            p2LastUpdatedUtc: DateTimeOffset.UnixEpoch,
            connected: HpsdrBoardKind.OrionMkII,
            effective: HpsdrBoardKind.OrionMkII,
            variant: OrionMkIIVariant.G2,
            p2Reading: new P2TelemetryReading(
                FwdAdc: 91,
                RevAdc: 14,
                ExciterAdc: 64,
                PttIn: false,
                PllLocked: true,
                SupplyVoltsAdc: 1613,
                UserAdc0: 523,
                UserDigitalIn: 7,
                HardwareLeds: 0x0C00),
            candidateWords:
            [
                new G2CandidateTelemetryWordDto(
                    Offset: 0x1C,
                    HexOffset: "0x1C",
                    Known: null,
                    Last: 58_752,
                    Min: 0,
                    Max: 65_280,
                    ChangeCount: 42,
                    Status: "unknown-variable",
                    MappingHint: "capture markers")
            ]);

        Assert.Equal("manual-sensors-unmapped", diag.Status);
        Assert.Equal(12, diag.P2Packets);
        Assert.Contains(diag.MappedSensors, sensor =>
            sensor.Id == "pa-forward-power"
            && sensor.TelemetryPath == "p2.fwdAdc"
            && sensor.RawValue == 91
            && sensor.Status == "mapped-live");
        Assert.Contains(diag.MappedSensors, sensor =>
            sensor.Id == "supply-voltage"
            && sensor.RawValue == 1613);
        Assert.Contains(diag.UnmappedManualSensors, sensor =>
            sensor.Id == "pa-current"
            && sensor.CurrentTelemetryStatus == "p2-g2-pa-current-unmapped");
        Assert.Contains(diag.UnmappedManualSensors, sensor =>
            sensor.Id == "driver-current"
            && sensor.ManualEvidence.Contains("biascheck"));
        Assert.Equal("0x1C", diag.CandidateWords.Single().HexOffset);
        Assert.Contains("4 G2 manual sensor fields", diag.DiagnosticRecommendation);
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesG2SensorMappingSurface()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;
        Assert.True(root.TryGetProperty("g2Sensors", out var g2Sensors));
        Assert.Equal("not-g2", g2Sensors.GetProperty("status").GetString());

        var reference = root.GetProperty("referenceMap")
            .EnumerateArray()
            .Single(item => item.GetProperty("field").GetString() == "G2 current, bias, thermal, and fan sensors");
        Assert.Equal("mapping-required", reference.GetProperty("status").GetString());
        Assert.Contains("Driver Current / PA Current", reference.GetProperty("notes").GetString());

        var surface = root.GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "pa.g2.sensor-mapping");
        var telemetry = surface.GetProperty("telemetryPaths")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        var controls = surface.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Equal("mapping-ready", surface.GetProperty("implementationStatus").GetString());
        Assert.Contains("g2Sensors.unmappedManualSensors", telemetry);
        Assert.Contains("g2Sensors.candidateWords", telemetry);
        Assert.Contains("/api/radio/g2-sensors", controls);
        Assert.Contains("Settings > Hardware > G2 Sensor Mapping", controls);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zeus-prefs-g2-sensor-diag-{Guid.NewGuid():N}.db");
        private readonly string? _previousPrefsPath;

        public Factory()
        {
            _previousPrefsPath = Environment.GetEnvironmentVariable("ZEUS_PREFS_PATH");
            Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _dbPath);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("ZEUS_PREFS_PATH", _previousPrefsPath);
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }
    }
}
