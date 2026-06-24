// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Dependency-free streaming sample-rate conversion between the radio audio
// rate (48 kHz) and the FreeDV modem rate (8 kHz). The ratio is exactly 6:1,
// so a single Hamming-windowed-sinc prototype low-pass (cutoff ~3.4 kHz, well
// above the FreeDV occupied bandwidth of ~2.4 kHz) drives both a decimator and
// a polyphase interpolator. Both keep filter history across calls so block
// boundaries are seamless, and both are pure span -> span with NO allocation
// and NO locking — matching the realtime discipline of the Zeus audio bus
// (AudioChain, FloatSpscRing). This replaces any external libsamplerate / soxr
// dependency — important for the arm64 / Raspberry Pi targets.

using System.Runtime.CompilerServices;
using System.Threading;

namespace Zeus.Dsp.FreeDv;

/// <summary>Exact-ratio (6:1) low-pass FIR resamplers for 48 kHz &lt;-&gt; 8 kHz.</summary>
internal static class FreeDvResampler
{
    internal const int Factor = 6;            // 48000 / 8000
    internal const int FsHigh = 48000;
    internal const int FsLow = 8000;
    private const int TapsPerPhase = 16;      // -> 96-tap prototype, good stopband for voice
    private const double CutoffHz = 3400.0;   // < 8 kHz Nyquist, above FreeDV ~2.4 kHz BW

    // Prototype low-pass, normalized so DC gain == 1. Shared by both directions.
    private static readonly float[] Prototype = DesignLowpass(TapsPerPhase * Factor, CutoffHz, FsHigh);

    private static float[] DesignLowpass(int numTaps, double cutoffHz, double fsHz)
    {
        var h = new double[numTaps];
        double fc = cutoffHz / fsHz;          // normalized cutoff (cycles/sample)
        double mid = (numTaps - 1) / 2.0;
        double sum = 0.0;
        for (int i = 0; i < numTaps; i++)
        {
            double n = i - mid;
            double sinc = (Math.Abs(n) < 1e-9) ? 2.0 * fc : Math.Sin(2.0 * Math.PI * fc * n) / (Math.PI * n);
            double w = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (numTaps - 1)); // Hamming
            double v = sinc * w;
            h[i] = v;
            sum += v;
        }
        var taps = new float[numTaps];
        for (int i = 0; i < numTaps; i++) taps[i] = (float)(h[i] / sum); // DC gain 1
        return taps;
    }

    /// <summary>Upper bound on decimator output for a given input length.</summary>
    internal static int MaxDecimatedLength(int inputLen) => inputLen / Factor + 1;

    /// <summary>Exact interpolator output for a given input length.</summary>
    internal static int InterpolatedLength(int inputLen) => inputLen * Factor;

    internal static Decimator NewDecimator() => new(Prototype);
    internal static Interpolator NewInterpolator() => new(Prototype);

    /// <summary>48 kHz -&gt; 8 kHz. Streaming, history-preserving, keep-every-6th after low-pass.</summary>
    internal sealed class Decimator
    {
        private readonly float[] _taps;
        private readonly float[] _delay; // circular line, length = numTaps
        private int _head;               // index of most-recent sample
        private int _phase;              // 0..Factor-1; emit when wraps to 0

        internal Decimator(float[] taps)
        {
            _taps = taps;
            _delay = new float[taps.Length];
        }

        internal void Reset()
        {
            Array.Clear(_delay);
            _head = 0;
            _phase = 0;
        }

        /// <summary>Consumes all of <paramref name="input"/>; writes produced 8 kHz
        /// samples to <paramref name="output"/> (size at least MaxDecimatedLength).
        /// Returns the number of samples written. No allocation.</summary>
        internal int Process(ReadOnlySpan<float> input, Span<float> output)
        {
            int n = _taps.Length;
            int outCount = 0;
            foreach (float x in input)
            {
                _head = (_head + 1) % n;
                _delay[_head] = x;
                if (++_phase >= Factor)
                {
                    _phase = 0;
                    float acc = 0f;
                    int idx = _head;
                    for (int k = 0; k < n; k++)
                    {
                        acc += _taps[k] * _delay[idx];
                        idx = (idx == 0) ? n - 1 : idx - 1;
                    }
                    output[outCount++] = acc;
                }
            }
            return outCount;
        }
    }

    /// <summary>8 kHz -&gt; 48 kHz. Polyphase: each input sample yields Factor output samples.</summary>
    internal sealed class Interpolator
    {
        private readonly float[][] _phases; // [Factor][tapsPerPhase]
        private readonly float[] _delay;    // input history, length = tapsPerPhase
        private int _head;

        internal Interpolator(float[] prototype)
        {
            int perPhase = prototype.Length / Factor;
            _phases = new float[Factor][];
            for (int p = 0; p < Factor; p++)
            {
                var sub = new float[perPhase];
                for (int k = 0; k < perPhase; k++)
                    sub[k] = prototype[k * Factor + p] * Factor; // x Factor: zero-stuff energy comp
                _phases[p] = sub;
            }
            _delay = new float[perPhase];
        }

        internal void Reset()
        {
            Array.Clear(_delay);
            _head = 0;
        }

        /// <summary>Consumes all of <paramref name="input"/>; writes produced 48 kHz
        /// samples to <paramref name="output"/> (size at least InterpolatedLength).
        /// Returns the number of samples written. No allocation.</summary>
        internal int Process(ReadOnlySpan<float> input, Span<float> output)
        {
            int n = _delay.Length;
            int outCount = 0;
            foreach (float x in input)
            {
                _head = (_head + 1) % n;
                _delay[_head] = x;
                for (int p = 0; p < Factor; p++)
                {
                    float[] sub = _phases[p];
                    float acc = 0f;
                    int idx = _head;
                    for (int k = 0; k < n; k++)
                    {
                        acc += sub[k] * _delay[idx];
                        idx = (idx == 0) ? n - 1 : idx - 1;
                    }
                    output[outCount++] = acc;
                }
            }
            return outCount;
        }
    }
}

