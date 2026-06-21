// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Collections.Concurrent;

namespace Zeus.Server.Tci;

/// <summary>
/// Rate-limits high-frequency events (VFO/DDS changes) per Thetis pattern.
/// Coalesces rapid updates into a single broadcast at configured intervals
/// to avoid flooding clients during tuning drags (which can fire hundreds
/// of events per second).
/// </summary>
public sealed class TciRateLimiter : IDisposable
{
    private readonly int _intervalMs;
    private readonly Timer _timer;
    private readonly ConcurrentDictionary<string, string> _pending = new();
    private readonly Action<string> _send;

    /// <summary>
    /// Create a rate limiter that flushes pending events every intervalMs.
    /// </summary>
    /// <param name="intervalMs">Flush cadence in milliseconds (e.g., 50 ms = 20 Hz)</param>
    /// <param name="send">Callback to send a coalesced event string</param>
    public TciRateLimiter(int intervalMs, Action<string> send)
    {
        _intervalMs = intervalMs;
        _send = send;
        _timer = new Timer(OnTick, null, intervalMs, intervalMs);
    }

    /// <summary>
    /// Enqueue an event for rate-limited broadcast. If the same key is enqueued
    /// multiple times before the next flush, only the latest value is sent.
    /// </summary>
    /// <param name="key">Deduplication key (e.g., "vfo:0,0" for RX0 VFO-A)</param>
    /// <param name="commandLine">Full TCI command string to send (e.g., "vfo:0,0,14074000;")</param>
    public void Enqueue(string key, string commandLine)
    {
        _pending[key] = commandLine;
    }

    /// <summary>
    /// Immediately flush all pending events and reset the timer. Use when the
    /// rate-limited path needs to be drained synchronously (e.g., on disconnect).
    /// </summary>
    public void FlushNow()
    {
        OnTick(null);
    }

    private void OnTick(object? state)
    {
        if (_pending.IsEmpty) return;

        // Snapshot and clear atomically
        var toSend = _pending.ToArray();
        foreach (var kvp in toSend)
        {
            _pending.TryRemove(kvp.Key, out _);
        }

        // Send outside the lock
        foreach (var kvp in toSend)
        {
            try
            {
                _send(kvp.Value);
            }
            catch
            {
                // Don't let a send failure stop other events from flushing
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
