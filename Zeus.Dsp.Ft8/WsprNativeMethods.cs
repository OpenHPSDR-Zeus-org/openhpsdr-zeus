// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// P/Invoke bindings for the zeus_wspr shared library (native/wspr/zeus_wspr.h,
// a stable C ABI over the vendored K1JT/K9AN wsprd, GPL-3). Lives in the
// Zeus.Dsp.Ft8 assembly and rides its single SetDllImportResolver — the loader
// resolves both "zeus_ft8" and "zeus_wspr" (the FreeDV one-resolver-many-libs
// pattern). Decode is POSIX-only today; on Windows the lib ships encode+synth
// only, so zeus_wspr_decode is absent there (WsprDecoder.DecodeAvailable gates).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.Ft8;

internal static partial class WsprNativeMethods
{
    internal const string LibraryName = "zeus_wspr";

    // Mirrors zeus_wspr_spot_t (44 bytes). Message is a fixed 28-byte buffer.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct SpotRaw
    {
        public float SnrDb;
        public float DtSec;
        public float FreqMhz;
        public int DriftHz;
        public fixed byte Message[28];
    }

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_wspr_encode(string message, [Out] byte[] symbols, int maxSymbols);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_wspr_synth(
        [In] byte[] symbols, int nSym, float f0Hz, int sampleRate,
        [Out] float[] audio, int maxSamples);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_wspr_decode(
        [In] float[] samples, int n, int sampleRate, double dialFreqMhz,
        [Out] SpotRaw[] outBuf, int maxResults);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr zeus_wspr_version();
}
