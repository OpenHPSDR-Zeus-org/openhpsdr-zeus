// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for ANAN-G2/Saturn ADC dither/random hardware options.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Zeus.Server.Tests;

public sealed class G2OptionsEndpointTests
{
    [Fact]
    public async Task G2Options_DefaultsOnButUnsupportedWithoutG2Selection()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/g2-options");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;
        Assert.True(root.GetProperty("ditherEnabled").GetBoolean());
        Assert.True(root.GetProperty("randomEnabled").GetBoolean());
        Assert.Equal(60.0, root.GetProperty("maxRxFreqMHz").GetDouble());
        Assert.False(root.GetProperty("supported").GetBoolean());
    }

    [Fact]
    public async Task G2Options_PutPersistsAndBecomesSupportedForG2Selection()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync(
            "/api/radio/g2-options",
            new { ditherEnabled = false, randomEnabled = true });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var body = await JsonDocument.ParseAsync(await put.Content.ReadAsStreamAsync()))
        {
            var root = body.RootElement;
            Assert.False(root.GetProperty("ditherEnabled").GetBoolean());
            Assert.True(root.GetProperty("randomEnabled").GetBoolean());
            Assert.False(root.GetProperty("supported").GetBoolean());
        }

        var selection = await client.PutAsJsonAsync(
            "/api/radio/selection",
            new { preferred = "OrionMkII", overrideDetection = false });

        Assert.Equal(HttpStatusCode.OK, selection.StatusCode);

        var get = await client.GetAsync("/api/radio/g2-options");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var getBody = await JsonDocument.ParseAsync(await get.Content.ReadAsStreamAsync());
        var afterSelection = getBody.RootElement;
        Assert.False(afterSelection.GetProperty("ditherEnabled").GetBoolean());
        Assert.True(afterSelection.GetProperty("randomEnabled").GetBoolean());
        Assert.Equal(60.0, afterSelection.GetProperty("maxRxFreqMHz").GetDouble());
        Assert.True(afterSelection.GetProperty("supported").GetBoolean());

        var caps = await client.GetAsync("/api/radio/capabilities");
        Assert.Equal(HttpStatusCode.OK, caps.StatusCode);
        using var capsBody = await JsonDocument.ParseAsync(await caps.Content.ReadAsStreamAsync());
        Assert.True(capsBody.RootElement.GetProperty("supportsG2AdcOptions").GetBoolean());
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"zeus-prefs-g2-options-{Guid.NewGuid():N}.db");
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
