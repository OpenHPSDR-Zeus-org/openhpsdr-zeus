using System.Runtime.InteropServices;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

public class AuBridgeNativeLoaderTests
{
    [Fact]
    public void CandidatePaths_ProbeRidNativeDirectoryBeforeExeRoot()
    {
        var paths = AuBridgeNativeLoader.CandidatePaths(typeof(AuBridgeNative).Assembly).ToArray();
        var asmDir = Path.GetDirectoryName(typeof(AuBridgeNative).Assembly.Location);

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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libzeus-au-bridge.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libzeus-au-bridge.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "zeus-au-bridge.dll";
        return "libzeus-au-bridge";
    }
}
