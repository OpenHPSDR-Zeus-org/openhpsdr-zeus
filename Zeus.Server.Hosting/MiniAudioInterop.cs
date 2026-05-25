// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF) and contributors.
//
// P/Invoke surface for the vendored miniaudio shim
// (native/miniaudio/zeus_miniaudio.{c,h}). Only used by desktop mode —
// service-mode Zeus.Server never loads libminiaudio.
//
// The shim API uses opaque void* handles and primitive types only, so this
// file is intentionally tiny. Anything fancier (config struct marshalling,
// device enumeration, format conversion) stays on the C side.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Zeus.Server;

/// <summary>
/// Raw P/Invoke bindings for `libminiaudio` (Zeus's shim on top of mackron's
/// miniaudio single-header library). The native side is the only place that
/// touches miniaudio's internal struct layout; this file only knows about
/// opaque handles + primitive types.
///
/// Loader resolution mirrors <c>Zeus.Dsp.Wdsp.WdspNativeLoader</c>:
/// `runtimes/&lt;rid&gt;/native/libminiaudio.{dylib,so}` (or `miniaudio.dll`)
/// next to the executing assembly, falling back to the standard OS search
/// path. Service mode never registers the sink that calls this so the
/// .dylib is never loaded.
/// </summary>
internal static partial class MiniAudioInterop
{
    internal const string LibraryName = "miniaudio";

    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>
    /// Register the DllImport resolver so probe-by-RID works under
    /// `dotnet run` / installed `.app` layouts. Idempotent; safe to call
    /// repeatedly. Always call before the first interop into miniaudio.
    /// </summary>
    internal static void EnsureResolverRegistered()
    {
        if (_registered) return;
        lock (Gate)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(typeof(MiniAudioInterop).Assembly, Resolve);
            _registered = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName) return IntPtr.Zero;

        string rid = CurrentRid();
        string fileName = NativeFileName();
        string? asmDir = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrEmpty(asmDir))
        {
            string c1 = Path.Combine(asmDir, "runtimes", rid, "native", fileName);
            if (File.Exists(c1) && NativeLibrary.TryLoad(c1, out var h1)) return h1;
            string c2 = Path.Combine(asmDir, fileName);
            if (File.Exists(c2) && NativeLibrary.TryLoad(c2, out var h2)) return h2;
        }

        string baseDir = AppContext.BaseDirectory;
        string c3 = Path.Combine(baseDir, "runtimes", rid, "native", fileName);
        if (File.Exists(c3) && NativeLibrary.TryLoad(c3, out var h3)) return h3;
        string c4 = Path.Combine(baseDir, fileName);
        if (File.Exists(c4) && NativeLibrary.TryLoad(c4, out var h4)) return h4;

        return NativeLibrary.TryLoad(LibraryName, assembly, null, out var h5) ? h5 : IntPtr.Zero;
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libminiaudio.dylib";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "libminiaudio.so";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "miniaudio.dll";
        return "libminiaudio";
    }

    // ---- Native delegate types --------------------------------------------
    //
    // Both data callbacks fire on miniaudio's dedicated audio worker thread.
    // C# implementations must not block, allocate, or call back into managed
    // code that could acquire locks the rest of the host might hold —
    // standard real-time audio discipline.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void PlaybackCallback(IntPtr user, IntPtr outBuffer, uint frameCount, uint channels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CaptureCallback(IntPtr user, IntPtr inBuffer, uint frameCount, uint channels);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NotifyCallback(IntPtr user, int kind);

    // ---- Playback ---------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_create")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr OutputCreate(
        uint preferSampleRate,
        uint preferChannels,
        uint periodFrames,
        uint periods,
        IntPtr dataCallback,
        IntPtr notifyCallback,
        IntPtr user);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_start")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int OutputStart(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int OutputStop(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_sample_rate")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint OutputSampleRate(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_channels")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint OutputChannels(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_backend_name")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr OutputBackendNamePtr(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_buffer_frames")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint OutputBufferFrames(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_periods")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint OutputPeriods(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_share_mode_exclusive")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int OutputShareModeExclusive(IntPtr handle);

    /// <summary>Marshal the playback device's active backend name
    /// ("WASAPI" / "DirectSound" / "Core Audio" / ...) for logging.</summary>
    internal static string OutputBackendName(IntPtr handle)
    {
        IntPtr p = OutputBackendNamePtr(handle);
        return p == IntPtr.Zero ? "unknown" : (Marshal.PtrToStringAnsi(p) ?? "unknown");
    }

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_output_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void OutputDestroy(IntPtr handle);

    // ---- Capture ----------------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_create")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr InputCreate(
        uint preferSampleRate,
        uint preferChannels,
        uint periodFrames,
        uint periods,
        IntPtr dataCallback,
        IntPtr notifyCallback,
        IntPtr user);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_start")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int InputStart(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int InputStop(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_sample_rate")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint InputSampleRate(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_channels")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint InputChannels(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_backend_name")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr InputBackendNamePtr(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_buffer_frames")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint InputBufferFrames(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_periods")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint InputPeriods(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_share_mode_exclusive")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int InputShareModeExclusive(IntPtr handle);

    /// <summary>Marshal the capture device's active backend name for logging.</summary>
    internal static string InputBackendName(IntPtr handle)
    {
        IntPtr p = InputBackendNamePtr(handle);
        return p == IntPtr.Zero ? "unknown" : (Marshal.PtrToStringAnsi(p) ?? "unknown");
    }

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_input_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void InputDestroy(IntPtr handle);

    // ---- Version probe ---------------------------------------------------

    [LibraryImport(LibraryName, EntryPoint = "zeus_ma_version")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr VersionPtr();

    /// <summary>Marshal the shim's version string for logging.</summary>
    internal static string Version()
    {
        IntPtr p = VersionPtr();
        return p == IntPtr.Zero ? "zeus-miniaudio (unknown)" : (Marshal.PtrToStringAnsi(p) ?? "");
    }
}
