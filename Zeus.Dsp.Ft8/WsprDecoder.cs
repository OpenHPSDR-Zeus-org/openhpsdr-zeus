// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Managed wrapper over the zeus_wspr native decoder (vendored K1JT/K9AN wsprd,
// GPL-3). WSPR decode is stateless from the caller's view (the native side
// serialises internally), so these are static helpers — one call per completed
// 120 s slot. Decode is POSIX-only today; on a Windows build that ships
// encode+synth only, DecodeSupported is false and Decode returns nothing.

using System.Runtime.InteropServices;

namespace Zeus.Dsp.Ft8;

/// <summary>One decoded WSPR spot.</summary>
public readonly record struct WsprSpot(
    float SnrDb,
    float DtSec,
    float FreqMhz,
    int DriftHz,
    string Message);

public static class WsprDecoder
{
    /// <summary>The canonical WSPR audio sample rate.</summary>
    public const int SampleRate = 12000;

    /// <summary>True if the zeus_wspr native library is available.</summary>
    public static bool IsAvailable => Ft8NativeLoader.TryProbeWspr();

    /// <summary>Native library version string, or null if unavailable.</summary>
    public static string? NativeVersion
    {
        get
        {
            if (!Ft8NativeLoader.TryProbeWspr()) return null;
            IntPtr p = WsprNativeMethods.zeus_wspr_version();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }
    }

    /// <summary>
    /// Encode a WSPR message ("KB2UKA FN12 30") to 162 4-FSK tone indices (0..3),
    /// or null if the message cannot be encoded.
    /// </summary>
    public static byte[]? Encode(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        Ft8NativeLoader.TryProbeWspr();
        var tones = new byte[162];
        int n = WsprNativeMethods.zeus_wspr_encode(message, tones, tones.Length);
        return n == tones.Length ? tones : null;
    }

    /// <summary>
    /// Synthesize the continuous-phase 4-FSK audio for a WSPR symbol sequence
    /// (the TX beacon waveform). Returns null if synthesis fails.
    /// </summary>
    public static float[]? Synth(byte[] symbols, float f0Hz = 1500f, int sampleRate = SampleRate)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        if (symbols.Length == 0) return null;
        Ft8NativeLoader.TryProbeWspr();
        int nspsym = (int)(sampleRate * (8192.0 / 12000.0) + 0.5);
        var audio = new float[symbols.Length * nspsym];
        int n = WsprNativeMethods.zeus_wspr_synth(symbols, symbols.Length, f0Hz, sampleRate, audio, audio.Length);
        if (n <= 0) return null;
        return n == audio.Length ? audio : audio[..n];
    }

    /// <summary>
    /// Decode one WSPR slot of mono float audio (must be <see cref="SampleRate"/>
    /// Hz, ~114 s). <paramref name="dialFreqMhz"/> labels the absolute frequency.
    /// Returns the spots, or empty if decode is unsupported on this platform.
    /// </summary>
    public static IReadOnlyList<WsprSpot> Decode(float[] samples, double dialFreqMhz, int maxResults = 32)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0 || maxResults <= 0) return [];
        if (!Ft8NativeLoader.TryProbeWspr()) return [];

        var raw = new WsprNativeMethods.SpotRaw[maxResults];
        int got = 0;
        Exception? error = null;

        // The vendored wsprd uses large stack arrays (a 512xN VLA), which overflow
        // the ~1 MB stack of thread-pool / xunit threads. Run the native decode on
        // a dedicated big-stack thread. WSPR decodes once per 120 s and the native
        // side already serialises, so a thread per decode is free.
        var worker = new Thread(() =>
        {
            try
            {
                got = WsprNativeMethods.zeus_wspr_decode(
                    samples, samples.Length, SampleRate, dialFreqMhz, raw, maxResults);
            }
            catch (EntryPointNotFoundException)
            {
                got = 0; // decode absent (Windows encode-only build)
            }
            catch (Exception e)
            {
                error = e;
            }
        }, 16 * 1024 * 1024);
        worker.IsBackground = true;
        worker.Start();
        worker.Join();

        if (error is not null) throw error;
        if (got <= 0) return [];

        var list = new List<WsprSpot>(got);
        for (int i = 0; i < got; i++)
            list.Add(ToSpot(in raw[i]));
        return list;
    }

    private static unsafe WsprSpot ToSpot(in WsprNativeMethods.SpotRaw r)
    {
        string text;
        fixed (byte* p = r.Message)
        {
            int len = 0;
            while (len < 28 && p[len] != 0) len++;
            text = len == 0 ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)p, len);
        }
        return new WsprSpot(r.SnrDb, r.DtSec, r.FreqMhz, r.DriftHz, text);
    }
}
