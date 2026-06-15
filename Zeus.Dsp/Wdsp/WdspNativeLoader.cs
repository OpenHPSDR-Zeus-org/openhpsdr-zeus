// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.Wdsp;

internal static class WdspNativeLoader
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static bool _probedLoadable;
    private static bool _loadable;
    private static readonly Dictionary<string, bool> ExportCache = new(StringComparer.Ordinal);

    internal static void EnsureResolverRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
            _registered = true;
        }
    }

    internal static bool TryProbe()
    {
        EnsureResolverRegistered();
        if (_probedLoadable) return _loadable;
        lock (Gate)
        {
            if (_probedLoadable) return _loadable;
            if (TryResolve(typeof(NativeMethods).Assembly, out var handle))
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

    internal static bool TryProbeExport(string symbolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName);

        EnsureResolverRegistered();
        lock (Gate)
        {
            if (ExportCache.TryGetValue(symbolName, out bool available))
                return available;

            if (!TryResolve(typeof(NativeMethods).Assembly, out var handle))
            {
                ExportCache[symbolName] = false;
                return false;
            }

            try
            {
                available = NativeLibrary.TryGetExport(handle, symbolName, out _);
                ExportCache[symbolName] = available;
                return available;
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.LibraryName) return IntPtr.Zero;
        return TryResolve(assembly, out var handle) ? handle : IntPtr.Zero;
    }

    private static bool TryResolve(Assembly assembly, out IntPtr handle)
    {
        foreach (var candidate in CandidatePaths(assembly))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                return true;
        }
        return NativeLibrary.TryLoad(NativeMethods.LibraryName, assembly, null, out handle);
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libwdsp.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libwdsp.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "wdsp.dll";
        return "libwdsp";
    }
}
