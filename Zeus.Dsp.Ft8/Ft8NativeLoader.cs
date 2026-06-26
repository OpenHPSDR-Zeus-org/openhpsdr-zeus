// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Resolver for the zeus_ft8 shared library, mirroring FreeDvNativeLoader. Lives
// in its own assembly (Zeus.Dsp.Ft8) so it owns this assembly's single
// NativeLibrary.SetDllImportResolver registration. The zeus_ft8 binary ships
// under Zeus.Dsp/runtimes/{rid}/native and lands in the shared output
// directory, so both the assembly-relative and BaseDirectory probes find it at
// runtime. A writable per-user managed dir is probed first so an in-app install
// can back-fill a platform whose binary was not shipped.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.Ft8;

public static class Ft8NativeLoader
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static bool _probed;
    private static bool _loadable;

    internal static void EnsureResolverRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(typeof(Ft8NativeMethods).Assembly, Resolve);
            _registered = true;
        }
    }

    /// <summary>True if the zeus_ft8 shared library can be located and loaded.</summary>
    public static bool TryProbe()
    {
        EnsureResolverRegistered();
        if (_probed) return _loadable;
        lock (Gate)
        {
            if (_probed) return _loadable;
            if (TryResolve(typeof(Ft8NativeMethods).Assembly, out var handle))
            {
                NativeLibrary.Free(handle);
                _loadable = true;
            }
            else
            {
                _loadable = false;
            }
            _probed = true;
            return _loadable;
        }
    }

    /// <summary>Drop the cached probe result so the next probe re-scans (after an install).</summary>
    public static void ResetProbe()
    {
        lock (Gate) { _probed = false; _loadable = false; }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == Ft8NativeMethods.LibraryName)
            return TryResolve(assembly, out var handle) ? handle : IntPtr.Zero;
        return IntPtr.Zero;
    }

    private static bool TryResolve(Assembly assembly, out IntPtr handle)
    {
        foreach (var candidate in CandidatePaths(assembly, NativeFileName()))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }
        return NativeLibrary.TryLoad(Ft8NativeMethods.LibraryName, assembly, null, out handle);
    }

    private static IEnumerable<string> CandidatePaths(Assembly assembly, string fileName)
    {
        string rid = CurrentRid();

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

    /// <summary>Writable per-user dir an in-app installer would stage zeus_ft8 into.</summary>
    public static string? ManagedLibraryDir()
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) return null;
        return Path.Combine(baseDir, "Zeus", "ft8");
    }

    /// <summary>Full path zeus_ft8 is staged at for the current platform.</summary>
    public static string? ManagedLibraryPath()
    {
        string? dir = ManagedLibraryDir();
        return dir is null ? null : Path.Combine(dir, NativeFileName());
    }

    /// <summary>Runtime identifier (os-arch), e.g. "osx-arm64".</summary>
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

    /// <summary>Platform shared-library filename (zeus_ft8.dll / libzeus_ft8.{so,dylib}).</summary>
    public static string NativeFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libzeus_ft8.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libzeus_ft8.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "zeus_ft8.dll";
        return "libzeus_ft8";
    }
}
