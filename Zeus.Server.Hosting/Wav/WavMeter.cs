// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server.Wav;

/// <summary>
/// Lock-free peak-hold / RMS / clip meter for the tape deck. Folded one audio
/// block at a time from the capture and playback threads and read from the
/// status path. The fields are plain (not behind a lock): a torn read of a VU
/// value is harmless, and the audio hot path must never block on a meter. Time
/// drives the peak decay (≈20 dB/sec) so the result is correct regardless of
/// how big each block is.
/// </summary>
public sealed class WavMeter
{
    // Sample magnitude at/above which we treat the block as clipped, and how
    // long the clip latch stays lit after the last clipped block.
    private const double ClipThreshold = 0.999;
    private const long ClipHoldMs = 1000;

    private double _peak;
    private double _rms;
    private long _clipUntilTick;
    private long _lastTick;

    // Monotonic millisecond clock. Defaults to Environment.TickCount64; an
    // injected provider lets tests drive decay / clip-latch expiry deterministically
    // without flaky sleeps. WavRecorderService uses the default.
    private readonly Func<long> _now;

    public WavMeter(Func<long>? nowMsProvider = null)
    {
        _now = nowMsProvider ?? (() => Environment.TickCount64);
        Reset();
    }

    /// <summary>Linear peak, 0..1, with peak-hold + decay.</summary>
    public double Peak => _peak;

    /// <summary>Linear RMS of the most recent block, 0..1.</summary>
    public double Rms => _rms;

    /// <summary>Peak expressed in dBFS, floored at -100.</summary>
    public double PeakDb => ToDb(_peak);

    /// <summary>True while the clip latch is lit.</summary>
    public bool Clip => _now() < Volatile.Read(ref _clipUntilTick);

    /// <summary>Reset to silence — called on record/play start and stop/finish.</summary>
    public void Reset()
    {
        _peak = 0;
        _rms = 0;
        Volatile.Write(ref _clipUntilTick, 0);
        Volatile.Write(ref _lastTick, _now());
    }

    /// <summary>Fold one block of mono samples into the meter.</summary>
    public void Update(ReadOnlySpan<float> block)
    {
        if (block.IsEmpty) return;
        double blockPeak = 0, sumSq = 0;
        bool clipped = false;
        for (int i = 0; i < block.Length; i++)
        {
            double s = block[i];
            double a = Math.Abs(s);
            if (a > blockPeak) blockPeak = a;
            sumSq += s * s;
            if (a >= ClipThreshold) clipped = true;
        }
        double blockRms = Math.Sqrt(sumSq / block.Length);

        long now = _now();
        long last = Volatile.Read(ref _lastTick);
        Volatile.Write(ref _lastTick, now);
        double dt = Math.Min(1.0, Math.Max(0, (now - last) / 1000.0));
        // 20 dB/sec on the linear scale ⇒ factor = 10^(-dt).
        double decayed = _peak * Math.Pow(10, -dt);

        _peak = Math.Max(blockPeak, decayed);
        _rms = blockRms;
        if (clipped) Volatile.Write(ref _clipUntilTick, now + ClipHoldMs);
    }

    /// <summary>Linear amplitude → dBFS, floored at -100.</summary>
    public static double ToDb(double linear)
        => linear <= 0 ? -100 : Math.Max(-100, 20 * Math.Log10(linear));
}
