// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// PluginManager tests load the HelloWorld sample plugin (built and
// copied into testdata/HelloWorld/ via the .csproj target) into a temp
// directory and exercise the loader the same way Zeus does at boot.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Plugins;

namespace Zeus.Server.Tests.Plugins;

public sealed class PluginManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    public PluginManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"zeus-plugin-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private PluginManager CreateManager(bool disabled = false, string? overrideDir = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Plugins:Directory"] = overrideDir ?? _tempDir,
        };
        if (disabled) dict["Plugins:Disabled"] = "true";
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        return new PluginManager(NullLogger<PluginManager>.Instance, _loggerFactory, config);
    }

    private static string TestDataRoot
    {
        get
        {
            // Built-output path of HelloWorld inside the test assembly's bin
            // dir, copied by the CopyHelloWorldPluginForTests msbuild target.
            var dir = Path.Combine(AppContext.BaseDirectory, "testdata", "HelloWorld");
            Assert.True(Directory.Exists(dir),
                $"testdata/HelloWorld not found at {dir} — was the CopyHelloWorldPluginForTests msbuild target skipped?");
            return dir;
        }
    }

    private void InstallHelloWorldUnder(string pluginSubdir)
    {
        var dest = Path.Combine(_tempDir, pluginSubdir);
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(TestDataRoot))
        {
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        }
    }

    [Fact]
    public async Task NoPluginDirectory_DoesNotThrow_LoadsNothing()
    {
        // Point at a path that doesn't exist yet — StartAsync should create it.
        var nonexistent = Path.Combine(_tempDir, "does-not-exist-yet");
        var mgr = CreateManager(overrideDir: nonexistent);
        await mgr.StartAsync(default);
        Assert.Empty(mgr.Plugins);
    }

    [Fact]
    public async Task EmptyPluginDirectory_LoadsNothing()
    {
        Directory.CreateDirectory(_tempDir);
        var mgr = CreateManager();
        await mgr.StartAsync(default);
        Assert.Empty(mgr.Plugins);
    }

    [Fact]
    public async Task ValidHelloWorldPlugin_Loads_AndInstanceIsLive()
    {
        InstallHelloWorldUnder("HelloWorld");
        var mgr = CreateManager();
        await mgr.StartAsync(default);

        Assert.Single(mgr.Plugins);
        var loaded = mgr.Plugins[0];
        Assert.Equal("com.openhpsdr.zeus.helloworld", loaded.Manifest.Id);
        Assert.NotNull(loaded.Instance);
        Assert.Null(loaded.LoadError);
        Assert.Equal("Hello World", loaded.Instance!.Metadata.Name);

        await mgr.StopAsync(default);
    }

    [Fact]
    public async Task MalformedManifest_IsRecordedAsErrorAndDoesNotCrash()
    {
        var dir = Path.Combine(_tempDir, "Broken");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"), "{ this is not valid json");

        var mgr = CreateManager();
        await mgr.StartAsync(default);

        Assert.Single(mgr.Plugins);
        var loaded = mgr.Plugins[0];
        Assert.Null(loaded.Instance);
        Assert.NotNull(loaded.LoadError);
    }

    [Fact]
    public async Task ManifestForMissingAssembly_IsRecordedAsError()
    {
        var dir = Path.Combine(_tempDir, "MissingDll");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"), """
        {
          "id": "com.example.missing",
          "name": "Missing",
          "version": "1.0.0",
          "author": "test",
          "description": "test",
          "homepageUrl": null,
          "assembly": "MissingPlugin.dll",
          "capabilities": [],
          "resourceLimits": null,
          "isolation": "inprocess"
        }
        """);

        var mgr = CreateManager();
        await mgr.StartAsync(default);

        Assert.Single(mgr.Plugins);
        Assert.Null(mgr.Plugins[0].Instance);
        Assert.NotNull(mgr.Plugins[0].LoadError);
    }

    [Fact]
    public async Task ManifestIdMismatch_IsRejected()
    {
        // Copy HelloWorld but with a manifest claiming a different Id —
        // the loader must refuse it because the assembly's Metadata.Id
        // disagrees (potential capability-elevation attempt).
        var dir = Path.Combine(_tempDir, "Spoof");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(TestDataRoot, "HelloWorld.dll"), Path.Combine(dir, "HelloWorld.dll"));
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"), """
        {
          "id": "com.attacker.elevated",
          "name": "Spoof",
          "version": "1.0.0",
          "author": "test",
          "description": "test",
          "homepageUrl": null,
          "assembly": "HelloWorld.dll",
          "capabilities": ["ControlRadio"],
          "resourceLimits": null,
          "isolation": "inprocess"
        }
        """);

        var mgr = CreateManager();
        await mgr.StartAsync(default);

        Assert.Single(mgr.Plugins);
        Assert.Null(mgr.Plugins[0].Instance);
        Assert.NotNull(mgr.Plugins[0].LoadError);
        Assert.Contains("does not match", mgr.Plugins[0].LoadError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafeMode_SkipsDiscovery_EvenWhenPluginsExist()
    {
        InstallHelloWorldUnder("HelloWorld");
        var mgr = CreateManager(disabled: true);
        await mgr.StartAsync(default);
        Assert.Empty(mgr.Plugins);
    }

    [Fact]
    public async Task UnsupportedIsolation_IsRejected()
    {
        var dir = Path.Combine(_tempDir, "Process");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(TestDataRoot, "HelloWorld.dll"), Path.Combine(dir, "HelloWorld.dll"));
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"), """
        {
          "id": "com.openhpsdr.zeus.helloworld",
          "name": "HW",
          "version": "1.0.0",
          "author": "t",
          "description": "t",
          "homepageUrl": null,
          "assembly": "HelloWorld.dll",
          "capabilities": [],
          "resourceLimits": null,
          "isolation": "process"
        }
        """);

        var mgr = CreateManager();
        await mgr.StartAsync(default);

        Assert.Single(mgr.Plugins);
        Assert.Null(mgr.Plugins[0].Instance);
        Assert.NotNull(mgr.Plugins[0].LoadError);
        Assert.Contains("isolation", mgr.Plugins[0].LoadError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EscapingAssemblyPath_IsRejected()
    {
        var dir = Path.Combine(_tempDir, "Escape");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"), """
        {
          "id": "com.openhpsdr.zeus.helloworld",
          "name": "HW",
          "version": "1.0.0",
          "author": "t",
          "description": "t",
          "homepageUrl": null,
          "assembly": "../../etc/passwd.dll",
          "capabilities": [],
          "resourceLimits": null,
          "isolation": "inprocess"
        }
        """);

        var mgr = CreateManager();
        await mgr.StartAsync(default);

        Assert.Single(mgr.Plugins);
        Assert.Null(mgr.Plugins[0].Instance);
        Assert.NotNull(mgr.Plugins[0].LoadError);
    }

    [Fact]
    public async Task MultiplePlugins_OneFailingDoesNotBlockOthers()
    {
        InstallHelloWorldUnder("Good");
        var brokenDir = Path.Combine(_tempDir, "Broken");
        Directory.CreateDirectory(brokenDir);
        await File.WriteAllTextAsync(Path.Combine(brokenDir, "plugin.json"), "garbage");

        var mgr = CreateManager();
        await mgr.StartAsync(default);

        Assert.Equal(2, mgr.Plugins.Count);
        Assert.Single(mgr.Plugins, p => p.Instance != null);
        Assert.Single(mgr.Plugins, p => p.LoadError != null);
    }
}
