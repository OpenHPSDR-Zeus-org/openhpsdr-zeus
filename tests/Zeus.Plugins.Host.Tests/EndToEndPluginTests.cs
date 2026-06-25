using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Registry;
using DotnetHost = Microsoft.Extensions.Hosting.Host;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// End-to-end: a plugin author can ship a class library + plugin.json
/// and have it appear under <c>/api/plugins</c> with a working backend.
///
/// Reference implementations live in the separate registry repo at
/// <c>OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins/samples/</c>. These tests build
/// shape-equivalent fixtures via Roslyn so the integration is
/// exercised without pulling the registry repo into Zeus's reference
/// graph.
/// </summary>
public class EndToEndPluginTests : IDisposable
{
    private readonly string _root;
    private readonly List<RoslynFixture> _fixtures = new();

    public EndToEndPluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            "zeus-e2e-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private string InstallFixture(RoslynFixture fixture, string installDirName)
    {
        _fixtures.Add(fixture);
        var dst = Path.Combine(_root, installDirName);
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(fixture.PluginDir))
        {
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        return dst;
    }

    /// <summary>Source equivalent to the HelloWorld sample's Plugin.cs.</summary>
    private const string HelloWorldSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Zeus.Plugins.Contracts;

        namespace Openhpsdr.Zeus.Samples.HelloWorld;

        public sealed class HelloWorldPlugin : IZeusPlugin
        {
            public Task InitializeAsync(IPluginContext context, CancellationToken ct)
                => Task.CompletedTask;
            public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
        }
        """;

    private const string HelloWorldManifest = """
        {
          "schemaVersion": 1,
          "id": "com.openhpsdr.zeus.samples.helloworld",
          "name": "Hello World",
          "version": "1.0.0",
          "sdk": { "abi": 1, "minVersion": "1.0.0" },
          "entrypoint": { "assembly": "HelloWorld.dll", "type": "Openhpsdr.Zeus.Samples.HelloWorld.HelloWorldPlugin" }
        }
        """;

    /// <summary>Source equivalent to the Amplifier sample's AmplifierPlugin.cs.
    /// Pared down to the contract surface needed by the endpoint round-trip.</summary>
    private const string AmplifierSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Http;
        using Microsoft.AspNetCore.Routing;
        using Zeus.Plugins.Contracts;
        using Zeus.Plugins.Contracts.Extensions;

        namespace Openhpsdr.Zeus.Samples.Amplifier;

        public sealed class AmplifierPlugin : IZeusPlugin, IBackendPlugin
        {
            private int _powerWatts;

            public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;
            public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;

            public void MapEndpoints(IEndpointRouteBuilder endpoints)
            {
                endpoints.MapGet("status", () => Results.Ok(new { powerWatts = _powerWatts, swr = 1.1, fault = (string?)null }));
                endpoints.MapPost("power", (SetPowerRequest req) =>
                {
                    if (req.Watts < 0 || req.Watts > 2000) return Results.BadRequest();
                    _powerWatts = req.Watts;
                    return Results.NoContent();
                });
            }

            public record SetPowerRequest(int Watts);
        }
        """;

    private const string AmplifierManifest = """
        {
          "schemaVersion": 1,
          "id": "com.openhpsdr.zeus.samples.amplifier",
          "name": "Amplifier Control (sample)",
          "version": "1.0.0",
          "sdk": { "abi": 1, "minVersion": "1.0.0" },
          "entrypoint": { "assembly": "Amplifier.dll", "type": "Openhpsdr.Zeus.Samples.Amplifier.AmplifierPlugin" }
        }
        """;

    [Fact]
    public async Task HelloWorld_Loads_And_Reports_Manifest()
    {
        var dir = InstallFixture(
            RoslynFixture.Create("HelloWorld", HelloWorldSource, HelloWorldManifest),
            "helloworld");

        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        using var store = new PluginSettingsStore(Path.Combine(_root, "settings.db"));
        var manager = new PluginManager(loader, store, new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);

        var activated = await manager.ActivateAsync(dir, default);

        Assert.Equal("com.openhpsdr.zeus.samples.helloworld", activated.Loaded.Manifest.Id);
        Assert.Equal("1.0.0", activated.Loaded.Manifest.Version);
        Assert.Equal("Hello World", activated.Loaded.Manifest.Name);

        await manager.StopAsync(default);
        store.Dispose();
    }

