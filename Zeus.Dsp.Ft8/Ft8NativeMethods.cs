// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// P/Invoke bindings for the zeus_ft8 shared library (native/ft8/zeus_ft8.h, a
// stable C ABI over kgoba/ft8_lib MIT). Loaded dynamically — zeus_ft8.dll on
// Windows, libzeus_ft8.{so,dylib} elsewhere — resolved by Ft8NativeLoader,
// mirroring the FreeDV / WDSP native-load pattern. The ABI passes only flat C
// structs and arrays, so these bindings are blittable and stable across
// vendored-ft8_lib updates.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.Ft8;

internal static partial class Ft8NativeMethods
{
    // NativeLibrary resolves "zeus_ft8" -> zeus_ft8.dll / libzeus_ft8.{so,dylib}.
    internal const string LibraryName = "zeus_ft8";

    internal const int PROTO_FT4 = 0;
    internal const int PROTO_FT8 = 1;

    // Mirrors zeus_ft8_decode_t (60 bytes). `Text` is a fixed 40-byte
    // null-terminated buffer; blittable, no marshalling layer needed.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DecodeRaw
    {
        public float SnrDb;
        public float DtSec;
        public float FreqHz;
        public int Score;
        public int LdpcErrors;
        public fixed byte Text[40];
    }

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr zeus_ft8_ctx_create();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void zeus_ft8_ctx_destroy(IntPtr ctx);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void zeus_ft8_ctx_reset(IntPtr ctx);

    // Decode one slot. samples = mono float PCM in [-1,1]. Returns # decodes
    // written to `outBuf` (>=0), or <0 on error.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_ft8_decode(
        IntPtr ctx,
        [In] float[] samples, int n,
        int sampleRate, int protocol, int passes,
        [Out] DecodeRaw[] outBuf, int maxResults);

    // Encode message text -> FSK tone indices (79 FT8 / 105 FT4). Returns #tones.
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_ft8_encode(
        string message, int protocol, [Out] byte[] tones, int maxTones);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr zeus_ft8_version();
}
