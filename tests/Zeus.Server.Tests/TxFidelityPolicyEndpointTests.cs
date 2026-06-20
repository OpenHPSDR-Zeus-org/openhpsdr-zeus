// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for the active TX fidelity policy. These routes make the
// existing TX advisor/profile target durable without duplicating profile data.

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

public sealed class TxFidelityPolicyEndpointTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-txpolicy-endpoint-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task GetFirstRun_ReturnsDefaultPolicy()
    {
        using var factory = new Factory(_dbPath);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/tx/fidelity-policy");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        Assert.Equal("studio-ssb", body.RootElement.GetProperty("profileId").GetString());
        Assert.Equal(55, body.RootElement.GetProperty("targetSpectralDensity").GetInt32());
    }

    [Fact]
    public async Task PutValidPolicy_PersistsAfterHostRestart()
    {
        using (var first = new Factory(_dbPath))
        using (var client = first.CreateClient())
        {
            var resp = await client.PutAsJsonAsync(
                "/api/tx/fidelity-policy",
                new { profileId = "DX", targetSpectralDensity = 100 });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            Assert.Equal("dx", body.RootElement.GetProperty("profileId").GetString());
            Assert.Equal(100, body.RootElement.GetProperty("targetSpectralDensity").GetInt32());
        }

        using (var restarted = new Factory(_dbPath))
        using (var client = restarted.CreateClient())
        {
            var resp = await client.GetAsync("/api/tx/fidelity-policy");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            Assert.Equal("dx", body.RootElement.GetProperty("profileId").GetString());
            Assert.Equal(100, body.RootElement.GetProperty("targetSpectralDensity").GetInt32());
        }
    }

    [Theory]
    [InlineData("{\"profileId\":\"contest\",\"targetSpectralDensity\":55}")]
    [InlineData("{\"profileId\":\"dx\",\"targetSpectralDensity\":101}")]
    [InlineData("{\"profileId\":\"dx\",\"targetSpectralDensity\":-1}")]
    public async Task PutInvalidPolicy_Returns400(string json)
    {
        using var factory = new Factory(_dbPath);
        using var client = factory.CreateClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await client.PutAsync("/api/tx/fidelity-policy", content);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed class Factory(string dbPath) : IsolatedPrefsFactory
    {
        protected override void ConfigureExtra(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TxFidelityPolicyStore>();
                services.AddSingleton(sp => new TxFidelityPolicyStore(
                    sp.GetRequiredService<ILogger<TxFidelityPolicyStore>>(),
                    dbPath));
            });
        }
    }
}
