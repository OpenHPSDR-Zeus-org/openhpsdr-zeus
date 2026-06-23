// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDvNativeLoader — platform resolution + the writable managed install path
// the in-app installer stages into. These are environment-independent: the RID
// shape, the platform filename, and that the managed path sits under
// Zeus/freedv and is named for the current platform.

using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

public class FreeDvNativeLoaderTests
{
    [Fact]
    public void NativeFileName_matches_the_running_platform()
    {
        var name = FreeDvNativeLoader.NativeFileName();
        if (OperatingSystem.IsWindows()) Assert.Equal("codec2.dll", name);
        else if (OperatingSystem.IsMacOS()) Assert.Equal("libcodec2.dylib", name);
        else if (OperatingSystem.IsLinux()) Assert.Equal("libcodec2.so", name);
    }

    [Fact]
    public void CurrentRid_is_os_dash_arch()
    {
        Assert.Matches(@"^(win|linux|osx|unknown)-(x64|arm64|x86)$", FreeDvNativeLoader.CurrentRid());
    }

    [Fact]
    public void ManagedLibraryPath_sits_under_Zeus_freedv_named_for_the_platform()
    {
        var path = FreeDvNativeLoader.ManagedLibraryPath();
        Assert.NotNull(path);
        Assert.EndsWith(FreeDvNativeLoader.NativeFileName(), path);
        Assert.Contains(Path.Combine("Zeus", "freedv"), path!);
        Assert.Equal(FreeDvNativeLoader.ManagedLibraryDir(), Path.GetDirectoryName(path));
    }

    [Fact]
    public void ResetProbe_is_safe_to_call_repeatedly()
    {
        // Probe availability is environment-dependent (the lib may or may not be
        // present on the test host); the contract here is only that invalidating
        // the cached probe never throws and is idempotent.
        FreeDvNativeLoader.ResetProbe();
        FreeDvNativeLoader.ResetProbe();
    }
}