/// <summary>
/// Fixed-capacity, lock-free single-producer/single-consumer float ring used to
/// bridge the mismatched block cadences between the 48 kHz pipeline and FreeDV's
/// variable frame sizes. This is the FreeDV-assembly twin of
/// <c>Zeus.Server.FloatSpscRing</c> (same Volatile release/acquire cursor
/// discipline, power-of-two bitmask wrap, never-blocking Write/Read) — Zeus.Dsp
/// can't reference the Hosting assembly, so the proven design is mirrored here.
/// Within a single FreeDV direction the producer and consumer are the same
/// (DSP-tick) thread; the ring still uses SPSC-safe ordering so a reconfigure on
/// another thread that has gated the hot path out sees a consistent state.
/// </summary>
internal sealed class FreeDvSampleRing
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;
    private long _head; // consumer cursor
    private long _tail; // producer cursor

    internal FreeDvSampleRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of two.", nameof(capacityPowerOfTwo));
        _buffer = new float[capacityPowerOfTwo];
        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;
    }

    internal int Capacity => _capacity;

    internal int Count
    {
        get
        {
            long tail = Volatile.Read(ref _tail);
            long head = Volatile.Read(ref _head);
            long diff = tail - head;
            if (diff < 0) return 0;
            if (diff > _capacity) return _capacity;
            return (int)diff;
        }
    }

    /// <summary>Writes up to src.Length samples; returns the number written (short on full). Never blocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Write(ReadOnlySpan<float> src)
    {
        long tail = _tail;
        long head = Volatile.Read(ref _head);
        long space = _capacity - (tail - head);
        if (space <= 0) return 0;

        int n = src.Length;
        if (n > (int)space) n = (int)space;

        int idx = (int)(tail & _mask);
        int first = Math.Min(n, _capacity - idx);
        src.Slice(0, first).CopyTo(new Span<float>(_buffer, idx, first));
        if (first < n)
            src.Slice(first, n - first).CopyTo(new Span<float>(_buffer, 0, n - first));
        Volatile.Write(ref _tail, tail + n);
        return n;
    }

    /// <summary>Reads up to dst.Length samples; returns the number read (short on empty). Never blocks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Read(Span<float> dst)
    {
        long head = _head;
        long tail = Volatile.Read(ref _tail);
        long avail = tail - head;
        if (avail <= 0) return 0;

        int n = dst.Length;
        if (n > (int)avail) n = (int)avail;

        int idx = (int)(head & _mask);
        int first = Math.Min(n, _capacity - idx);
        new ReadOnlySpan<float>(_buffer, idx, first).CopyTo(dst.Slice(0, first));
        if (first < n)
            new ReadOnlySpan<float>(_buffer, 0, n - first).CopyTo(dst.Slice(first, n - first));
        Volatile.Write(ref _head, head + n);
        return n;
    }

    /// <summary>Drops the oldest n samples (latency bound). Consumer-side.</summary>
    internal void Drop(int n)
    {
        long head = _head;
        long tail = Volatile.Read(ref _tail);
        long avail = tail - head;
        if (n > avail) n = (int)avail;
        if (n > 0) Volatile.Write(ref _head, head + n);
    }

    /// <summary>Discards everything. Call only while quiesced (hot path gated out).</summary>
    internal void Clear()
    {
        Volatile.Write(ref _head, Volatile.Read(ref _tail));
    }
}

/// <summary>
/// Lock-free single-producer/single-consumer byte ring for the FreeDV text
/// sidechannel. The producer is the RX hot path (the codec2 txt callback fires
/// inside freedv_rx); the consumer is the status thread draining decoded chars.
/// Same Volatile release/acquire discipline and power-of-two wrap as
/// <see cref="FreeDvSampleRing"/> — a byte twin so the hot-path enqueue never
/// allocates or blocks. Oldest bytes are dropped on overflow (text is advisory).
/// </summary>
internal sealed class FreeDvByteRing
{
    private readonly byte[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;
    private long _head; // consumer cursor
    private long _tail; // producer cursor

    internal FreeDvByteRing(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of two.", nameof(capacityPowerOfTwo));
        _buffer = new byte[capacityPowerOfTwo];
        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;
    }

    /// <summary>Enqueue one byte; drop the oldest if full. Producer-side, no alloc.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteByte(byte value)
    {
        long tail = _tail;
        long head = Volatile.Read(ref _head);
        if (tail - head >= _capacity)
            Volatile.Write(ref _head, head + 1); // drop oldest to make room
        _buffer[(int)(tail & _mask)] = value;
        Volatile.Write(ref _tail, tail + 1);
    }

    /// <summary>Reads up to dst.Length bytes; returns the number read (0 on empty).</summary>
    internal int Read(Span<byte> dst)
    {
        long head = _head;
        long tail = Volatile.Read(ref _tail);
        long avail = tail - head;
        if (avail <= 0) return 0;
        int n = dst.Length;
        if (n > (int)avail) n = (int)avail;
        for (int i = 0; i < n; i++)
            dst[i] = _buffer[(int)((head + i) & _mask)];
        Volatile.Write(ref _head, head + n);
        return n;
    }

    /// <summary>Discards everything. Call only while quiesced (hot path gated out).</summary>
    internal void Clear() => Volatile.Write(ref _head, Volatile.Read(ref _tail));
}
