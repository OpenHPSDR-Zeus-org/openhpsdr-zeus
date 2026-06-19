// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for read-only P2 user analog/digital I/O diagnostics.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Protocol2;

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
    public async Task DigIn_ReturnsG2TxDisableMappingBeforeTelemetry()
    {
        using var factory = new Factory();

        using (var scope = factory.Services.CreateScope())
        {
            var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
            radio.MarkProtocol2Connected(
                "192.168.1.21:1024",
                384_000,
                boardKind: HpsdrBoardKind.OrionMkII);
        }

        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/dig-in");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("OrionMkII", root.GetProperty("effectiveBoard").GetString());
        Assert.Equal("G2", root.GetProperty("orionMkIIVariant").GetString());
        Assert.Equal("userDigital1", root.GetProperty("txDisableLineId").GetString());
        Assert.Equal("User I/O IO5", root.GetProperty("txDisableLineName").GetString());
        Assert.Equal(1, root.GetProperty("txDisableBit").GetInt32());
        Assert.Equal("active-low", root.GetProperty("txDisablePolarity").GetString());
        Assert.False(root.GetProperty("txInhibitBehaviorArmed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("txDisableActive").ValueKind);
        Assert.Contains("Dig In", root.GetProperty("manualReference").GetString());
    }

    [Fact]
    public void DigIn_G2TxDisableUsesActiveLowP2UserDigitalBit1()
    {
        var inactive = HardwareDiagnosticsService.BuildDigInDiagnostics(
            activeProtocol: "P2",
            p2Attached: true,
            p2Packets: 2,
            p2LastUpdatedUtc: DateTimeOffset.UnixEpoch,
            connected: HpsdrBoardKind.OrionMkII,
            effective: HpsdrBoardKind.OrionMkII,
            variant: OrionMkIIVariant.G2,
            p2Reading: new P2TelemetryReading(
                FwdAdc: 0,
                RevAdc: 0,
                ExciterAdc: 0,
                PttIn: false,
                PllLocked: true,
                DotIn: true,
                DashIn: false,
                UserDigitalIn: 0x02));
        var active = HardwareDiagnosticsService.BuildDigInDiagnostics(
            activeProtocol: "P2",
            p2Attached: true,
            p2Packets: 3,
            p2LastUpdatedUtc: DateTimeOffset.UnixEpoch,
            connected: HpsdrBoardKind.OrionMkII,
            effective: HpsdrBoardKind.OrionMkII,
            variant: OrionMkIIVariant.G2,
            p2Reading: new P2TelemetryReading(
                FwdAdc: 0,
                RevAdc: 0,
                ExciterAdc: 0,
                PttIn: false,
                PllLocked: true,
                DotIn: false,
                DashIn: false,
                UserDigitalIn: 0x00));

        Assert.Equal("userDigital1", inactive.TxDisableLineId);
        Assert.Equal("thetis-p2-saturn-io5", inactive.TxDisableMappingStatus);
        Assert.True(inactive.TxDisableRawHigh);
        Assert.False(inactive.TxDisableActive);
        Assert.True(inactive.CwKeyTipDown);
        Assert.False(inactive.TxInhibitBehaviorArmed);
        Assert.False(active.TxDisableRawHigh);
        Assert.True(active.TxDisableActive);
        Assert.Contains("currently active", active.DiagnosticRecommendation);
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
        Assert.Contains("/api/radio/dig-in", controls);
        Assert.DoesNotContain("planned:/api/radio/user-io/labels", controls);
        Assert.DoesNotContain("planned:/api/radio/user-io/actions", controls);

        var telemetry = userIo.GetProperty("telemetryPaths")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        Assert.Contains("digIn.txDisableActive", telemetry);
        Assert.Contains("digIn.txDisableMappingStatus", telemetry);
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
    }
}
