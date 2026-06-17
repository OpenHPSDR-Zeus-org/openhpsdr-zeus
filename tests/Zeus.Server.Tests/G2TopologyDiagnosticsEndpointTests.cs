// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for the ANAN G2 receiver topology diagnostics surfaced
// from the manual audit.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public sealed class G2TopologyDiagnosticsEndpointTests
{
    [Fact]
    public async Task HardwareDiagnostics_ReportsG2TopologyCapabilityFacts()
    {
        using var factory = new Factory();

        using (var scope = factory.Services.CreateScope())
        {
            var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
            radio.MarkProtocol2Connected(
                "192.168.1.21:1024",
                1_536_000,
                boardKind: HpsdrBoardKind.OrionMkII);
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;
        var caps = root.GetProperty("capabilities");

        Assert.Equal("OrionMkII", root.GetProperty("effectiveBoard").GetString());
        Assert.Equal("G2", root.GetProperty("orionMkIIVariant").GetString());
        Assert.Equal(2, caps.GetProperty("rxAdcCount").GetInt32());
        Assert.True(caps.GetProperty("mkiiBpf").GetBoolean());
        Assert.True(caps.GetProperty("hasSteppedAttenuationRx2").GetBoolean());
        Assert.Equal(1_536_000, caps.GetProperty("maxRxSampleRateHz").GetInt32());

        var manualReference = root.GetProperty("referenceMap")
            .EnumerateArray()
            .Single(item => item.GetProperty("field").GetString() == "G2 manual receiver topology");
        Assert.Equal("audited", manualReference.GetProperty("status").GetString());
        Assert.Contains("10 independent DDC receivers", manualReference.GetProperty("notes").GetString());
        Assert.Contains("ADC2 ground-on-TX", manualReference.GetProperty("notes").GetString());

        var hardwarePotential = root.GetProperty("hardwarePotential");
        Assert.True(hardwarePotential.GetProperty("g2Class").GetBoolean());
        Assert.Equal(
            new[] { 48_000, 96_000, 192_000, 384_000, 768_000, 1_536_000 },
            hardwarePotential.GetProperty("sampleRates")
                .EnumerateArray()
                .Select(item => item.GetProperty("rateHz").GetInt32())
                .ToArray());
        Assert.Contains("65536", string.Join(" ", hardwarePotential.GetProperty("filterAndWindowAudit").EnumerateArray().Select(item => item.GetString())));

        var potentialItems = hardwarePotential.GetProperty("items").EnumerateArray().ToArray();
        var groundOnTx = potentialItems.Single(item => item.GetProperty("id").GetString() == "rx.adc2-ground-on-tx");
        Assert.Equal("auto-protection-wired", groundOnTx.GetProperty("implementationStatus").GetString());

        var ditherRandom = potentialItems.Single(item => item.GetProperty("id").GetString() == "rx.adc-dither-random");
        Assert.Equal("protocol-gap-read-only", ditherRandom.GetProperty("implementationStatus").GetString());

        var filterPolicy = potentialItems.Single(item => item.GetProperty("id").GetString() == "rx.filter-taps-window-sizes");
        Assert.Equal("p-invoke-gap", filterPolicy.GetProperty("implementationStatus").GetString());

        var filterGeometry = root.GetProperty("dsp").GetProperty("filterGeometry");
        Assert.True(filterGeometry.GetProperty("operatorConfigurable").GetBoolean());
        var runtimeControl = filterGeometry.GetProperty("runtimeSampleRateControl");
        Assert.Equal("max-wideband-active", runtimeControl.GetProperty("status").GetString());
        Assert.True(runtimeControl.GetProperty("writable").GetBoolean());
        Assert.False(runtimeControl.GetProperty("requiresReconnect").GetBoolean());
        Assert.Equal(1_536_000, runtimeControl.GetProperty("maxWritableSampleRateHz").GetInt32());
        Assert.True(runtimeControl.GetProperty("widebandWritable").GetBoolean());
        Assert.Equal("/api/sampleRate", runtimeControl.GetProperty("apiRoute").GetString());

        var receiverBandwidth = filterGeometry.GetProperty("receiverBandwidth");
        Assert.Equal("max-wideband-active", receiverBandwidth.GetProperty("status").GetString());
        Assert.True(receiverBandwidth.GetProperty("protocol2Active").GetBoolean());
        Assert.True(receiverBandwidth.GetProperty("widebandActive").GetBoolean());
        Assert.Equal(100.0, receiverBandwidth.GetProperty("utilizationPct").GetDouble());
        Assert.Equal(2, receiverBandwidth.GetProperty("activeUserDdcIndex").GetInt32());
        Assert.Equal(10, receiverBandwidth.GetProperty("manualReceiverCapacity").GetInt32());
        Assert.Equal(9, receiverBandwidth.GetProperty("unexposedReceiverCount").GetInt32());
        Assert.Equal(2, receiverBandwidth.GetProperty("reservedSlots").GetArrayLength());
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesG2ReceiverTopologySurface()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var surface = body.RootElement
            .GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "rx.g2.receiver-topology");
        var telemetry = surface.GetProperty("telemetryPaths")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        var controls = surface.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        var notes = surface.GetProperty("notes").GetString();

        Assert.Equal("telemetry-ready", surface.GetProperty("implementationStatus").GetString());
        Assert.Contains("capabilities.rxAdcCount", telemetry);
        Assert.Contains("capabilities.maxRxSampleRateHz", telemetry);
        Assert.Contains("p2.adc0MaxMagnitude", telemetry);
        Assert.Contains("p2.adc1MaxMagnitude", telemetry);
        Assert.Contains("dsp.filterGeometry.receiverBandwidth.utilizationPct", telemetry);
        Assert.Contains("dsp.filterGeometry.receiverBandwidth.activeUserDdcIndex", telemetry);
        Assert.Contains("dsp.filterGeometry.receiverBandwidth.unexposedReceiverCount", telemetry);
        Assert.Contains("Settings > Hardware > Receiver Topology", controls);
        Assert.Contains("10 independent DDC receivers", notes);
        Assert.Contains("Automatic ground-on-TX protection is wired", notes);
    }

    [Fact]
    public async Task HardwareDiagnostics_ReportsConservativeP2ReceiverBandwidthForHermesClass()
    {
        using var factory = new Factory();

        using (var scope = factory.Services.CreateScope())
        {
            var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
            radio.MarkProtocol2Connected(
                "192.168.1.22:1024",
                384_000,
                boardKind: HpsdrBoardKind.Hermes);
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var filterGeometry = body.RootElement.GetProperty("dsp").GetProperty("filterGeometry");
        var runtimeControl = filterGeometry.GetProperty("runtimeSampleRateControl");
        Assert.Equal("board-capability-limited", runtimeControl.GetProperty("status").GetString());
        Assert.True(runtimeControl.GetProperty("writable").GetBoolean());
        Assert.Equal(384_000, runtimeControl.GetProperty("maxWritableSampleRateHz").GetInt32());
        Assert.False(runtimeControl.GetProperty("widebandWritable").GetBoolean());

        var receiverBandwidth = filterGeometry.GetProperty("receiverBandwidth");

        Assert.Equal("board-capability-limited", receiverBandwidth.GetProperty("status").GetString());
        Assert.True(receiverBandwidth.GetProperty("protocol2Active").GetBoolean());
        Assert.False(receiverBandwidth.GetProperty("p2WidebandCapable").GetBoolean());
        Assert.Equal(384_000, receiverBandwidth.GetProperty("maxSampleRateHz").GetInt32());
        Assert.Equal(0, receiverBandwidth.GetProperty("activeUserDdcIndex").GetInt32());
        Assert.Equal(1, receiverBandwidth.GetProperty("manualReceiverCapacity").GetInt32());
        Assert.Equal(0, receiverBandwidth.GetProperty("unexposedReceiverCount").GetInt32());
    }

    [Fact]
    public async Task HardwareDiagnostics_SurfacesSecondReceiverHealth()
    {
        using var factory = new Factory();

        using (var scope = factory.Services.CreateScope())
        {
            var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
            radio.MarkProtocol2Connected(
                "192.168.1.23:1024",
                384_000,
                boardKind: HpsdrBoardKind.OrionMkII);
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var health = body.RootElement.GetProperty("dsp").GetProperty("secondReceiverHealth");

        // RX2 defaults off, so the verdict is the disabled rung — but the full
        // data shape is always present so the panel can bind without null
        // guards. (The "rx2-streaming-silence" / "rx2-healthy" rungs require a
        // live DDC and are exercised by the on-radio harness.)
        Assert.Equal(1, health.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("rx2-disabled", health.GetProperty("status").GetString());
        Assert.False(health.GetProperty("rx2Enabled").GetBoolean());

        var frames = health.GetProperty("displayFramesPerWindow");
        Assert.True(frames.TryGetProperty("rx1Panadapter", out _));
        Assert.True(frames.TryGetProperty("rx2Panadapter", out _));
        Assert.True(frames.TryGetProperty("rx2Waterfall", out _));

        var iq = health.GetProperty("iqSignal");
        Assert.True(iq.GetProperty("rx1").TryGetProperty("rms", out _));
        Assert.True(iq.GetProperty("rx2").TryGetProperty("rms", out _));

        // Per-DDC packet-rate map keyed by UDP source port (1035 + ddc).
        var ports = health.GetProperty("ddcPacketRatePerSec");
        Assert.True(ports.TryGetProperty("ddc2_port1037", out _));
        Assert.True(ports.TryGetProperty("ddc3_port1038", out _));
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
