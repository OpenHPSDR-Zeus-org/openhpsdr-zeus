// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Plugins.Host;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Tests for <see cref="VstDirectoryScanService"/> — scanning a folder
/// of .vst3 files and registering each as a generated plugin package
/// (stub assembly + synthesized manifest) that activates into the
/// plugin manager. Uses fake .vst3 files: activation loads only the
/// embedded managed stub, never the (absent) native VST, so these run
/// without the native bridge.
/// </summary>
public class VstDirectoryScanServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _srcDir;
    private readonly PluginSettingsStore _store;
    private readonly PluginManager _manager;
    private readonly VstDirectoryScanService _scanner;

    public VstDirectoryScanServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zeus-vstscan-" + Guid.NewGuid().ToString("N"));
        _srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(_srcDir);

        _store = new PluginSettingsStore(Path.Combine(_root, "settings.db"));
        _manager = new PluginManager(
            loader: new PluginLoader(NullLogger<PluginLoader>.Instance),
            settings: _store,
            services: new ServiceCollection().BuildServiceProvider(),
            logFactory: NullLoggerFactory.Instance,
            options: new PluginManagerOptions { PluginRoot = Path.Combine(_root, "plugins") });

        _scanner = new VstDirectoryScanService(
            _manager,
            Path.Combine(_root, "plugins"),
            NullLogger<VstDirectoryScanService>.Instance);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    // A structurally-valid x64 PE carrying the VST3 factory export name, so it
    // passes the scanner's static loadability gate. Activation still only loads
    // the managed stub assembly — the native VST is never executed in tests.
    private static byte[] MinimalX64Vst3()
    {
        var head = new byte[0x80];
        head[0] = 0x4D; head[1] = 0x5A;                               // 'MZ'
        BitConverter.GetBytes(0x40).CopyTo(head, 0x3C);              // e_lfanew -> 0x40
        head[0x40] = 0x50; head[0x41] = 0x45;                        // 'PE'
        BitConverter.GetBytes((ushort)0x8664).CopyTo(head, 0x44);   // machine = x64
        var name = System.Text.Encoding.ASCII.GetBytes("GetPluginFactory");
        return [.. head, .. name];
    }

    private void WriteFakeVst(string fileName) =>
        File.WriteAllBytes(Path.Combine(_srcDir, fileName), MinimalX64Vst3());

    private void WriteRawVst(string fileName, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(_srcDir, fileName), bytes);

    [Fact]
    public async Task Scan_Registers_Vst_And_Activates_It()
    {
        WriteFakeVst("TDR Nova.vst3");

        var result = await _scanner.ScanAsync(_srcDir, default);

        Assert.Single(result.Registered);
        var reg = result.Registered[0];
        Assert.Equal("TDR Nova", reg.Name);
        Assert.Equal("com.openhpsdr.zeus.vst.tdrnova", reg.Id);
        Assert.Empty(result.Errors);

        // Activated into the manager.
        Assert.Contains(_manager.Active, p => p.Loaded.Manifest.Id == reg.Id);

        // Package on disk: manifest + stub assembly. The VST is referenced in
        // place (NOT copied), so no vst3/ subdir is created — stub + sidecar
        // plugins keep their dependency files at the original install path.
        var dir = Path.Combine(_root, "plugins", reg.Id);
        Assert.True(File.Exists(Path.Combine(dir, "plugin.json")));
        Assert.True(File.Exists(Path.Combine(dir, "Zeus.Plugins.VstHostStub.dll")));
        Assert.False(Directory.Exists(Path.Combine(dir, "vst3")));

        // Manifest declares the ORIGINAL absolute vst3Path + tx slot.
        using var doc = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(dir, "plugin.json")));
        var audio = doc.RootElement.GetProperty("audio");
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_srcDir, "TDR Nova.vst3")),
            audio.GetProperty("vst3Path").GetString());
        Assert.Equal("tx.post-leveler", audio.GetProperty("slot").GetString());
    }

    [Fact]
    public async Task Scan_Skips_Already_Registered_On_Second_Pass()
    {
        WriteFakeVst("Comp.vst3");

        var first = await _scanner.ScanAsync(_srcDir, default);
        Assert.Single(first.Registered);

        var second = await _scanner.ScanAsync(_srcDir, default);
        Assert.Empty(second.Registered);
        Assert.Single(second.Skipped);
        Assert.Equal("com.openhpsdr.zeus.vst.comp", second.Skipped[0].Id);
    }

    [Fact]
    public async Task Scan_Registers_Multiple_With_Distinct_Ids()
    {
        WriteFakeVst("Reverb.vst3");
        WriteFakeVst("Limiter.vst3");

        var result = await _scanner.ScanAsync(_srcDir, default);

        Assert.Equal(2, result.Registered.Count);
        Assert.Equal(2, result.Registered.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Scan_Skips_MacOs_AppleDouble_Sidecars()
    {
        // macOS resource-fork shadow files ("._Name.vst3") travel next to the
        // real bundle on cross-platform copies. They parse as .vst3 by extension
        // but are junk no host can load — must not register as dead chain plugins.
        WriteFakeVst("ReLife.vst3");
        WriteFakeVst("._ReLife.vst3");

        var result = await _scanner.ScanAsync(_srcDir, default);

        Assert.Single(result.Registered);
        Assert.Equal("com.openhpsdr.zeus.vst.relife", result.Registered[0].Id);
        Assert.DoesNotContain(result.Registered, r => r.Name.StartsWith("._"));
    }

    [Fact]
    public async Task Scan_Skips_NonPe_Junk_As_Incompatible()
    {
        // A .vst3 that isn't a Windows executable (e.g. a macOS resource fork or
        // stray data file) must not register as a dead chain plugin.
        WriteRawVst("Junk.vst3", new byte[] { 0, 5, 22, 7, 9, 9, 9, 9 });

        var result = await _scanner.ScanAsync(_srcDir, default);

        Assert.Empty(result.Registered);
        Assert.Single(result.Errors);
        Assert.Contains("incompatible", result.Errors[0].Message);
    }

    [Fact]
    public async Task Scan_Skips_32bit_Vst_As_Incompatible()
    {
        // 32-bit binary: valid PE but machine = x86; the x64 engine can't host it.
        var b = new byte[0x80];
        b[0] = 0x4D; b[1] = 0x5A;
        BitConverter.GetBytes(0x40).CopyTo(b, 0x3C);
        b[0x40] = 0x50; b[0x41] = 0x45;
        BitConverter.GetBytes((ushort)0x14C).CopyTo(b, 0x44); // x86
        WriteRawVst("Old32.vst3", b);

        var result = await _scanner.ScanAsync(_srcDir, default);

        Assert.Empty(result.Registered);
        Assert.Single(result.Errors);
        Assert.Contains("32-bit", result.Errors[0].Message);
    }

    [Fact]
    public async Task Scan_Missing_Directory_Throws()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _scanner.ScanAsync(Path.Combine(_root, "does-not-exist"), default));
    }
}
