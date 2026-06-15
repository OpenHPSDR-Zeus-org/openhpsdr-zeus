// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class PsEndpointPersistenceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-psendpoint-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task PostPsArm_RehydratesThroughStateAfterHostRestart()
    {
        using (var first = new Factory(_dbPath))
        using (var client = first.CreateClient())
        {
            var resp = await client.PostAsJsonAsync(
                "/api/tx/ps",
                new { enabled = true, auto = false, single = false });

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            Assert.True(body.RootElement.GetProperty("psEnabled").GetBoolean());
            Assert.False(body.RootElement.GetProperty("psAuto").GetBoolean());
        }

        using (var restarted = new Factory(_dbPath))
        using (var client = restarted.CreateClient())
        {
            var resp = await client.GetAsync("/api/state");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            Assert.True(body.RootElement.GetProperty("psEnabled").GetBoolean());
            Assert.False(body.RootElement.GetProperty("psAuto").GetBoolean());
        }
    }

    private sealed class Factory(string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<PsSettingsStore>();
                services.AddSingleton(sp => new PsSettingsStore(
                    sp.GetRequiredService<ILogger<PsSettingsStore>>(),
                    dbPath));
            });
        }
    }
}
