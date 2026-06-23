// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDvNativeInstaller — the URL/path mapping core (no network). The installer
// fetches the prebuilt codec2 binary Zeus committed for the running platform
// from the repo's runtimes/ tree; BuildLibraryUrl must compose that raw-GitHub
// path correctly for every shipped RID and tolerate a trailing slash on the base.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class FreeDvNativeInstallerTests
{
    [Fact]
    public void BuildLibraryUrl_composes_repo_raw_path()
    {
        var url = FreeDvNativeInstaller.BuildLibraryUrl(
            "https://raw.githubusercontent.com/OpenHPSDR-Zeus-org/openhpsdr-zeus",
            "develop",
            "win-x64",
            "codec2.dll");

        Assert.Equal(
            "https://raw.githubusercontent.com/OpenHPSDR-Zeus-org/openhpsdr-zeus/develop/Zeus.Dsp/runtimes/win-x64/native/codec2.dll",
            url);
    }

    [Theory]
    [InlineData("win-x64", "codec2.dll")]
    [InlineData("linux-x64", "libcodec2.so")]
    [InlineData("linux-arm64", "libcodec2.so")]
    [InlineData("osx-arm64", "libcodec2.dylib")]
    public void BuildLibraryUrl_maps_each_shipped_platform(string rid, string file)
    {
        var url = FreeDvNativeInstaller.BuildLibraryUrl("https://host/repo", "main", rid, file);
        Assert.Equal($"https://host/repo/main/Zeus.Dsp/runtimes/{rid}/native/{file}", url);
    }

    [Fact]
    public void BuildLibraryUrl_trims_a_trailing_slash_on_the_base()
    {
        var url = FreeDvNativeInstaller.BuildLibraryUrl(
            "https://host/repo/", "develop", "win-x64", "codec2.dll");
        Assert.Equal("https://host/repo/develop/Zeus.Dsp/runtimes/win-x64/native/codec2.dll", url);
    }
}
