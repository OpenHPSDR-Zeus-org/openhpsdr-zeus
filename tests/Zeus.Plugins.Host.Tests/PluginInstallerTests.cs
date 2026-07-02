using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Registry;

namespace Zeus.Plugins.Host.Tests;

public class PluginInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly PluginSettingsStore _store;
    private readonly PluginManager _manager;
    private readonly PluginInstaller _installer;
    private readonly RecordingHandler _http;

    public PluginInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zeus-installer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "settings.db");

        _store = new PluginSettingsStore(_dbPath);
        _manager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: _store,
            services: new ServiceCollection().BuildServiceProvider(),
            logFactory: NullLoggerFactory.Instance,
            options: new PluginManagerOptions { PluginRoot = _root });

        _http = new RecordingHandler();
        var httpClient = new HttpClient(_http);
        _installer = new PluginInstaller(
            httpClient,
            new StubRegistry(),
            _manager,
            _root);
    }

    public void Dispose()
    {
        foreach (var f in _addedFixtures) f.Dispose();
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private const string FixtureSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Zeus.Plugins.Contracts;

        namespace Openhpsdr.Zeus.Samples.HelloWorld;

        public sealed class HelloWorldPlugin : IZeusPlugin
        {
            public Task InitializeAsync(IPluginContext context, CancellationToken ct) => Task.CompletedTask;
            public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
        }
        """;

    private const string FixtureManifest = """
        {
          "schemaVersion": 1,
          "id": "com.openhpsdr.zeus.samples.helloworld",
          "name": "Hello World",
          "version": "1.0.0",
          "sdk": { "abi": 1, "minVersion": "1.0.0" },
          "entrypoint": { "assembly": "HelloWorld.dll", "type": "Openhpsdr.Zeus.Samples.HelloWorld.HelloWorldPlugin" }
        }
        """;

    /// <summary>Builds a self-contained plugin zip in memory by
    /// Roslyn-compiling a HelloWorld-shaped fixture and packaging its
    /// dll + plugin.json. Replaces the older "read from sample-plugins
    /// bin output" approach now that samples live in the registry
    /// repo, not the Zeus core repo.</summary>
    private (byte[] zipBytes, string sha256Hex) BuildZipFromFixture()
    {
        var fixture = RoslynFixture.Create("HelloWorld", FixtureSource, FixtureManifest);
        _addedFixtures.Add(fixture);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in Directory.EnumerateFiles(fixture.PluginDir))
            {
                var entry = archive.CreateEntry(Path.GetFileName(f), CompressionLevel.Fastest);
                using var es = entry.Open();
                using var fs = File.OpenRead(f);
                fs.CopyTo(es);
            }
        }
        var bytes = ms.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        return (bytes, sha);
    }

    private readonly List<RoslynFixture> _addedFixtures = new();

    [Fact]
    public async Task InstallFromZipFile_RoundTrips()
    {
        var (bytes, _) = BuildZipFromFixture();
        var zip = Path.Combine(_root, "in.zip");
        await File.WriteAllBytesAsync(zip, bytes);

        var installed = await _installer.InstallFromZipFileAsync(zip, default);

        Assert.Equal("com.openhpsdr.zeus.samples.helloworld", installed.Manifest.Id);
        Assert.NotNull(_manager.Find("com.openhpsdr.zeus.samples.helloworld"));
        Assert.True(Directory.Exists(installed.Directory));
        Assert.True(File.Exists(Path.Combine(installed.Directory, "plugin.json")));
    }

    [Fact]
    public async Task InstallFromUrl_VerifyHash_HappyPath()
    {
        var (bytes, sha) = BuildZipFromFixture();
        _http.Body = bytes;

        var installed = await _installer.InstallFromUrlAsync("https://example.com/plug.zip", sha, default);

        Assert.Equal("com.openhpsdr.zeus.samples.helloworld", installed.Manifest.Id);
        Assert.NotNull(_manager.Find("com.openhpsdr.zeus.samples.helloworld"));
    }

    [Fact]
    public async Task InstallFromUrl_HashMismatch_Rejects_AndLeavesNoFiles()
    {
        var (bytes, _) = BuildZipFromFixture();
        _http.Body = bytes;
        var wrongHash = new string('0', 64);

        var ex = await Assert.ThrowsAsync<PluginInstallException>(
            () => _installer.InstallFromUrlAsync("https://example.com/plug.zip", wrongHash, default));

        Assert.Contains("sha256 mismatch", ex.Message);
        Assert.Null(_manager.Find("com.openhpsdr.zeus.samples.helloworld"));
        Assert.False(Directory.Exists(
            Path.Combine(_root, PluginInstaller.SafeDirName("com.openhpsdr.zeus.samples.helloworld"))));
    }

    [Fact]
    public async Task InstallFromUrl_RejectsHttp()
    {
        var ex = await Assert.ThrowsAsync<PluginInstallException>(
            () => _installer.InstallFromUrlAsync("http://insecure.example.com/plug.zip", null, default));
        Assert.Contains("non-HTTPS", ex.Message);
    }

    [Fact]
    public async Task InstallFromZipFile_RejectsZipSlip()
    {
        var zip = Path.Combine(_root, "malicious.zip");
        using (var ms = new FileStream(zip, FileMode.Create))
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../escape.txt");
            using var s = entry.Open();
            s.Write(Encoding.UTF8.GetBytes("evil"));
        }

        await Assert.ThrowsAsync<PluginInstallException>(
            () => _installer.InstallFromZipFileAsync(zip, default));
    }

    [Fact]
    public async Task InstallFromZipFile_MissingManifest_Rejected()
    {
        var zip = Path.Combine(_root, "no-manifest.zip");
        using (var ms = new FileStream(zip, FileMode.Create))
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create))
        {
            archive.CreateEntry("README.md");
        }
        var ex = await Assert.ThrowsAsync<PluginInstallException>(
            () => _installer.InstallFromZipFileAsync(zip, default));
        Assert.Contains("plugin.json", ex.Message);
    }

    [Fact]
    public async Task Uninstall_DeactivatesAndRemovesDirectory()
    {
        var (bytes, _) = BuildZipFromFixture();
        var zip = Path.Combine(_root, "in.zip");
        await File.WriteAllBytesAsync(zip, bytes);
        var installed = await _installer.InstallFromZipFileAsync(zip, default);

        Assert.True(Directory.Exists(installed.Directory));

        // On Windows the ALC keeps the plugin DLL open until GC reclaims
        // it; UninstallAsync surfaces that as a "deferred" PluginInstallException
        // by design. The deactivation MUST succeed on every platform;
        // the dir removal is best-effort — but a deferred removal MUST leave
        // the .pending-delete marker so PluginManager.StartAsync completes
        // (not resurrects) the uninstall on the next boot.
        try
        {
            await _installer.UninstallAsync("com.openhpsdr.zeus.samples.helloworld", default);
            Assert.False(Directory.Exists(installed.Directory));
        }
        catch (PluginInstallException ex) when (ex.Message.Contains("could not be removed yet"))
        {
            // Windows-only: ALC unload latency. Test contract is just
            // that deactivation happens — which it did before the throw —
            // and that the deferred dir carries the pending-delete marker.
            Assert.True(File.Exists(
                Path.Combine(installed.Directory, PluginManager.PendingDeleteMarker)));
        }
        Assert.Null(_manager.Find("com.openhpsdr.zeus.samples.helloworld"));
    }

    [Fact]
    public void SafeDirName_NormalisesDotsAndStripsBadChars()
    {
        Assert.Equal("com.example.a", PluginInstaller.SafeDirName("com.example.a"));
        Assert.Equal("a-b_c.d", PluginInstaller.SafeDirName("a-b_c.d"));
        Assert.Equal("a_b", PluginInstaller.SafeDirName("a/b"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public byte[] Body { get; set; } = Array.Empty<byte>();
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref RequestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Body),
            });
        }
    }

    private sealed class StubRegistry : IRegistryClient
    {
        public string SourceUrl => "stub://registry";
        public Task<Zeus.Plugins.Contracts.Registry.RegistryCatalog> FetchAsync(CancellationToken ct)
            => Task.FromResult(new Zeus.Plugins.Contracts.Registry.RegistryCatalog());
    }
}
