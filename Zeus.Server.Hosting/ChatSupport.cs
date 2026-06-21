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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Bounded in-memory chat message history. Keeps at most <c>capacity</c>
/// messages, evicting the oldest. Thread-safe. Factored out of
/// <see cref="ChatService"/> so the cap behaviour is unit-testable in isolation.
/// </summary>
public sealed class ChatMessageRing
{
    private readonly int _capacity;
    private readonly Queue<ChatMessage> _items;
    private readonly object _sync = new();

    public ChatMessageRing(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _items = new Queue<ChatMessage>(capacity);
    }

    public int Count
    {
        get { lock (_sync) return _items.Count; }
    }

    public void Add(ChatMessage message)
    {
        lock (_sync)
        {
            _items.Enqueue(message);
            while (_items.Count > _capacity) _items.Dequeue();
        }
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> of the most recent messages in
    /// chronological (oldest-first) order. A non-positive limit returns all.
    /// </summary>
    public IReadOnlyList<ChatMessage> Snapshot(int limit)
    {
        lock (_sync)
        {
            if (limit <= 0 || limit >= _items.Count)
                return _items.ToArray();
            return _items.Skip(_items.Count - limit).ToArray();
        }
    }
}

/// <summary>
/// Presence-update throttle for the relay. Coalesces frequency/mode/status
/// changes so the relay sees at most one presence frame per
/// <c>window</c>, but never suppresses a genuine change beyond the window.
/// Factored out of <see cref="ChatService"/> for unit testing.
///
/// <para>Usage: event handlers call <see cref="Offer"/> with the latest
/// presence; it returns true when the worker should wake. The worker calls
/// <see cref="TryTake"/> on its tick; it yields a frame only when presence
/// differs from the last sent value AND the window has elapsed.</para>
/// </summary>
public sealed class PresenceThrottle
{
    public readonly record struct Presence(long FreqHz, string Mode, string Status);

    private readonly TimeSpan _window;
    private readonly object _sync = new();

    private Presence _lastSent;
    private bool _hasSent;
    private Presence _pending;
    private bool _hasPending;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;

    public PresenceThrottle(TimeSpan window)
    {
        _window = window;
    }

    /// <summary>
    /// Seeds the last-sent presence without emitting (e.g. after a hello frame
    /// already carried it) so the next identical idle tick is suppressed.
    /// </summary>
    public void Seed(long freqHz, string mode, string status)
    {
        lock (_sync)
        {
            _lastSent = new Presence(freqHz, mode, status);
            _hasSent = true;
            _lastSentAt = DateTimeOffset.MinValue; // allow an immediate change to flush
            _hasPending = false;
        }
    }

    /// <summary>
    /// Records the latest presence. Returns true when this represents a change
    /// from the last sent value (so the caller should wake the worker); false
    /// when it's a no-op duplicate.
    /// </summary>
    public bool Offer(long freqHz, string mode, string status)
    {
        var p = new Presence(freqHz, mode, status);
        lock (_sync)
        {
            if (_hasSent && p == _lastSent && !_hasPending)
                return false;
            _pending = p;
            _hasPending = true;
            return true;
        }
    }

    /// <summary>
    /// If a changed presence is pending and the throttle window has elapsed
    /// since the last send, yields it and records it as sent. Otherwise returns
    /// false.
    /// </summary>
    public bool TryTake(DateTimeOffset now, out Presence presence)
    {
        lock (_sync)
        {
            presence = default;
            if (!_hasPending) return false;
            if (_hasSent && _pending == _lastSent)
            {
                _hasPending = false;
                return false;
            }
            if (now - _lastSentAt < _window) return false;

            presence = _pending;
            _lastSent = _pending;
            _hasSent = true;
            _hasPending = false;
            _lastSentAt = now;
            return true;
        }
    }
}
