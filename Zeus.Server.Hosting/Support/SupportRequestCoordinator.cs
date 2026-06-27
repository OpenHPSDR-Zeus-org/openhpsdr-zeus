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
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Support;

/// <summary>A maintainer support request awaiting the operator's Allow/Deny.</summary>
/// <param name="RequestId">Opaque id minted by the broker, correlates the whole flow.</param>
/// <param name="AdminCallsign">The maintainer asking (verified admin at the broker).</param>
/// <param name="CreatedAt">When the request arrived.</param>
/// <param name="ExpiresAt">When the prompt goes stale (auto-dismiss; the maintainer must re-ask).</param>
public sealed record PendingSupportRequest(
    string RequestId,
    string AdminCallsign,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Orchestrates the request → Allow/Deny flow for read-only maintainer support
/// sessions. The broker relays an admin's request in (<see cref="RegisterRequest"/>);
/// this raises <see cref="Requested"/> so the operator UI (P5) can prompt. The
/// operator's decision arrives via the local <c>/api/support/*</c> endpoints:
///   • <see cref="Approve"/> mints a single-use <see cref="SupportGrant"/> in the
///     <see cref="SupportGrantStore"/> and raises <see cref="Approved"/> (the broker
///     client signals the waiting admin to send its WebRTC offer).
///   • <see cref="Deny"/> drops the request and raises <see cref="Denied"/>.
///
/// Nothing is ever auto-approved: with no operator Approve, no grant is minted and
/// <see cref="SupportWebRtcService"/> refuses the offer. Pending requests auto-expire
/// so an unanswered prompt cannot linger. Thread-safe; registered as a singleton.
/// </summary>
public sealed class SupportRequestCoordinator
{
    /// <summary>How long an unanswered request stays actionable before it auto-expires.</summary>
    public static readonly TimeSpan DefaultPendingTtl = TimeSpan.FromSeconds(90);

    private readonly SupportGrantStore _grants;
    private readonly TimeProvider _time;
    private readonly TimeSpan _pendingTtl;
    private readonly ILogger<SupportRequestCoordinator> _log;
    private readonly ConcurrentDictionary<string, PendingSupportRequest> _pending = new(StringComparer.Ordinal);

    public SupportRequestCoordinator(
        SupportGrantStore grants,
        ILogger<SupportRequestCoordinator> log,
        TimeProvider? timeProvider = null,
        TimeSpan? pendingTtl = null)
    {
        _grants = grants;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _pendingTtl = pendingTtl ?? DefaultPendingTtl;
    }

    /// <summary>Raised when a new request arrives (the operator UI shows an Allow/Deny prompt).</summary>
    public event Action<PendingSupportRequest>? Requested;

    /// <summary>Raised when the operator approves — carries the minted grant (the broker client signals the admin).</summary>
    public event Action<SupportGrant>? Approved;

    /// <summary>Raised when the operator denies (or a request is withdrawn) — carries the request id.</summary>
    public event Action<string>? Denied;

    /// <summary>
    /// Record an inbound maintainer request and prompt the operator. Returns false
    /// (and re-raises nothing) for a duplicate id already pending, or for missing
    /// input. The decision is the operator's — this only surfaces the ask.
    /// </summary>
    public bool RegisterRequest(string requestId, string adminCallsign)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return false;
        PruneExpired();

        var now = _time.GetUtcNow();
        var req = new PendingSupportRequest(requestId, adminCallsign ?? "", now, now + _pendingTtl);
        if (!_pending.TryAdd(requestId, req)) return false; // already pending — don't double-prompt

        _log.LogInformation("support.request from {Admin} (id {RequestId}) — awaiting operator decision",
            req.AdminCallsign, requestId);
        Raise(Requested, req);
        return true;
    }

    /// <summary>
    /// Operator approves <paramref name="requestId"/>: mint a single-use grant and
    /// signal it. Returns false if no such request is pending (or it expired).
    /// </summary>
    public bool Approve(string requestId)
    {
        if (string.IsNullOrEmpty(requestId)) return false;
        if (!_pending.TryRemove(requestId, out var req)) return false;
        if (req.ExpiresAt <= _time.GetUtcNow())
        {
            _log.LogInformation("support.request {RequestId} expired before approval", requestId);
            return false;
        }

        var grant = _grants.Approve(requestId, req.AdminCallsign);
        _log.LogInformation("support.request {RequestId} APPROVED for {Admin}", requestId, req.AdminCallsign);
        Raise(Approved, grant);
        return true;
    }

    /// <summary>Operator denies (or withdraws) <paramref name="requestId"/>. Returns whether it was pending.</summary>
    public bool Deny(string requestId)
    {
        if (string.IsNullOrEmpty(requestId)) return false;
        bool was = _pending.TryRemove(requestId, out _);
        _grants.Revoke(requestId); // belt-and-braces: drop any grant if one slipped in
        if (was)
        {
            _log.LogInformation("support.request {RequestId} DENIED", requestId);
            Raise(Denied, requestId);
        }
        return was;
    }

    /// <summary>Live (unexpired) pending requests, newest first — for the operator UI / local endpoint.</summary>
    public IReadOnlyList<PendingSupportRequest> Pending()
    {
        PruneExpired();
        var list = new List<PendingSupportRequest>(_pending.Values);
        list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return list;
    }

    /// <summary>Drop expired pending requests (and raise <see cref="Denied"/> for each, so UIs can dismiss).</summary>
    public void PruneExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _pending)
        {
            if (kv.Value.ExpiresAt <= now && _pending.TryRemove(kv.Key, out _))
                Raise(Denied, kv.Key);
        }
    }

    private void Raise<T>(Action<T>? handler, T arg)
    {
        if (handler is null) return;
        try { handler(arg); }
        catch (Exception ex) { _log.LogWarning(ex, "support.coordinator subscriber threw"); }
    }
}
