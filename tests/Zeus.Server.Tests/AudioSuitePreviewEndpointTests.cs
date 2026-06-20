// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class AudioSuitePreviewEndpointTests : IClassFixture<AudioSuitePreviewEndpointTests.Factory>
{
    private readonly Factory _factory;
    public AudioSuitePreviewEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task GetReportsTxMonitorState()
    {
        using var client = _factory.CreateClient();

        using var json = await client.GetFromJsonAsync<JsonDocument>("/api/audio-suite/preview");

        Assert.NotNull(json);
        Assert.True(json!.RootElement.GetProperty("supported").GetBoolean());
        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task PutTogglesTxMonitorState()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var on = await client.PutAsJsonAsync("/api/audio-suite/preview", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(radio.Snapshot().TxMonitorEnabled);

        var off = await client.PutAsJsonAsync("/api/audio-suite/preview", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(radio.Snapshot().TxMonitorEnabled);
    }

    [Fact]
    public async Task TxPreviewAliasTogglesSameTxMonitorState()
    {
        using var scope = _factory.Services.CreateScope();
        var radio = scope.ServiceProvider.GetRequiredService<RadioService>();
        using var client = _factory.CreateClient();

        var on = await client.PutAsJsonAsync("/api/tx-audio-suite/preview", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True(radio.Snapshot().TxMonitorEnabled);

        using var json = await client.GetFromJsonAsync<JsonDocument>("/api/tx-audio-suite/preview");
        Assert.NotNull(json);
        Assert.True(json!.RootElement.GetProperty("supported").GetBoolean());
        Assert.True(json.RootElement.GetProperty("enabled").GetBoolean());

        var off = await client.PutAsJsonAsync("/api/tx-audio-suite/preview", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False(radio.Snapshot().TxMonitorEnabled);
    }

    [Fact]
    public async Task MeterOnlyReportsBackOnlyWhileEnabled()
    {
        using var client = _factory.CreateClient();

        // Auto Tune arms the monitor for metering only — the chain runs but the
        // operator hears nothing. The endpoint echoes the applied flag.
        var on = await client.PutAsJsonAsync(
            "/api/tx-audio-suite/preview", new { enabled = true, meterOnly = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        using (var onJson = await on.Content.ReadFromJsonAsync<JsonDocument>())
        {
            Assert.NotNull(onJson);
            Assert.True(onJson!.RootElement.GetProperty("enabled").GetBoolean());
            Assert.True(onJson.RootElement.GetProperty("meterOnly").GetBoolean());
        }

        // meterOnly is meaningless without the monitor; disabling reports false.
        var off = await client.PutAsJsonAsync(
            "/api/tx-audio-suite/preview", new { enabled = false, meterOnly = true });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        using var offJson = await off.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(offJson);
        Assert.False(offJson!.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(offJson.RootElement.GetProperty("meterOnly").GetBoolean());
    }

    [Fact]
    public async Task TxDiagTreatsPreviewAsStageActiveOnly()
    {
        using var client = _factory.CreateClient();

        var on = await client.PutAsJsonAsync(
            "/api/tx-audio-suite/preview", new { enabled = true, meterOnly = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        using var json = await client.GetFromJsonAsync<JsonDocument>("/api/tx/diag");
        Assert.NotNull(json);
        var root = json!.RootElement;

        Assert.True(root.GetProperty("stage").GetProperty("hostTxActive").GetBoolean());
        Assert.False(root.GetProperty("audioPath").GetProperty("hostTxActive").GetBoolean());
        Assert.False(root.GetProperty("egress").GetProperty("hostTxActive").GetBoolean());

        var off = await client.PutAsJsonAsync(
            "/api/tx-audio-suite/preview", new { enabled = false, meterOnly = true });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }

    [Fact]
    public async Task TxSuiteAliasesExposeTxRouteSurfaces()
    {
        using var client = _factory.CreateClient();

        var mode = await client.GetAsync("/api/tx-audio-suite/processing-mode");
        var order = await client.GetAsync("/api/tx-audio-suite/chain/order");
        var meters = await client.GetAsync("/api/tx-audio-suite/chain/meters");
        var rxMeters = await client.GetAsync("/api/rx-audio-suite/chain/meters");
        var txScan = await client.PostAsJsonAsync("/api/tx-audio-suite/scan-vst-directory", new { directory = "" });
        var rxScan = await client.PostAsJsonAsync("/api/rx-audio-suite/scan-vst-directory", new { directory = "" });

        Assert.Equal(HttpStatusCode.OK, mode.StatusCode);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode);
        Assert.Equal(HttpStatusCode.OK, meters.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rxMeters.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, txScan.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rxScan.StatusCode);
    }

    public sealed class Factory : IsolatedPrefsFactory
    {
    }
}
