// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Hosting.Support;

namespace Zeus.Server.Tests;

public sealed class SupportRequestCoordinatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    private static (SupportRequestCoordinator coord, SupportGrantStore grants, TestClock clock) New(
        TimeSpan? pendingTtl = null)
    {
        var clock = new TestClock(T0);
        var grants = new SupportGrantStore(clock, TimeSpan.FromMinutes(2));
        var coord = new SupportRequestCoordinator(
            grants, NullLogger<SupportRequestCoordinator>.Instance, clock, pendingTtl);
        return (coord, grants, clock);
    }

    [Fact]
    public void Register_RaisesRequested_AndIsListed()
    {
        var (coord, _, _) = New();
        PendingSupportRequest? seen = null;
        coord.Requested += r => seen = r;

        Assert.True(coord.RegisterRequest("req-1", "KB2UKA"));
        Assert.NotNull(seen);
        Assert.Equal("req-1", seen!.RequestId);
        Assert.Equal("KB2UKA", seen.AdminCallsign);

        var pending = coord.Pending();
        Assert.Single(pending);
        Assert.Equal("req-1", pending[0].RequestId);
    }

    [Fact]
    public void Register_DuplicateId_IsRejected()
    {
        var (coord, _, _) = New();
        Assert.True(coord.RegisterRequest("req-1", "KB2UKA"));
        Assert.False(coord.RegisterRequest("req-1", "KB2UKA")); // already pending
    }

    [Fact]
    public void Approve_MintsGrant_RaisesApproved_AndClearsPending()
    {
        var (coord, grants, _) = New();
        coord.RegisterRequest("req-1", "KB2UKA");

        SupportGrant? approved = null;
        coord.Approved += g => approved = g;

        Assert.True(coord.Approve("req-1"));
        Assert.NotNull(approved);
        Assert.Equal("req-1", approved!.RequestId);

        // The grant is now consumable exactly once (the offer redeems it).
        Assert.True(grants.TryConsume("req-1", out _));
        // No longer pending.
        Assert.Empty(coord.Pending());
    }

    [Fact]
    public void Approve_UnknownId_ReturnsFalse_AndMintsNothing()
    {
        var (coord, grants, _) = New();
        Assert.False(coord.Approve("ghost"));
        Assert.False(grants.TryConsume("ghost", out _));
    }

    [Fact]
    public void Deny_RemovesPending_RaisesDenied_AndBlocksLaterApprove()
    {
        var (coord, grants, _) = New();
        coord.RegisterRequest("req-1", "KB2UKA");

        string? denied = null;
        coord.Denied += id => denied = id;

        Assert.True(coord.Deny("req-1"));
        Assert.Equal("req-1", denied);
        Assert.Empty(coord.Pending());

        // A denied request cannot subsequently be approved into a grant.
        Assert.False(coord.Approve("req-1"));
        Assert.False(grants.TryConsume("req-1", out _));
    }

    [Fact]
    public void Pending_ExpiresAfterTtl_AndApproveThenFails()
    {
        var (coord, _, clock) = New(TimeSpan.FromSeconds(90));
        coord.RegisterRequest("req-1", "KB2UKA");

        string? denied = null;
        coord.Denied += id => denied = id;

        clock.Advance(TimeSpan.FromSeconds(120)); // past pending TTL
        Assert.Empty(coord.Pending());            // prune drops it…
        Assert.Equal("req-1", denied);            // …and signals a dismiss
        Assert.False(coord.Approve("req-1"));     // too late to approve
    }

    // ---- L1 availability gate (remote-diag P5) -------------------------

    private static (SupportRequestCoordinator coord, SupportAvailabilityStore avail, string dbPath) NewGated(bool available)
    {
        var clock = new TestClock(T0);
        var grants = new SupportGrantStore(clock, TimeSpan.FromMinutes(2));
        var dbPath = Path.Combine(Path.GetTempPath(), $"zeus-support-gate-{Guid.NewGuid():N}.db");
        var avail = new SupportAvailabilityStore(NullLogger<SupportAvailabilityStore>.Instance, dbPath);
        avail.Set(available, autoShareCrashes: false);
        var coord = new SupportRequestCoordinator(
            grants, avail, NullLogger<SupportRequestCoordinator>.Instance, clock);
        return (coord, avail, dbPath);
    }

    private static void Cleanup(string dbPath)
    {
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { /* best effort */ }
        try { if (File.Exists(dbPath + "-log")) File.Delete(dbPath + "-log"); } catch { /* best effort */ }
    }

    [Fact]
    public void RegisterRequest_Refused_WhenUnavailable()
    {
        var (coord, _, dbPath) = NewGated(available: false);
        try
        {
            PendingSupportRequest? seen = null;
            coord.Requested += r => seen = r;

            Assert.False(coord.RegisterRequest("req-1", "KB2UKA")); // gate closed
            Assert.Null(seen);                                      // no prompt surfaced
            Assert.Empty(coord.Pending());                          // nothing parked
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void RegisterRequest_Allowed_WhenAvailable()
    {
        var (coord, _, dbPath) = NewGated(available: true);
        try
        {
            PendingSupportRequest? seen = null;
            coord.Requested += r => seen = r;

            Assert.True(coord.RegisterRequest("req-1", "KB2UKA"));
            Assert.NotNull(seen);
            Assert.Single(coord.Pending());
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void RegisterRequest_TogglingAvailability_GatesLive()
    {
        var (coord, avail, dbPath) = NewGated(available: true);
        try
        {
            Assert.True(coord.RegisterRequest("req-1", "KB2UKA"));

            // Operator turns the master switch OFF: new requests are refused.
            avail.Set(available: false, autoShareCrashes: false);
            Assert.False(coord.RegisterRequest("req-2", "KB2UKA"));
        }
        finally { Cleanup(dbPath); }
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }
}
