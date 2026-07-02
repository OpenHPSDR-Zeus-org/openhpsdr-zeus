// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Pins the pure-logic pieces that drive Zeus's FreeDV Reporter rx_report emit
/// (<see cref="FreeDvReporterService.ShouldEmitRxReport"/> and
/// <see cref="FreeDvReporterService.ExtractCallsign"/>) — the cadence guard
/// (immediate emit on the sync edge, throttled after) and the callsign
/// extractor that turns the FreeDV txt sidechannel into a sanitized callsign
/// field for the reporter. No sockets, no modem, no clock — deterministic.
/// </summary>
public sealed class FreeDvReporterRxReportTests
{
    private static readonly TimeSpan TenSec = TimeSpan.FromSeconds(10);
    private static readonly DateTime T0 = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldEmitRxReport_EmitsImmediatelyOnSyncEdge()
    {
        // A fresh sync (prevSynced=false) must emit right away regardless of
        // how recently the last emit went out — otherwise a brief drop-and-
        // reacquire would show as a 10 s hole on the public map.
        Assert.True(FreeDvReporterService.ShouldEmitRxReport(
            prevSynced: false, nowUtc: T0, lastEmitUtc: T0, interval: TenSec));
        Assert.True(FreeDvReporterService.ShouldEmitRxReport(
            prevSynced: false, nowUtc: T0.AddSeconds(1), lastEmitUtc: T0, interval: TenSec));
    }

    [Fact]
    public void ShouldEmitRxReport_ThrottlesWhileStillSynced()
    {
        // Same sync, well before the interval — hold.
        Assert.False(FreeDvReporterService.ShouldEmitRxReport(
            prevSynced: true, nowUtc: T0.AddSeconds(1), lastEmitUtc: T0, interval: TenSec));
        // Exactly at the interval boundary — emit.
        Assert.True(FreeDvReporterService.ShouldEmitRxReport(
            prevSynced: true, nowUtc: T0.AddSeconds(10), lastEmitUtc: T0, interval: TenSec));
        // Past the interval — emit.
        Assert.True(FreeDvReporterService.ShouldEmitRxReport(
            prevSynced: true, nowUtc: T0.AddSeconds(11), lastEmitUtc: T0, interval: TenSec));
    }

    [Fact]
    public void ExtractCallsign_ReturnsNullWhenTextEmpty()
    {
        Assert.Null(FreeDvReporterService.ExtractCallsign(null));
        Assert.Null(FreeDvReporterService.ExtractCallsign(""));
        Assert.Null(FreeDvReporterService.ExtractCallsign("   "));
    }

    [Theory]
    [InlineData("N0CALL", "N0CALL")]
    [InlineData("kb2uka", "KB2UKA")]           // upper-case normalized
    [InlineData("hello N0CALL de", "N0CALL")]  // first callsign-shaped token wins
    [InlineData("N0CALL/P", "N0CALL/P")]       // portable suffix
    [InlineData("VE7/W1AW", "VE7/W1AW")]       // portable prefix
    public void ExtractCallsign_PicksFirstCallsignShapedToken(string rx, string expected)
    {
        Assert.Equal(expected, FreeDvReporterService.ExtractCallsign(rx));
    }

    [Theory]
    [InlineData("hi there")]        // no digit
    [InlineData("!!!!")]             // no letter
    [InlineData("12345")]            // no letter
    [InlineData("ab")]               // too short
    [InlineData("abcdefghijk")]      // too long (11)
    [InlineData("!@#$%^")]           // invalid chars
    public void ExtractCallsign_ReturnsNullWhenNothingLooksLikeACallsign(string rx)
    {
        Assert.Null(FreeDvReporterService.ExtractCallsign(rx));
    }
}
