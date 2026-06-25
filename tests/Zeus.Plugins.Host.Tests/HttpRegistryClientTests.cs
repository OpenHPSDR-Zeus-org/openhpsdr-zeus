using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Host.Registry;

namespace Zeus.Plugins.Host.Tests;

public class HttpRegistryClientTests
{
    private const string SampleCatalog = """
        {
          "schemaVersion": 1,
          "generated": "2026-05-17T00:00:00Z",
          "plugins": [
            {
              "id": "com.example.x",
              "name": "X",
              "license": "GPL-2.0-or-later",
              "versions": [
                {
                  "version": "1.0.0",
                  "sdkAbi": 1,
                  "sdkMinVersion": "1.0.0",
                  "platforms": ["any"],
                  "downloadUrl": "https://example.com/x.zip",
                  "sha256": "abc123"
                }
              ]
            }
          ]
        }
        """;

    private static HttpClient FakeClient(HttpStatusCode code, string body)
    {
        var handler = new FakeHandler(code, body);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task FetchAsync_ReturnsParsedCatalog()
    {
        var client = new HttpRegistryClient(
            FakeClient(HttpStatusCode.OK, SampleCatalog),
            new RegistryClientOptions { SourceUrl = "https://example.com/registry.json" });

        var cat = await client.FetchAsync(default);
        Assert.Equal(1, cat.SchemaVersion);
        Assert.Single(cat.Plugins);
        Assert.Equal("com.example.x", cat.Plugins[0].Id);
    }

    [Fact]
    public async Task FetchAsync_CachesUntilTtlExpires()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, SampleCatalog);
        var http = new HttpClient(handler);
        var client = new HttpRegistryClient(http,
            new RegistryClientOptions
            {
                SourceUrl = "https://example.com/registry.json",
                CacheTtl = TimeSpan.FromMinutes(5),
            });

        await client.FetchAsync(default);
        await client.FetchAsync(default);
        Assert.Equal(1, handler.RequestCount);

        client.InvalidateCache();
        await client.FetchAsync(default);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_NonOk_Throws()
    {
        var client = new HttpRegistryClient(
            FakeClient(HttpStatusCode.NotFound, ""),
            new RegistryClientOptions { SourceUrl = "https://example.com/missing.json" });

        await Assert.ThrowsAsync<RegistryFetchException>(() => client.FetchAsync(default));
    }

    [Fact]
    public async Task FetchAsync_BadJson_Throws()
    {
        var client = new HttpRegistryClient(
            FakeClient(HttpStatusCode.OK, "{ not json }"),
            new RegistryClientOptions { SourceUrl = "https://example.com/registry.json" });

        await Assert.ThrowsAsync<RegistryFetchException>(() => client.FetchAsync(default));
    }

    [Fact]
    public async Task FetchAsync_InsecureUrl_RefusedExceptForLocalhost()
    {
        var client = new HttpRegistryClient(
            FakeClient(HttpStatusCode.OK, SampleCatalog),
            new RegistryClientOptions { SourceUrl = "http://evil.example.com/registry.json" });

        var ex = await Assert.ThrowsAsync<RegistryFetchException>(() => client.FetchAsync(default));
        Assert.Contains("insecure", ex.Message);

        // localhost over http is allowed for dev / loopback registries.
        var localClient = new HttpRegistryClient(
            FakeClient(HttpStatusCode.OK, SampleCatalog),
            new RegistryClientOptions { SourceUrl = "http://localhost:8080/registry.json" });
        var cat = await localClient.FetchAsync(default);
        Assert.NotNull(cat);
    }

    [Fact]
    public void DefaultSourceUrl_PointsAtBrianbruffRegistryRepo()
    {
        Assert.Contains("OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins", HttpRegistryClient.DefaultUrl);
        Assert.StartsWith("https://", HttpRegistryClient.DefaultUrl);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _body;
        public int RequestCount;

        public FakeHandler(HttpStatusCode code, string body) { _code = code; _body = body; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
