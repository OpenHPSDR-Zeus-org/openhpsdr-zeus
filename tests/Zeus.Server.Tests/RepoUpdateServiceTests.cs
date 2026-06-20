// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using System.Runtime.InteropServices;

namespace Zeus.Server.Tests;

public class RepoUpdateServiceTests
{
    [Theory]
    [InlineData("v0.9.1", "0.9.0", true)]
    [InlineData("v0.9.1", "0.9.1", false)]
    [InlineData("v0.9.1", "0.9.1-dev", false)]
    [InlineData("v0.10.0", "0.9.9", true)]
    [InlineData("bad", "0.9.1", false)]
    public void IsReleaseNewer_UsesNumericReleaseVersion(string latest, string installed, bool expected)
    {
        Assert.Equal(expected, RepoUpdateService.IsReleaseNewer(latest, installed));
    }

    [Theory]
    // Identical build string → nothing to do.
    [InlineData("0.9.1-main.20260620.abc1234", "0.9.1-main.20260620.abc1234", false)]
    // Same numeric prefix, different rolling-build suffix → newer build available.
    [InlineData("0.9.1-main.20260620.abc1234", "0.9.1-main.20260621.def5678", true)]
    // A plain local/dev build vs the published main build of the same prefix.
    [InlineData("0.9.1-dev", "0.9.1-main.20260621.def5678", true)]
    // Strictly newer numeric prefix.
    [InlineData("0.9.1-main.20260620.abc1234", "0.10.0-main.20260701.aaa0001", true)]
    // Local build ahead of the published line → not an update.
    [InlineData("0.10.0-main.20260701.aaa0001", "0.9.1-main.20260620.abc1234", false)]
    // Unknown installed version → offer whatever is published.
    [InlineData("unknown", "0.9.1-main.20260620.abc1234", true)]
    // Empty manifest version → never an update.
    [InlineData("0.9.1-main.20260620.abc1234", "", false)]
    public void IsManifestNewer_HandlesRollingMainBuilds(string installed, string latest, bool expected)
    {
        Assert.Equal(expected, RepoUpdateService.IsManifestNewer(installed, latest));
    }

    [Theory]
    [InlineData("windows", Architecture.X64, false, false, "openhpsdr-zeus-0.9.1-win-x64-setup.exe")]
    [InlineData("windows", Architecture.Arm64, false, false, "openhpsdr-zeus-0.9.1-win-arm64-setup.exe")]
    [InlineData("macos", Architecture.Arm64, false, false, "OpenhpsdrZeus-0.9.1-macos-arm64.dmg")]
    [InlineData("linux", Architecture.X64, false, false, "openhpsdr-zeus-0.9.1-linux-x64.tar.gz")]
    [InlineData("linux", Architecture.Arm64, false, false, "openhpsdr-zeus-0.9.1-linux-arm64.tar.gz")]
    [InlineData("linux", Architecture.X64, true, false, "OpenhpsdrZeus-0.9.1-linux-x86_64.AppImage")]
    [InlineData("linux", Architecture.X64, true, true, "OpenhpsdrZeus-Server-0.9.1-linux-x86_64.AppImage")]
    public void SelectDownloadAsset_PicksPlatformAsset(
        string platform,
        Architecture architecture,
        bool appImage,
        bool serverMode,
        string expected)
    {
        var asset = RepoUpdateService.SelectDownloadAsset(
            DownloadAssets(),
            platform,
            architecture,
            appImage,
            serverMode);

        Assert.NotNull(asset);
        Assert.Equal(expected, asset.Filename);
    }

    [Fact]
    public void SelectDownloadAsset_FallsBackToTarballWhenArm64AppImageIsMissing()
    {
        var asset = RepoUpdateService.SelectDownloadAsset(
            DownloadAssets(),
            "linux",
            Architecture.Arm64,
            runningFromAppImage: true,
            serverMode: false);

        Assert.NotNull(asset);
        Assert.Equal("openhpsdr-zeus-0.9.1-linux-arm64.tar.gz", asset.Filename);
    }

    [Fact]
    public void SelectDownloadAsset_ReturnsNullWhenPlatformMissing()
    {
        var asset = RepoUpdateService.SelectDownloadAsset(
            DownloadAssets(),
            "windows",
            Architecture.X64,
            runningFromAppImage: false,
            serverMode: false);
        Assert.NotNull(asset);

        var none = RepoUpdateService.SelectDownloadAsset(
            new List<ZeusDownloadAsset>(),
            "windows",
            Architecture.X64,
            runningFromAppImage: false,
            serverMode: false);
        Assert.Null(none);
    }

    // Mirrors the asset shape produced by tools/update-download-manifest.mjs.
    private static List<ZeusDownloadAsset> DownloadAssets() => new()
    {
        new() { Filename = "openhpsdr-zeus-0.9.1-win-x64-setup.exe", Platform = "windows", Arch = "x64", Kind = "installer" },
        new() { Filename = "openhpsdr-zeus-0.9.1-win-arm64-setup.exe", Platform = "windows", Arch = "arm64", Kind = "installer" },
        new() { Filename = "OpenhpsdrZeus-0.9.1-macos-arm64.dmg", Platform = "macos", Arch = "arm64", Kind = "dmg" },
        new() { Filename = "openhpsdr-zeus-0.9.1-linux-x64.tar.gz", Platform = "linux", Arch = "x64", Kind = "tarball" },
        new() { Filename = "openhpsdr-zeus-0.9.1-linux-arm64.tar.gz", Platform = "linux", Arch = "arm64", Kind = "tarball" },
        new() { Filename = "OpenhpsdrZeus-0.9.1-linux-x86_64.AppImage", Platform = "linux", Arch = "x64", Kind = "appimage", Mode = "desktop" },
        new() { Filename = "OpenhpsdrZeus-Server-0.9.1-linux-x86_64.AppImage", Platform = "linux", Arch = "x64", Kind = "appimage", Mode = "server" },
    };
}
