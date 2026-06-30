// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.VirtualRadio.Observation;

/// <summary>
/// Bounded ring of the most-recent decoded host commands, surfaced in the
/// <c>/status</c> snapshot and the 1 Hz structured log. Thread-safe: written
/// from the decode path, read from the status path.
/// </summary>
public sealed class CommandLog
{
    private readonly int _capacity;
    private readonly object _gate = new();
    // FIFO of retained commands. We cap the queue at _capacity and drop the
    // oldest on overflow, so Snapshot() yields oldest-first newest-last.
    private readonly Queue<DecodedHostCommand> _items;

    /// <summary>Create a log holding at most <paramref name="capacity"/> commands.</summary>
    public CommandLog(int capacity = 64)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        _capacity = capacity;
        _items = new Queue<DecodedHostCommand>(capacity);
    }

    /// <summary>The configured ring capacity.</summary>
    public int Capacity => _capacity;

    /// <summary>Append a decoded command, evicting the oldest past capacity.</summary>
    public void Add(DecodedHostCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            _items.Enqueue(command);
            while (_items.Count > _capacity)
                _items.Dequeue();
        }
    }

    /// <summary>Return the retained commands, oldest first.</summary>
    public IReadOnlyList<DecodedHostCommand> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }
}
