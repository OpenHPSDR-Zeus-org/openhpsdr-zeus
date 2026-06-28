// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// DigitalTxStreamer — shared block-pacing helper for the FT8/FT4 and WSPR TX
// keyers. Both render a 48 kHz mono float waveform, then must feed it to
// TxAudioIngest in exactly 960-sample (3840-byte) f32le blocks at the 20 ms mic
// cadence (anything else is silently dropped by the ingest). Real-time pacing is
// Stopwatch-corrected, not a bare PeriodicTimer, so block delivery tracks
// elapsed wall time and the TxIqRing neither underflows nor overflows —
// identical parity with the live mic path on every platform.

using System.Buffers.Binary;
using System.Diagnostics;

namespace Zeus.Server;

internal static class DigitalTxStreamer
{
    /// <summary>Samples per ingest block (20 ms @ 48 kHz mono).</summary>
    public const int BlockSamples = 960;
    /// <summary>Bytes per ingest block (f32le).</summary>
    public const int BlockBytes = BlockSamples * 4;
    /// <summary>Block cadence in milliseconds.</summary>
    public const int BlockMs = 20;

    /// <summary>
    /// Stream <paramref name="leadBlocks"/> silence blocks followed by the
    /// <paramref name="audio"/> waveform as 960-sample f32le blocks into
    /// <paramref name="sink"/>, paced one block every 20 ms of elapsed wall time.
    /// The lead silence lets the T/R relay settle before real audio appears
    /// (a digital MOX source has no UI pre-key mute window). Stops early — leaving
    /// a clean block boundary — if <paramref name="ct"/> cancels or
    /// <paramref name="stillArmed"/> returns false (operator Halt / disarm
    /// mid-slot). <paramref name="delay"/> is injectable so tests run instantly.
    /// </summary>
    public static async Task StreamAsync(
        float[] audio,
        int leadBlocks,
        Action<ReadOnlyMemory<byte>> sink,
        Func<int, CancellationToken, Task> delay,
        Func<bool> stillArmed,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(sink);

        var block = new byte[BlockBytes];
        var sw = Stopwatch.StartNew();
        int blocksSent = 0;

        // Lead-in silence (T/R settle). The block buffer starts all-zero.
        for (int i = 0; i < leadBlocks; i++)
        {
            if (ct.IsCancellationRequested || !stillArmed()) return;
            sink(block);
            blocksSent++;
            await PaceAsync(sw, blocksSent, delay, ct).ConfigureAwait(false);
        }

        // Real audio, padded to a whole final block with trailing zeros.
        for (int offset = 0; offset < audio.Length; offset += BlockSamples)
        {
            if (ct.IsCancellationRequested || !stillArmed()) return;
            int count = Math.Min(BlockSamples, audio.Length - offset);
            for (int s = 0; s < BlockSamples; s++)
            {
                float v = s < count ? audio[offset + s] : 0f;
                BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan(s * 4, 4), v);
            }
            sink(block);
            blocksSent++;
            await PaceAsync(sw, blocksSent, delay, ct).ConfigureAwait(false);
        }
    }

    // Wait until the wall clock reaches the deadline for the next block, so the
    // average rate is exactly one block per 20 ms regardless of per-iteration
    // jitter. Never waits negative time (a late block fires immediately).
    private static Task PaceAsync(
        Stopwatch sw, int blocksSent, Func<int, CancellationToken, Task> delay, CancellationToken ct)
    {
        long deadlineMs = (long)blocksSent * BlockMs;
        long remaining = deadlineMs - sw.ElapsedMilliseconds;
        return remaining > 0 ? delay((int)remaining, ct) : Task.CompletedTask;
    }
}
