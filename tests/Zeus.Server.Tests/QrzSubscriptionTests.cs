// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Pins QrzService.IsActiveSubscription: HasXmlSubscription must reflect QRZ's
// authoritative <SubExp> Session field, NOT lookup success. QRZ returns basic
// callsign data to non-subscribers as well, so the old "self-lookup returned a
// record → subscribed" heuristic falsely reported active subscriptions. Only an
// explicit, unexpired SubExp date counts as active; "non-subscriber" does not.

using Zeus.Server;

namespace Zeus.Server.Tests;

public class QrzSubscriptionTests
{
    [Fact]
    public void NonSubscriber_IsNotActive()
    {
        Assert.False(QrzService.IsActiveSubscription("non-subscriber"));
        Assert.False(QrzService.IsActiveSubscription("Non-Subscriber"));
        Assert.False(QrzService.IsActiveSubscription("  non-subscriber  "));
    }

    [Fact]
    public void MissingOrEmpty_IsNotActive()
    {
        Assert.False(QrzService.IsActiveSubscription(null));
        Assert.False(QrzService.IsActiveSubscription(""));
        Assert.False(QrzService.IsActiveSubscription("   "));
    }

    [Fact]
    public void FutureExpiryDate_IsActive()
    {
        // QRZ's documented SubExp format for active subscribers (weekday must
        // match the date or .NET's parser rejects it — Dec 31 2099 is a Thursday).
        Assert.True(QrzService.IsActiveSubscription("Thu Dec 31 23:59:59 2099"));
    }

    [Fact]
    public void SpacePaddedDay_IsParsed()
    {
        // asctime space-pads single-digit days: "Mon Jan  1 ...". Jan 1 2099 is a Thursday.
        Assert.True(QrzService.IsActiveSubscription("Thu Jan  1 00:00:00 2099"));
    }

    [Fact]
    public void PastExpiryDate_IsNotActive()
    {
        // Dec 31 2000 was a Sunday.
        Assert.False(QrzService.IsActiveSubscription("Sun Dec 31 23:59:59 2000"));
    }
}
