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

namespace Zeus.Server.Tci;

/// <summary>
/// In-memory storage for DX cluster spots received via TCI or a native Telnet
/// DX-cluster connection. Fires <see cref="SpotsChanged"/> after every mutation
/// so SpotBroadcastService can push a fresh snapshot to all WS clients.
///
/// <para>The store is bounded (LRU): once <see cref="MaxSpots"/> distinct
/// callsigns are held, adding a new one evicts the least-recently-spotted call.
/// Re-spotting an existing call refreshes its recency. This matters for the
/// native DX-cluster path, where a busy/contest cluster is a firehose of unique
/// callsigns that would otherwise accumulate without bound for the whole
/// session (the TCI path self-expires via RemoveSpot, so it never hits the
/// cap). The bound also caps the per-broadcast snapshot size.</para>
/// </summary>
public sealed class SpotManager
{
    /// <summary>Default upper bound on retained distinct callsigns.</summary>
    public const int DefaultMaxSpots = 1000;

    private readonly object _sync = new();
    // LRU ordering: head = least-recently-spotted, tail = most-recent. The
    // dictionary indexes nodes by callsign for O(1) add/update/remove.
    private readonly LinkedList<Spot> _order = new();
    private readonly Dictionary<string, LinkedListNode<Spot>> _index = new();

    /// <summary>Maximum number of distinct callsigns retained before the
    /// least-recently-spotted is evicted.</summary>
    public int MaxSpots { get; }

    public SpotManager() : this(DefaultMaxSpots) { }

    public SpotManager(int maxSpots)
    {
        MaxSpots = maxSpots > 0 ? maxSpots : DefaultMaxSpots;
    }

    /// <summary>
    /// Raised after any mutation (add, remove, or clear). Fired while the
    /// lock is NOT held so subscribers can call <see cref="GetAll"/> safely.
    /// The event fires on whatever thread called the mutating method.
    /// </summary>
    public event Action? SpotsChanged;

    /// <summary>
    /// Add or update a spot. Adding a brand-new callsign past <see cref="MaxSpots"/>
    /// evicts the least-recently-spotted one. Updating an existing callsign
    /// refreshes its value and its recency.
    /// </summary>
    public void AddSpot(string callsign, string mode, long freqHz, uint argb, string? comment = null)
    {
        lock (_sync)
        {
            var spot = new Spot(callsign, mode, freqHz, argb, comment);
            if (_index.TryGetValue(callsign, out var existing))
            {
                // Update in place and move to the MRU end.
                existing.Value = spot;
                _order.Remove(existing);
                _order.AddLast(existing);
            }
            else
            {
                _index[callsign] = _order.AddLast(spot);
                if (_index.Count > MaxSpots)
                {
                    var oldest = _order.First!; // count > cap >= 1 ⇒ non-null
                    _order.RemoveFirst();
                    _index.Remove(oldest.Value.Callsign);
                }
            }
        }
        SpotsChanged?.Invoke();
    }

    /// <summary>
    /// Remove a spot by callsign.
    /// </summary>
    public void RemoveSpot(string callsign)
    {
        lock (_sync)
        {
            if (_index.TryGetValue(callsign, out var node))
            {
                _order.Remove(node);
                _index.Remove(callsign);
            }
        }
        SpotsChanged?.Invoke();
    }

    /// <summary>
    /// Clear all spots.
    /// </summary>
    public void ClearAll()
    {
        lock (_sync)
        {
            _order.Clear();
            _index.Clear();
        }
        SpotsChanged?.Invoke();
    }

    /// <summary>
    /// Get a snapshot of all spots, oldest-spotted first.
    /// </summary>
    public Spot[] GetAll()
    {
        lock (_sync)
        {
            var arr = new Spot[_order.Count];
            int i = 0;
            foreach (var s in _order)
                arr[i++] = s;
            return arr;
        }
    }

    public sealed record Spot(
        string Callsign,
        string Mode,
        long FreqHz,
        uint Argb,
        string? Comment);
}
