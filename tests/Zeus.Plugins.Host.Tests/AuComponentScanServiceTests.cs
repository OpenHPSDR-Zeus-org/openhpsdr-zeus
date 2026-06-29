// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Tests for <see cref="AuComponentScanService"/> — enumerating system AU
/// effects (here a fake enumerator) and registering each as a generated
/// plugin package (stub assembly + format:"au" manifest). The scanner is
/// macOS-gated, so the registration assertions only run on macOS; the
/// off-macOS no-op is asserted unconditionally.
/// </summary>
public class AuComponentScanServiceTests : IDisposable
{
    private readonly string _root;
    private readonly PluginSettingsStore _store;
    private readonly PluginManager _manager;

    public AuComponentScanServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zeus-auscan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new PluginSettingsStore(Path.Combine(_root, "settings.db"));
        _manager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: _store,
            services: new ServiceCollection().BuildServiceProvider(),
            logFactory: NullLoggerFactory.Instance,
            options: new PluginManagerOptions { PluginRoot = Path.Combine(_root, "plugins") });
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private AuComponentScanService MakeScanner(params AuBridgeNative.AuEffect[] effects) =>
        new(_manager, Path.Combine(_root, "plugins"),
            NullLogger<AuComponentScanService>.Instance,
            enumerate: () => effects);

    [SkippableFact]
    public async Task Scan_Registers_Au_And_Activates_It()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "AU scanner is macOS-only.");
        var scanner = MakeScanner(new AuBridgeNative.AuEffect("aufx:lpas:appl", "AULowpass", "Apple"));

        var result = await scanner.ScanAsync(route: "tx", default);

        Assert.Single(result.Registered);
        var reg = result.Registered[0];
        Assert.StartsWith("com.openhpsdr.zeus.au.", reg.Id);
        Assert.Equal("aufx:lpas:appl", reg.ComponentId);

        // The activated plugin's manifest carries the AU format + identity.
        var active = _manager.Active.Single(p => p.Loaded.Manifest.Id == reg.Id);
        Assert.Equal("au", active.Loaded.Manifest.Audio!.Format);
        Assert.Equal("aufx:lpas:appl", active.Loaded.Manifest.Audio!.AuComponentId);
    }

    [SkippableFact]
    public async Task Scan_RxRoute_UsesRxPrefix()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "AU scanner is macOS-only.");
        var scanner = MakeScanner(new AuBridgeNative.AuEffect("aufx:dely:appl", "AUDelay", "Apple"));

        var result = await scanner.ScanAsync(route: "rx", default);

        Assert.Single(result.Registered);
        Assert.StartsWith("com.openhpsdr.zeus.rxau.", result.Registered[0].Id);
        Assert.True(AuComponentScanService.IsRxPluginId(result.Registered[0].Id));
    }

    [Fact]
    public async Task Scan_OffMacOS_IsNoOp()
    {
        // The service must return an empty result without touching the
        // enumerator off macOS. We can only positively assert the no-op when
        // actually off macOS; on macOS we just assert it doesn't throw.
        var enumeratorCalled = false;
        var scanner = new AuComponentScanService(
            _manager, Path.Combine(_root, "plugins"),
            NullLogger<AuComponentScanService>.Instance,
            enumerate: () => { enumeratorCalled = true; return []; });

        var result = await scanner.ScanAsync(route: "tx", default);

        if (!OperatingSystem.IsMacOS())
        {
            Assert.Empty(result.Registered);
            Assert.False(enumeratorCalled);
        }
    }
}
