// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using System.Threading;

namespace Zeus.Server;

/// <summary>
/// Non-blocking single-entry gate. Exactly one caller at a time may hold the
/// gate; concurrent callers are rejected (they get <c>false</c>) rather than
/// blocked. Used to make <see cref="DspPipelineService"/>'s <c>Tick</c> mutually
/// exclusive across the timer thread and the RX inline-tick thread during the
/// sink attach/detach window (issue #1167), keeping the strict
/// single-producer <see cref="FloatSpscRing"/> producer side single-threaded.
///
/// <para>
/// This deliberately does NOT re-add a per-sink lock (that reintroduced the
/// #1148 hot-path contention) and does NOT block. A rejected caller simply
/// skips its critical section — the holder is already executing it ~now.
/// </para>
/// </summary>
internal sealed class SingleEntryGate
{
    private int _busy; // 0 = free, 1 = entered

    /// <summary>Returns true to exactly one caller at a time. Concurrent
    /// callers get false and MUST skip their critical section. Full
    /// Interlocked fence orders the protected body across threads.</summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    /// <summary>Releases the gate. Idempotent and safe to call from the same
    /// thread that entered. Volatile write publishes the protected body's
    /// effects before the next entrant acquires.</summary>
    public void Exit() => Volatile.Write(ref _busy, 0);
}
