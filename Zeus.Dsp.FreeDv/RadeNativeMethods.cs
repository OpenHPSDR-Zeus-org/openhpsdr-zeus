// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// P/Invoke bindings for the zeus_rade shim (native/radae/shim/zeus_rade.h) — a
// thin single-entry wrapper over RADE V1 (radae_nopy) + the FARGAN vocoder that
// hides RADE's two-stage decode behind one streaming "complex IQ in -> 16 kHz
// PCM out" surface. Loaded dynamically as a shared library — zeus_rade.dll on
// Windows, libzeus_rade.so / libzeus_rade.dylib elsewhere — resolved by
// FreeDvNativeLoader (same once-per-assembly resolver as codec2).
//
// The native binary is built separately by CI and may be absent; every call
// site MUST guard via FreeDvNativeLoader.TryProbeRade() before invoking these.
//
// RADE_COMP is a C struct of two floats {real, imag}; an array of N complex
// samples is therefore 2*N interleaved floats [r0,i0,r1,i1,...], which the
// managed side passes as a float[] (rx_in) — see zeus_rade_rx.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zeus.Dsp.FreeDv;

internal static partial class RadeNativeMethods
{
    // NativeLibrary resolves "zeus_rade" -> zeus_rade.dll / libzeus_rade.so / libzeus_rade.dylib.
    internal const string LibraryName = "zeus_rade";

    // Library lifecycle (wrap rade_initialize/finalize; call once per process).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void zeus_rade_global_init();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void zeus_rade_global_shutdown();

    // Open/close a decoder context. Returns IntPtr.Zero on failure.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr zeus_rade_open();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void zeus_rade_close(IntPtr z);

    // Buffer-sizing helpers (call after open).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_nin(IntPtr z);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_nin_max(IntPtr z);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_max_pcm_per_rx(IntPtr z);

    // Decode one block of IQ to 16 kHz PCM.
    //   rx_in   : zeus_rade_nin(z) complex (interleaved real,imag) samples @ 8 kHz
    //   pcm_out : caller buffer, >= zeus_rade_max_pcm_per_rx(z) int16 samples @ 16 kHz
    // Returns the number of int16 PCM samples written (0 while unsynced / priming).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_rx(IntPtr z, float[] rx_in, short[] pcm_out);

    // Telemetry (valid when synced).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_sync(IntPtr z);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial float zeus_rade_freq_offset(IntPtr z);

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_snr_db(IntPtr z);

    // Last decoded End-of-Over callsign. Copies into callsign_out (>= 9 bytes);
    // returns chars written, 0 if none since the last over. Decoded with the
    // FreeDV reliable-text LDPC (rade_text) — CRC-checked, FreeDV-GUI compatible.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_get_eoo_callsign(IntPtr z, byte[] callsign_out);

    // ---- Transmit (mirror of the RX surface) ----
    // TX sizing (call after open).
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_n_speech_samples(IntPtr z); // int16 @16k consumed per tx call

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_n_tx_out(IntPtr z);         // RADE_COMP produced per tx call

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_n_tx_eoo_out(IntPtr z);     // RADE_COMP in the EOO frame

    // Encode one frame-group of 16 kHz speech to modem IQ.
    //   pcm_in : zeus_rade_n_speech_samples(z) int16 @16k
    //   tx_out : >= zeus_rade_n_tx_out(z) interleaved complex (real,imag) floats @8k
    // Returns the number of RADE_COMP samples written.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_tx(IntPtr z, short[] pcm_in, float[] tx_out);

    // Flush the End-of-Over frame (carries the callsign). Call once on un-key.
    //   tx_eoo_out : >= zeus_rade_n_tx_eoo_out(z) interleaved complex floats @8k
    // Returns the number of RADE_COMP samples written.
    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int zeus_rade_tx_eoo(IntPtr z, float[] tx_eoo_out);

    // Set the EOO callsign (<= 8 chars), encoded with the FreeDV reliable-text
    // LDPC so it interoperates with FreeDV-GUI. Empty/null clears it.
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void zeus_rade_set_tx_callsign(IntPtr z, string callsign);
}
