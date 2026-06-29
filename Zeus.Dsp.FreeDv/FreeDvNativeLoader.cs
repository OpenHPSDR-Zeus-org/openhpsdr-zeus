// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Resolver for the codec2/FreeDV shared library, mirroring WdspNativeLoader.
// Lives in its own assembly (Zeus.Dsp.FreeDv) so it can own this assembly's
// single NativeLibrary.SetDllImportResolver registration without colliding
// with the WDSP resolver in Zeus.Dsp. The codec2 binary ships under
// Zeus.Dsp/runtimes/{rid}/native and lands in the shared output directory, so
// both the assembly-relative and BaseDirectory probes find it at runtime.
//
// It is ALSO resolvable from a writable per-user "managed" directory
// (LocalApplicationData/Zeus/freedv) that the in-app FreeDV installer
// (FreeDvNativeInstaller) stages into when the bundled binary is missing — e.g.
// an older build that predates the committed lib, or a platform whose binary
// wasn't shipped. The managed path is probed FIRST so a freshly-installed lib
// wins, and TryProbe()'s cached result can be invalidated via ResetProbe() so
// an install takes effect without restarting the host.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.FreeDv;

public static class FreeDvNativeLoader
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static bool _probedLoadable;
    private static bool _loadable;
    private static bool _radeProbed;
    private static bool _radeLoadable;

    internal static void EnsureResolverRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(typeof(FreeDvNativeMethods).Assembly, Resolve);
            _registered = true;
        }
    }

    /// <summary>True if the codec2 shared library can be located and loaded.</summary>
    internal static bool TryProbe()
    {
        EnsureResolverRegistered();
        if (_probedLoadable) return _loadable;
        lock (Gate)
        {
            if (_probedLoadable) return _loadable;
            if (TryResolve(typeof(FreeDvNativeMethods).Assembly, out var handle))
            {
                NativeLibrary.Free(handle);
                _loadable = true;
            }
            else
            {
                _loadable = false;
            }
            _probedLoadable = true;
            return _loadable;
        }
    }

    /// <summary>True if the zeus_rade shim shared library can be located and loaded.</summary>
    internal static bool TryProbeRade()
    {
        EnsureResolverRegistered();
        if (_radeProbed) return _radeLoadable;
        lock (Gate)
        {
            if (_radeProbed) return _radeLoadable;
            if (TryResolveRade(typeof(RadeNativeMethods).Assembly, out var handle))
            {
                NativeLibrary.Free(handle);
                _radeLoadable = true;
            }
            else
            {
                _radeLoadable = false;
            }
            _radeProbed = true;
            return _radeLoadable;
        }
    }

    /// <summary>
    /// Drop the cached <see cref="TryProbe"/> and <see cref="TryProbeRade"/>
    /// results so the next probe re-scans the candidate paths. Called after the
    /// in-app installer stages a new codec2 / zeus_rade binary so FreeDV / RADE
    /// can go live without restarting the host.
    /// </summary>
    public static void ResetProbe()
    {
        lock (Gate)
        {
            _probedLoadable = false;
            _loadable = false;
            _radeProbed = false;
            _radeLoadable = false;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == FreeDvNativeMethods.LibraryName)
            return TryResolve(assembly, out var handle) ? handle : IntPtr.Zero;
        if (libraryName == RadeNativeMethods.LibraryName)
            return TryResolveRade(assembly, out var radeHandle) ? radeHandle : IntPtr.Zero;
        return IntPtr.Zero;
    }

    private static bool TryResolve(Assembly assembly, out IntPtr handle)
    {
        foreach (var candidate in CandidatePaths(assembly, NativeFileName()))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }
        return NativeLibrary.TryLoad(FreeDvNativeMethods.LibraryName, assembly, null, out handle);
    }

    private static bool TryResolveRade(Assembly assembly, out IntPtr handle)
    {
        foreach (var candidate in CandidatePaths(assembly, RadeNativeFileName()))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }
        return NativeLibrary.TryLoad(RadeNativeMethods.LibraryName, assembly, null, out handle);
    }

    private static IEnumerable<string> CandidatePaths(Assembly assembly, string fileName)
    {
        string rid = CurrentRid();

        // Writable per-user install location first: a binary staged by the
        // in-app installer should win over (and back-fill) a missing bundled one.
        string? managedDir = ManagedLibraryDir();
        if (managedDir is not null) yield return Path.Combine(managedDir, fileName);

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

    /// <summary>
    /// The writable per-user directory the in-app installer stages the codec2
    /// binary into (LocalApplicationData/Zeus/freedv). Cross-platform via
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/>:
    /// %LOCALAPPDATA%\Zeus\freedv on Windows, ~/.local/share/Zeus/freedv on
    /// Linux, ~/Library/Application Support/Zeus/freedv on macOS. Null only when
    /// the platform exposes no local-app-data location.
    /// </summary>
    public static string? ManagedLibraryDir()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) return null;
        return Path.Combine(baseDir, "Zeus", "freedv");
    }

    /// <summary>Full path the codec2 binary is staged at for the current platform.</summary>
    public static string? ManagedLibraryPath()
    {
        string? dir = ManagedLibraryDir();
        return dir is null ? null : Path.Combine(dir, NativeFileName());
    }

    /// <summary>Runtime identifier (os-arch) used to resolve the bundled binary, e.g. "win-x64".</summary>
    public static string CurrentRid()
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

    /// <summary>Platform shared-library filename for codec2 (codec2.dll / libcodec2.so / libcodec2.dylib).</summary>
    public static string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libcodec2.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libcodec2.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "codec2.dll";
        return "libcodec2";
    }

    /// <summary>Platform shared-library filename for the zeus_rade shim (zeus_rade.dll / libzeus_rade.so / libzeus_rade.dylib).</summary>
    public static string RadeNativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libzeus_rade.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libzeus_rade.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "zeus_rade.dll";
        return "libzeus_rade";
    }
}
