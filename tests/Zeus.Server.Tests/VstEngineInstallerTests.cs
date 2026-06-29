// SPDX-License-Identifier: GPL-2.0-or-later
//
// VstEngineInstaller — the engine-staging core (no network): given an extracted
// archive tree, it must locate VSTHostEngine.exe at any depth, copy it plus its
// sibling files into the Zeus-managed dir, and reject an archive that carries no
// engine (e.g. a VSTHost build without the Zeus bridge). The asset picker must
// prefer the portable zip over the Tauri setup.exe / .msi.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class VstEngineInstallerTests : IDisposable
{
    private readonly string _root;

    public VstEngineInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"zeus-vst-installer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Stage_finds_engine_in_subfolder_and_copies_siblings()
    {
        // Mirror the portable-zip layout: VSTHostEngine.exe under an engine/ folder
        // alongside a JUCE DLL it needs at runtime.
        var extracted = Path.Combine(_root, "extracted");
        var engineDir = Path.Combine(extracted, "VSTHost-1.4.0", "engine");
        Directory.CreateDirectory(engineDir);
        File.WriteAllText(Path.Combine(engineDir, "VSTHostEngine.exe"), "MZ-stub");
        File.WriteAllText(Path.Combine(engineDir, "juce_core.dll"), "dll-stub");

        var managed = Path.Combine(_root, "managed", "vst-engine");
        var staged = VstEngineInstaller.StageFromExtractedDir(extracted, managed);

        Assert.Equal(Path.Combine(managed, "VSTHostEngine.exe"), staged);
        Assert.True(File.Exists(staged));
        // The sibling DLL must travel with the exe (flat at the managed top level).
        Assert.True(File.Exists(Path.Combine(managed, "juce_core.dll")));
    }

    [Fact]
    public void Stage_replaces_stale_managed_contents_on_reinstall()
    {
        var managed = Path.Combine(_root, "managed", "vst-engine");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "stale.dll"), "old");

        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        File.WriteAllText(Path.Combine(extracted, "VSTHostEngine.exe"), "MZ-stub");

        VstEngineInstaller.StageFromExtractedDir(extracted, managed);

        Assert.True(File.Exists(Path.Combine(managed, "VSTHostEngine.exe")));
        Assert.False(File.Exists(Path.Combine(managed, "stale.dll")));
    }

    [Fact]
    public void Stage_throws_when_archive_has_no_engine()
    {
        var extracted = Path.Combine(_root, "extracted");
        Directory.CreateDirectory(extracted);
        File.WriteAllText(Path.Combine(extracted, "vsthost.exe"), "the-tauri-app-not-the-engine");

        var managed = Path.Combine(_root, "managed", "vst-engine");
        Assert.Throws<InvalidOperationException>(
            () => VstEngineInstaller.StageFromExtractedDir(extracted, managed));
    }

    [Fact]
    public void SelectZipAsset_prefers_portable_zip_over_installers()
    {
        var assets = new List<VstEngineInstaller.GithubAsset>
        {
            new() { Name = "VSTHost_1.4.0_x64-setup.exe", DownloadUrl = "https://x/setup.exe" },
            new() { Name = "VSTHost_1.4.0_x64_en-US.msi", DownloadUrl = "https://x/app.msi" },
            new() { Name = "VSTHost-1.4.0-portable.zip", DownloadUrl = "https://x/portable.zip" },
        };

        var picked = VstEngineInstaller.SelectZipAsset(assets);

        Assert.NotNull(picked);
        Assert.Equal("VSTHost-1.4.0-portable.zip", picked!.Name);
    }

    [Fact]
    public void SelectZipAsset_returns_null_when_no_zip_present()
    {
        var assets = new List<VstEngineInstaller.GithubAsset>
        {
            new() { Name = "VSTHost_1.4.0_x64-setup.exe", DownloadUrl = "https://x/setup.exe" },
        };

        Assert.Null(VstEngineInstaller.SelectZipAsset(assets));
    }

    // --- Manifest path (the production source: a Zeus-hosted, SHA-256-pinned engine) ---

    [Fact]
    public void SelectEngineAsset_prefers_windows_x64_engine_and_skips_non_engine()
    {
        var assets = new List<VstEngineInstaller.EngineAsset>
        {
            new() { Filename = "notes.txt", Url = "https://d/notes.txt", Platform = "windows", Arch = "x64" },
            new() { Filename = "VSTHostEngine-linux.zip", Url = "https://d/lx.zip", Platform = "linux", Arch = "x64" },
            new() { Filename = "VSTHostEngine.exe", Url = "https://d/eng.exe", Platform = "windows", Arch = "x64",
                    Sha256 = "abc" },
        };

        var picked = VstEngineInstaller.SelectEngineAsset(assets);

        Assert.NotNull(picked);
        Assert.Equal("VSTHostEngine.exe", picked!.Filename);
        Assert.Equal("abc", picked.Sha256);
    }

    [Fact]
    public void SelectEngineAsset_falls_back_to_any_engine_like_asset_without_platform_tag()
    {
        var assets = new List<VstEngineInstaller.EngineAsset>
        {
            new() { Filename = "engine.zip", Url = "https://d/engine.zip" },
        };

        var picked = VstEngineInstaller.SelectEngineAsset(assets);

        Assert.NotNull(picked);
        Assert.Equal("engine.zip", picked!.Filename);
    }

    [Fact]
    public void SelectEngineAsset_returns_null_when_no_usable_asset()
    {
        Assert.Null(VstEngineInstaller.SelectEngineAsset(null));
        Assert.Null(VstEngineInstaller.SelectEngineAsset(new List<VstEngineInstaller.EngineAsset>()));
        Assert.Null(VstEngineInstaller.SelectEngineAsset(new List<VstEngineInstaller.EngineAsset>
        {
            new() { Filename = "readme.md", Url = "https://d/readme.md" }, // not exe/zip
            new() { Filename = "VSTHostEngine.exe", Url = null },          // no URL
        }));
    }

    [Fact]
    public void StageSingleExe_stages_verified_exe_and_replaces_stale_contents()
    {
        var sourceExe = Path.Combine(_root, "src", "VSTHostEngine.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceExe)!);
        File.WriteAllText(sourceExe, "ENGINE-BYTES");

        var managed = Path.Combine(_root, "managed", "vst-engine");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "stale.dll"), "old");

        var staged = VstEngineInstaller.StageSingleExe(sourceExe, managed);

        Assert.Equal(Path.Combine(managed, "VSTHostEngine.exe"), staged);
        Assert.Equal("ENGINE-BYTES", File.ReadAllText(staged));
        Assert.False(File.Exists(Path.Combine(managed, "stale.dll")));
    }
}
