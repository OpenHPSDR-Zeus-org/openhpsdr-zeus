// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for the read-only hardware keying/PTT diagnostics.

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;

namespace Zeus.Server.Tests;

public sealed class HardwareKeyingEndpointTests
{
    [Fact]
    public async Task ExternalPtt_ReturnsIdleStatusBeforeProtocol1ClientAttaches()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/tx/external-ptt");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.False(root.GetProperty("available").GetBoolean());
        Assert.Equal("none", root.GetProperty("protocol").GetString());
        Assert.False(root.GetProperty("ownedMox").GetBoolean());
        Assert.Equal(250, root.GetProperty("hangTimeMs").GetInt32());
        Assert.False(root.GetProperty("moxOn").GetBoolean());
        Assert.Contains("idle", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task ExternalPtt_ReportsHardwareMoxOwner()
    {
        using var factory = new Factory();

        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var radio = services.GetRequiredService<RadioService>();
            var tx = services.GetRequiredService<TxService>();
            radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);

            Assert.True(tx.TrySetMox(true, MoxSource.Hardware, out var err), err);
        }

        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/tx/external-ptt");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.True(root.GetProperty("moxOn").GetBoolean());
        Assert.Equal("Hardware", root.GetProperty("moxOwner").GetString());
        Assert.False(root.GetProperty("ownedMox").GetBoolean());
    }

    [Fact]
    public async Task HardwareKeying_ReturnsDecodedTelemetryShape()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/cw/hardware-keying");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, root.GetProperty("p1Packets").GetInt64());
        Assert.Equal(0, root.GetProperty("p2Packets").GetInt64());
        Assert.False(root.GetProperty("externalPtt").GetProperty("available").GetBoolean());
        Assert.Contains("No hardware keying telemetry", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public async Task HardwareDiagnostics_AdvertisesLiveKeyingEndpoints()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/diagnostics");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var keying = body.RootElement
            .GetProperty("featureSurfaces")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "cw.hardware-keying");
        var controls = keying.GetProperty("candidateControls")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("/api/cw/hardware-keying", controls);
        Assert.Contains("/api/tx/external-ptt", controls);
        Assert.DoesNotContain("planned:/api/cw/hardware-keying", controls);
        Assert.DoesNotContain("planned:/api/tx/external-ptt", controls);
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
    }
}
