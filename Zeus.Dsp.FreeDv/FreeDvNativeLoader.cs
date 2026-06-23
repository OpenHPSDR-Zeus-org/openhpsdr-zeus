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

using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.FreeDv;

internal static class FreeDvNativeLoader
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static bool _probedLoadable;
    private static bool _loadable;

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

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != FreeDvNativeMethods.LibraryName) return IntPtr.Zero;
        return TryResolve(assembly, out var handle) ? handle : IntPtr.Zero;
    }

    private static bool TryResolve(Assembly assembly, out IntPtr handle)
    {
        foreach (var candidate in CandidatePaths(assembly))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }
        return NativeLibrary.TryLoad(FreeDvNativeMethods.LibraryName, assembly, null, out handle);
    }

    private static IEnumerable<string> CandidatePaths(Assembly assembly)
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libcodec2.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libcodec2.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "codec2.dll";
        return "libcodec2";
    }
}
