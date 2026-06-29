// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Server.Hosting.Support;

namespace Zeus.Server.Tests;

public sealed class SupportGrantStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Approve_ThenConsume_ReturnsGrant_AndIsSingleUse()
    {
        var store = new SupportGrantStore();
        var minted = store.Approve("req-1", "KB2UKA");
        Assert.Equal("req-1", minted.RequestId);
        Assert.Equal("KB2UKA", minted.AdminCallsign);

        Assert.True(store.TryConsume("req-1", out var got));
        Assert.Equal("req-1", got.RequestId);

        // Single-use: a replayed offer for the same id is refused.
        Assert.False(store.TryConsume("req-1", out _));
    }

    [Fact]
    public void Consume_UnknownId_ReturnsFalse()
    {
        var store = new SupportGrantStore();
        Assert.False(store.TryConsume("nope", out _));
    }

    [Fact]
    public void Grant_ExpiresAfterTtl()
    {
        var clock = new TestClock(T0);
        var store = new SupportGrantStore(clock, TimeSpan.FromMinutes(2));
        store.Approve("req-1", "KB2UKA");

        clock.Advance(TimeSpan.FromMinutes(3)); // past TTL
        Assert.False(store.TryConsume("req-1", out _));
        Assert.Equal(0, store.LiveCount);
    }

    [Fact]
    public void Grant_JustBeforeExpiry_StillConsumable()
    {
        var clock = new TestClock(T0);
        var store = new SupportGrantStore(clock, TimeSpan.FromMinutes(2));
        store.Approve("req-1", "KB2UKA");

        clock.Advance(TimeSpan.FromMinutes(1)); // still inside TTL
        Assert.True(store.TryConsume("req-1", out _));
    }

    [Fact]
    public void Revoke_RemovesGrant()
    {
        var store = new SupportGrantStore();
        store.Approve("req-1", "KB2UKA");
        store.Revoke("req-1");
        Assert.False(store.TryConsume("req-1", out _));
    }

    [Fact]
    public void Reapprove_RefreshesTtl()
    {
        var clock = new TestClock(T0);
        var store = new SupportGrantStore(clock, TimeSpan.FromMinutes(2));
        store.Approve("req-1", "KB2UKA");
        clock.Advance(TimeSpan.FromMinutes(1.5));
        store.Approve("req-1", "KB2UKA"); // refresh
        clock.Advance(TimeSpan.FromMinutes(1)); // 1 min after refresh, < TTL
        Assert.True(store.TryConsume("req-1", out _));
    }

    [Fact]
    public void Approve_EmptyId_Throws()
    {
        var store = new SupportGrantStore();
        Assert.Throws<ArgumentException>(() => store.Approve("", "KB2UKA"));
    }

    /// <summary>A manually-advanced <see cref="TimeProvider"/> for deterministic TTL tests.</summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }
}
