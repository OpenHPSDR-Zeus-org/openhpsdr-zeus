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
    [InlineData("windows", Architecture.X64, false, false, "openhpsdr-zeus-0.9.1-win-x64-setup.exe")]
    [InlineData("windows", Architecture.Arm64, false, false, "openhpsdr-zeus-0.9.1-win-arm64-setup.exe")]
    [InlineData("macos", Architecture.Arm64, false, false, "OpenhpsdrZeus-0.9.1-macos-arm64.dmg")]
    [InlineData("linux", Architecture.X64, false, false, "openhpsdr-zeus-0.9.1-linux-x64.tar.gz")]
    [InlineData("linux", Architecture.Arm64, false, false, "openhpsdr-zeus-0.9.1-linux-arm64.tar.gz")]
    [InlineData("linux", Architecture.X64, true, false, "OpenhpsdrZeus-0.9.1-linux-x86_64.AppImage")]
    [InlineData("linux", Architecture.X64, true, true, "OpenhpsdrZeus-Server-0.9.1-linux-x86_64.AppImage")]
    public void SelectReleaseAsset_PicksPlatformAsset(
        string platform,
        Architecture architecture,
        bool appImage,
        bool serverMode,
        string expected)
    {
        var asset = RepoUpdateService.SelectReleaseAsset(
            ReleaseAssets(),
            platform,
            architecture,
            appImage,
            serverMode);

        Assert.NotNull(asset);
        Assert.Equal(expected, asset.Name);
    }

    [Fact]
    public void SelectReleaseAsset_FallsBackToTarballWhenArm64AppImageIsMissing()
    {
        var asset = RepoUpdateService.SelectReleaseAsset(
            ReleaseAssets(),
            "linux",
            Architecture.Arm64,
            runningFromAppImage: true,
            serverMode: false);

        Assert.NotNull(asset);
        Assert.Equal("openhpsdr-zeus-0.9.1-linux-arm64.tar.gz", asset.Name);
    }

    private static List<GitHubReleaseAsset> ReleaseAssets() => new()
    {
        new() { Name = "openhpsdr-zeus-0.9.1-linux-arm64.tar.gz" },
        new() { Name = "openhpsdr-zeus-0.9.1-linux-x64.tar.gz" },
        new() { Name = "openhpsdr-zeus-0.9.1-win-arm64-setup.exe" },
        new() { Name = "openhpsdr-zeus-0.9.1-win-x64-setup.exe" },
        new() { Name = "OpenhpsdrZeus-0.9.1-linux-x86_64.AppImage" },
        new() { Name = "OpenhpsdrZeus-0.9.1-macos-arm64.dmg" },
        new() { Name = "OpenhpsdrZeus-Server-0.9.1-linux-x86_64.AppImage" },
    };
}
