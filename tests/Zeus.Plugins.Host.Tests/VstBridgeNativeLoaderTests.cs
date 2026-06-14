using System.Runtime.InteropServices;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

public class VstBridgeNativeLoaderTests
{
    [Fact]
    public void CandidatePaths_ProbeRidNativeDirectoryBeforeExeRoot()
    {
        var paths = VstBridgeNativeLoader.CandidatePaths(typeof(VstBridgeNative).Assembly).ToArray();
        var asmDir = Path.GetDirectoryName(typeof(VstBridgeNative).Assembly.Location);

        Assert.False(string.IsNullOrWhiteSpace(asmDir));
        Assert.Equal(
            Path.Combine(asmDir!, "runtimes", CurrentRid(), "native", NativeFileName()),
            paths[0]);
        Assert.Equal(Path.Combine(asmDir!, NativeFileName()), paths[1]);
    }

    private static string CurrentRid()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        return $"unknown-{arch}";
    }

    private static string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libzeus-vst-bridge.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libzeus-vst-bridge.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "zeus-vst-bridge.dll";
        return "libzeus-vst-bridge";
    }
}
