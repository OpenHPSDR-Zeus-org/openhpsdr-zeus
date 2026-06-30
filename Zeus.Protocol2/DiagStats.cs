// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Christian Suarez (N9WAR), and contributors.

namespace Zeus.Protocol2;

/// <summary>
/// Allocation-light statistics helpers for the ~1 Hz diagnostic log lines added
/// for issue #1148 (RX-IQ delivery jitter / inline-tick cadence / speaker-sink
/// pacing). Shared across <c>Zeus.Protocol2</c> and <c>Zeus.Server.Hosting</c>
/// (visible via <c>InternalsVisibleTo</c>) so the percentile math is written —
/// and unit-tested — exactly once instead of being copy-pasted into every diag
/// emit site.
/// </summary>
internal static class DiagStats
{
    /// <summary>
    /// Nearest-rank percentile (<paramref name="q"/> in [0,1]) over the first
    /// <paramref name="count"/> entries of <paramref name="values"/>. The
    /// entries are copied into <paramref name="scratch"/> and sorted ascending
    /// there, so the caller's ring buffer is left untouched. Returns 0 when
    /// <paramref name="count"/> is zero.
    /// </summary>
    /// <param name="values">Sample buffer (only [0, count) is read).</param>
    /// <param name="scratch">Scratch buffer; must be at least
    /// <paramref name="count"/> long. Sorted in place.</param>
    /// <param name="count">Number of valid samples in
    /// <paramref name="values"/>.</param>
    /// <param name="q">Quantile in [0,1], e.g. 0.99 for the 99th percentile.</param>
    public static long Percentile(long[] values, long[] scratch, int count, double q)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(scratch);
        if (count <= 0) return 0;
        if (count > values.Length) count = values.Length;
        if (count > scratch.Length) count = scratch.Length;

        Array.Copy(values, scratch, count);
        Array.Sort(scratch, 0, count);
        return scratch[PercentileIndex(count, q)];
    }

    /// <summary>
    /// Nearest-rank index into a length-<paramref name="count"/> ascending-sorted
    /// array for quantile <paramref name="q"/>. Clamped to [0, count-1].
    /// </summary>
    public static int PercentileIndex(int count, double q)
    {
        if (count <= 0) return 0;
        if (q < 0.0) q = 0.0;
        if (q > 1.0) q = 1.0;
        int idx = (int)Math.Ceiling(q * count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= count) idx = count - 1;
        return idx;
    }
}
