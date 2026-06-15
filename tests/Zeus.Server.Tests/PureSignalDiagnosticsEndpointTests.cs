// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for PureSignal feedback-path diagnostics.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public sealed class PureSignalDiagnosticsEndpointTests
{
    [Fact]
    public async Task HardwareDiagnostics_ReportsExternalPureSignalFeedbackHealth()
    {
        using var factory = new Factory();

        using (var scope = factory.Services.CreateScope())
        {
            var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
            radio.MarkProtocol2Connected(
                "192.168.1.20:1024",
                192_000,
                boardKind: HpsdrBoardKind.OrionMkII);
            radio.SetPs(new PsControlSetRequest(Enabled: true, Auto: true, Single: false));
            radio.SetPsFeedbackSource(new PsFeedbackSourceSetRequest(PsFeedbackSource.External));
            radio.UpdatePsLiveReadout(feedbackLevel: 92.5, calState: 4, correcting: false);
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var ps = body.RootElement.GetProperty("pureSignal");

        Assert.Equal(1, ps.GetProperty("schemaVersion").GetInt32());
        Assert.True(ps.GetProperty("enabled").GetBoolean());
        Assert.Equal("External", ps.GetProperty("feedbackSource").GetString());
        Assert.True(ps.GetProperty("externalFeedback").GetBoolean());
        Assert.True(ps.GetProperty("externalFeedbackPathSupported").GetBoolean());
        Assert.True(ps.GetProperty("rfBypassRequired").GetBoolean());
        Assert.True(ps.GetProperty("rfBypassSelected").GetBoolean());
        Assert.Equal(92.5, ps.GetProperty("feedbackLevelRaw").GetDouble());
        Assert.Equal(152.293, ps.GetProperty("feedbackTargetRaw").GetDouble(), precision: 3);
        Assert.Equal(128, ps.GetProperty("feedbackUsableMinRaw").GetInt32());
        Assert.Equal(181, ps.GetProperty("feedbackUsableMaxRaw").GetInt32());
        Assert.Equal("feedback-low", ps.GetProperty("healthStatus").GetString());
        Assert.Contains("RF Bypass", ps.GetProperty("diagnosticRecommendation").GetString());
        Assert.Contains("no external amp ALC", ps.GetProperty("manualReference").GetString());
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesPureSignalFeedbackHealthSurface()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var tx = body.RootElement
            .GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "tx.fidelity.spectral-density");
        var telemetry = tx.GetProperty("telemetryPaths")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();
        var controls = tx.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("pureSignal.healthStatus", telemetry);
        Assert.Contains("pureSignal.rfBypassSelected", telemetry);
        Assert.Contains("/api/tx/ps/feedback-source", controls);
        Assert.Contains("/api/tx/ps/feedback-attenuation", controls);
        Assert.Contains("/api/tx/ps/monitor", controls);
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
