// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Managed wrapper over the zeus_ft8 native decoder. One Ft8Decoder owns one
// native per-RX context, so a separate instance per receiver slice supports
// concurrent multi-band decode (each on its own worker thread). A single
// Ft8Decoder is NOT safe to drive from two threads at once — match one decoder
// to one decode worker, exactly as the native ABI requires.

using System.Runtime.InteropServices;

namespace Zeus.Dsp.Ft8;

/// <summary>FT8 vs FT4 protocol selector.</summary>
public enum Ft8Protocol
{
    Ft8 = Ft8NativeMethods.PROTO_FT8,
    Ft4 = Ft8NativeMethods.PROTO_FT4,
}

/// <summary>One decoded FT8/FT4 message (a single WSJT-X-style decode line).</summary>
public readonly record struct Ft8DecodeResult(
    float SnrDb,
    float DtSec,
    float FreqHz,
    int Score,
    int LdpcErrors,
    string Text);

/// <summary>
/// Per-RX FT8/FT4 decoder. Wraps a native zeus_ft8 context. Decode one
/// UTC-aligned slot of 12 kHz mono audio at a time. Dispose to free the context.
/// </summary>
public sealed class Ft8Decoder : IDisposable
{
    private IntPtr _ctx;
    private readonly object _gate = new();

    /// <summary>The canonical FT8/FT4 audio sample rate.</summary>
    public const int SampleRate = 12000;

    /// <summary>True if the zeus_ft8 native library is available on this platform.</summary>
    public static bool IsAvailable => Ft8NativeLoader.TryProbe();

    /// <summary>Native library version string, or null if unavailable.</summary>
    public static string? NativeVersion
    {
        get
        {
            if (!Ft8NativeLoader.TryProbe()) return null;
            IntPtr p = Ft8NativeMethods.zeus_ft8_version();
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
        }
    }

    public Ft8Decoder()
    {
        Ft8NativeLoader.EnsureResolverRegistered();
        _ctx = Ft8NativeMethods.zeus_ft8_ctx_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("zeus_ft8_ctx_create failed (native library unavailable?).");
    }

    /// <summary>Clear the accumulated callsign hash table (e.g. on band change).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_ctx != IntPtr.Zero) Ft8NativeMethods.zeus_ft8_ctx_reset(_ctx);
        }
    }

    /// <summary>
    /// Decode one slot of mono float audio (must be <see cref="SampleRate"/> Hz,
    /// in [-1,1], one 15 s FT8 / 7.5 s FT4 slot). <paramref name="passes"/> = 1
    /// for NORMAL, &gt;1 for deep multi-pass (FT8 only). Returns the decodes.
    /// </summary>
    public IReadOnlyList<Ft8DecodeResult> Decode(
        float[] samples, Ft8Protocol protocol = Ft8Protocol.Ft8, int passes = 1, int maxResults = 64)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0) return [];
        if (maxResults <= 0) maxResults = 1;

        var raw = new Ft8NativeMethods.DecodeRaw[maxResults];
        int got;
        lock (_gate)
        {
            if (_ctx == IntPtr.Zero) throw new ObjectDisposedException(nameof(Ft8Decoder));
            got = Ft8NativeMethods.zeus_ft8_decode(
                _ctx, samples, samples.Length, SampleRate, (int)protocol, passes, raw, maxResults);
        }
        if (got <= 0) return [];

        var list = new List<Ft8DecodeResult>(got);
        for (int i = 0; i < got; i++)
            list.Add(ToResult(in raw[i]));
        return list;
    }

    private static unsafe Ft8DecodeResult ToResult(in Ft8NativeMethods.DecodeRaw r)
    {
        string text;
        fixed (byte* p = r.Text)
        {
            int len = 0;
            while (len < 40 && p[len] != 0) len++;
            text = len == 0 ? string.Empty : Marshal.PtrToStringUTF8((IntPtr)p, len);
        }
        return new Ft8DecodeResult(r.SnrDb, r.DtSec, r.FreqHz, r.Score, r.LdpcErrors, text);
    }

    /// <summary>
    /// Encode a message ("CQ KB2UKA FN12") to FSK tone indices (79 FT8 / 105 FT4).
    /// Returns null if the message cannot be encoded.
    /// </summary>
    public static byte[]? Encode(string message, Ft8Protocol protocol = Ft8Protocol.Ft8)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        Ft8NativeLoader.EnsureResolverRegistered();
        var tones = new byte[protocol == Ft8Protocol.Ft4 ? 105 : 79];
        int n = Ft8NativeMethods.zeus_ft8_encode(message, (int)protocol, tones, tones.Length);
        if (n <= 0) return null;
        return n == tones.Length ? tones : tones[..n];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_ctx != IntPtr.Zero)
            {
                Ft8NativeMethods.zeus_ft8_ctx_destroy(_ctx);
                _ctx = IntPtr.Zero;
            }
        }
        GC.SuppressFinalize(this);
    }

    ~Ft8Decoder()
    {
        if (_ctx != IntPtr.Zero)
            Ft8NativeMethods.zeus_ft8_ctx_destroy(_ctx);
    }
}
