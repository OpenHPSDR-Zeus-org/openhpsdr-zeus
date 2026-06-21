using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// RID-probing DllImport resolver for the macOS Audio Unit bridge
/// (<c>libzeus-au-bridge.dylib</c>). Mirrors <see cref="VstBridgeNativeLoader"/>:
/// it probes <c>runtimes/&lt;rid&gt;/native</c> next to the assembly and the
/// app base directory before the OS fallback. The AU dylib ships ONLY under
/// <c>runtimes/osx-{x64,arm64}/native</c>; on Windows/Linux no candidate
/// exists, the resolver returns <see cref="IntPtr.Zero"/>, and
/// <see cref="AuBridgeNative"/> degrades to clean passthrough — exactly like
/// the VST3 bridge when its native lib is absent.
/// </summary>
internal static class AuBridgeNativeLoader
{
    internal static void EnsureResolverRegistered()
    {
        // Route through the shared registrar so the AU resolver coexists with
        // the VST3 resolver — both bridges live in this one assembly, which the
        // runtime permits only a single SetDllImportResolver for. See
        // NativeBridgeResolver.
        NativeBridgeResolver.Register(Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != AuBridgeNative.LibraryName) return IntPtr.Zero;
        return TryResolve(assembly, out var handle) ? handle : IntPtr.Zero;
    }

    internal static bool TryResolve(Assembly assembly, out IntPtr handle)
    {
        foreach (var candidate in CandidatePaths(assembly))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }
        return NativeLibrary.TryLoad(AuBridgeNative.LibraryName, assembly, null, out handle);
    }

    internal static IEnumerable<string> CandidatePaths(Assembly assembly)
    {
        string rid = CurrentRid();
        string fileName = NativeFileName();
        string? asmDir = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            yield return Path.Combine(asmDir, "runtimes", rid, "native", fileName);
            yield return Path.Combine(asmDir, fileName);
        }

        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        yield return Path.Combine(baseDir, fileName);
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
        // The AU bridge is macOS-only. On other platforms we still return a
        // platform-shaped name so the probe is well-defined, but no file ever
        // exists there and the load falls through to passthrough.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libzeus-au-bridge.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libzeus-au-bridge.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "zeus-au-bridge.dll";
        return "libzeus-au-bridge";
    }
}