    [Fact]
    public async Task AmplifierLike_BackendPlugin_RoundTrips_Via_TestServer()
    {
        // Force-load the AspNetCore routing / builder assemblies into
        // AppDomain so RoslynFixture's MetadataReference scan finds them
        // when compiling a fixture that implements IBackendPlugin. The
        // test project references Microsoft.AspNetCore.Mvc.Testing, but
        // some sub-assemblies only load on first use.
        _ = typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder);
        _ = typeof(Microsoft.AspNetCore.Http.Results);
        _ = typeof(Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions);

        var dir = InstallFixture(
            RoslynFixture.Create("Amplifier", AmplifierSource, AmplifierManifest),
            "amplifier");

        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        using var store = new PluginSettingsStore(Path.Combine(_root, "settings.db"));
        var manager = new PluginManager(loader, store, new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);
        await manager.ActivateAsync(dir, default);

        using var host = await BuildTestServerAsync(manager);
        var client = host.GetTestClient();

        var status = await client.GetFromJsonAsync<AmpStatus>(
            "/api/plugins/com.openhpsdr.zeus.samples.amplifier/status");
        Assert.NotNull(status);
        Assert.Equal(0, status!.PowerWatts);

        var set = await client.PostAsJsonAsync(
            "/api/plugins/com.openhpsdr.zeus.samples.amplifier/power",
            new { watts = 750 });
        Assert.True(set.IsSuccessStatusCode);

        status = await client.GetFromJsonAsync<AmpStatus>(
            "/api/plugins/com.openhpsdr.zeus.samples.amplifier/status");
        Assert.Equal(750, status!.PowerWatts);

        await manager.StopAsync(default);
        store.Dispose();
    }

    [Fact]
    public async Task GetApiPlugins_Lists_All_Activated()
    {
        _ = typeof(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder);
        _ = typeof(Microsoft.AspNetCore.Http.Results);
        _ = typeof(Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions);

        var hwDir = InstallFixture(
            RoslynFixture.Create("HelloWorld", HelloWorldSource, HelloWorldManifest),
            "helloworld");
        var ampDir = InstallFixture(
            RoslynFixture.Create("Amplifier", AmplifierSource, AmplifierManifest),
            "amplifier");

        var loader = new PluginLoader(NullLogger<PluginLoader>.Instance);
        using var store = new PluginSettingsStore(Path.Combine(_root, "settings.db"));
        var manager = new PluginManager(loader, store, new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance);
        await manager.ActivateAsync(hwDir, default);
        await manager.ActivateAsync(ampDir, default);

        using var host = await BuildTestServerAsync(manager);
        var client = host.GetTestClient();

        var resp = await client.GetFromJsonAsync<PluginListResponse>("/api/plugins");
        Assert.NotNull(resp);
        Assert.Equal(Zeus.Plugins.Contracts.AbiVersion.Current, resp!.SdkAbi);
        Assert.Equal(2, resp.Plugins.Count);

        await manager.StopAsync(default);
        store.Dispose();
    }

    private static async Task<IHost> BuildTestServerAsync(PluginManager manager)
    {
        var builder = DotnetHost.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    // Stubs satisfy parameter resolution on the registry +
                    // install endpoints; those endpoints have dedicated unit
                    // tests in HttpRegistryClientTests / PluginInstallerTests.
                    s.AddSingleton<IRegistryClient>(new StubRegistry());
                    s.AddSingleton(sp => new PluginInstaller(
                        http: new HttpClient(),
                        registry: sp.GetRequiredService<IRegistryClient>(),
                        manager: manager,
                        pluginRoot: Path.GetTempPath()));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        PluginEndpoints.MapAll(endpoints, manager);
                    });
                });
            });

        var host = await builder.StartAsync();
        return host;
    }

    private sealed class StubRegistry : IRegistryClient
    {
        public string SourceUrl => "stub://test";
        public Task<Zeus.Plugins.Contracts.Registry.RegistryCatalog> FetchAsync(CancellationToken ct)
            => Task.FromResult(new Zeus.Plugins.Contracts.Registry.RegistryCatalog());
    }

    private sealed record AmpStatus
    {
        public int PowerWatts { get; init; }
        public double Swr { get; init; }
        public string? Fault { get; init; }
    }
}
