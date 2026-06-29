// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Guards the FreeDV auto-detect TX-pause gate. The scanner must keep cycling
/// submodes while receiving, and pause only briefly around real transmit blocks.
/// A regression here (integer overflow on the never-transmitted sentinel) silently
/// disabled scanning entirely, so the sentinel case is pinned explicitly.
/// </summary>
public sealed class FreeDvServiceTxGateTests
{
    private const long Quiet = 400;

    [Fact]
    public void NeverTransmitted_IsNotActive()
    {
        // The sentinel must read as "not transmitting" no matter how large `now`
        // is — the bug was `now - long.MinValue` overflowing to a negative value
        // that compared < quiet and pinned scanning off forever.
        Assert.False(FreeDvService.IsTxActive(0, long.MinValue, Quiet));
        Assert.False(FreeDvService.IsTxActive(1_000_000, long.MinValue, Quiet));
        Assert.False(FreeDvService.IsTxActive(long.MaxValue, long.MinValue, Quiet));
    }

    [Fact]
    public void WithinQuietWindow_IsActive()
    {
        Assert.True(FreeDvService.IsTxActive(nowMs: 10_000, lastTxMs: 9_900, quietMs: Quiet));   // 100 ms ago
        Assert.True(FreeDvService.IsTxActive(nowMs: 10_000, lastTxMs: 10_000, quietMs: Quiet));  // this tick
    }

    [Fact]
    public void PastQuietWindow_IsNotActive()
    {
        Assert.False(FreeDvService.IsTxActive(nowMs: 10_000, lastTxMs: 9_600, quietMs: Quiet));  // exactly quiet → not active
        Assert.False(FreeDvService.IsTxActive(nowMs: 10_000, lastTxMs: 9_000, quietMs: Quiet));  // 1 s ago
    }
}
