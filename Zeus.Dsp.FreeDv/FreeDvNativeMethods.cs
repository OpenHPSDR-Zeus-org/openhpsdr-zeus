// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// P/Invoke bindings for the FreeDV modem in libcodec2 (drowe67/codec2,
// freedv_api.h, LGPL-2.1). Loaded dynamically as a shared library — codec2.dll
// on Windows, libcodec2.so / libcodec2.dylib elsewhere — resolved by
// FreeDvNativeLoader, exactly mirroring the WDSP native-load pattern. Only the
// classic HF voice modes are used (no LPCNet/2020), so the library has no
// external runtime dependencies.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.FreeDv;

internal static partial class FreeDvNativeMethods
{
    // NativeLibrary resolves "codec2" -> codec2.dll / libcodec2.so / libcodec2.dylib.
    internal const string LibraryName = "codec2";

    // Upstream FREEDV_MODE_* constants (freedv_api.h). Only the classic HF
    // voice modes are exposed by Zeus; the LPCNet 2020 family is excluded.
    internal const int FREEDV_MODE_1600 = 0;
    internal const int FREEDV_MODE_700C = 6;
    internal const int FREEDV_MODE_700D = 7;
    internal const int FREEDV_MODE_700E = 13;
    internal const int FREEDV_MODE_800XA = 5;

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr freedv_open(int mode);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void freedv_close(IntPtr freedv);

    // TX: feed exactly freedv_get_n_speech_samples() speech samples;
    // receive exactly freedv_get_n_nom_modem_samples() modem samples.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void freedv_tx(IntPtr freedv, short[] mod_out, short[] speech_in);

    // RX: feed exactly freedv_nin() modem samples; returns nout speech samples
    // actually produced (0 until synced). nin must be re-read after every call.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_rx(IntPtr freedv, short[] speech_out, short[] demod_in);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_nin(IntPtr freedv);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_get_n_speech_samples(IntPtr freedv);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_get_n_max_speech_samples(IntPtr freedv);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_get_n_nom_modem_samples(IntPtr freedv);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_get_n_max_modem_samples(IntPtr freedv);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_get_modem_sample_rate(IntPtr freedv);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_get_speech_sample_rate(IntPtr freedv);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int freedv_get_sync(IntPtr freedv);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void freedv_get_modem_stats(IntPtr freedv, out int sync, out float snr_est);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void freedv_set_squelch_en(IntPtr freedv, [MarshalAs(UnmanagedType.U1)] bool squelch_en);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void freedv_set_snr_squelch_thresh(IntPtr freedv, float snr_squelch_thresh);
}
