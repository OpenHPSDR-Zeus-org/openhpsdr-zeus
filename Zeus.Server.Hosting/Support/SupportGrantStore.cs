// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Collections.Concurrent;

namespace Zeus.Server.Hosting.Support;

/// <summary>
/// The radio's local authority for read-only support sessions. Holds the
/// short-lived, single-use <see cref="SupportGrant"/>s the operator has approved
/// but not yet redeemed. Deny-by-default: with no matching grant,
/// <see cref="SupportWebRtcService"/> refuses to answer a support offer, so a
/// maintainer session can never open without an explicit operator "Allow".
///
/// Grants are single-use (one approval = one session) and short-TTL (the
/// maintainer must connect promptly after Allow, else re-request), which bounds
/// the window in which an intercepted request id could be replayed. Thread-safe;
/// registered as a singleton.
/// </summary>
public sealed class SupportGrantStore
{
    /// <summary>How long an approved-but-unredeemed grant stays valid.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, SupportGrant> _grants = new(StringComparer.Ordinal);

    public SupportGrantStore(TimeProvider? timeProvider = null, TimeSpan? ttl = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// Record an operator approval for <paramref name="requestId"/>, replacing any
    /// prior unredeemed grant for the same id (a re-approval refreshes the TTL).
    /// Returns the minted grant.
    /// </summary>
    public SupportGrant Approve(string requestId, string adminCallsign)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("requestId required", nameof(requestId));

        var now = _time.GetUtcNow();
        var grant = new SupportGrant(requestId, adminCallsign ?? "", now, now + _ttl);
        _grants[requestId] = grant;
        return grant;
    }

    /// <summary>
    /// Atomically redeem the grant for <paramref name="requestId"/>: removes it and
    /// returns it iff present AND not expired. Single-use — a second call for the
    /// same id returns false. Expired grants are dropped (and reported as absent).
    /// </summary>
    public bool TryConsume(string requestId, out SupportGrant grant)
    {
        grant = null!;
        if (string.IsNullOrEmpty(requestId)) return false;
        if (!_grants.TryRemove(requestId, out var found)) return false;
        if (found.ExpiresAt <= _time.GetUtcNow()) return false; // expired → treat as absent
        grant = found;
        return true;
    }

    /// <summary>Explicitly drop a grant (e.g. the operator revoked or the request was withdrawn).</summary>
    public void Revoke(string requestId)
    {
        if (!string.IsNullOrEmpty(requestId)) _grants.TryRemove(requestId, out _);
    }

    /// <summary>Remove every expired grant. Cheap; safe to call opportunistically.</summary>
    public void PruneExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _grants)
            if (kv.Value.ExpiresAt <= now)
                _grants.TryRemove(kv.Key, out _);
    }

    /// <summary>Count of live (unexpired) grants — for diagnostics/tests only.</summary>
    public int LiveCount
    {
        get
        {
            PruneExpired();
            return _grants.Count;
        }
    }
}
