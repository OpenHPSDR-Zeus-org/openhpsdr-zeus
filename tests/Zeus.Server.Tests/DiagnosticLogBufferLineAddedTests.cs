// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

/// <summary>
/// The live-tail hook a maintainer support session's "log" channel rides on:
/// every appended line is fanned to subscribers, and a misbehaving subscriber can
/// never break logging.
/// </summary>
public sealed class DiagnosticLogBufferLineAddedTests
{
    [Fact]
    public void LineAdded_FiresWithAppendedLine()
    {
        var buffer = new DiagnosticLogBuffer();
        var seen = new List<string>();
        buffer.LineAdded += seen.Add;

        buffer.Add("12:00:00.000 INFO RadioService hello");
        buffer.Add("12:00:01.000 WARN Foo bar");

        Assert.Equal(
            new[] { "12:00:00.000 INFO RadioService hello", "12:00:01.000 WARN Foo bar" },
            seen);
    }

    [Fact]
    public void EmptyLine_DoesNotFire()
    {
        var buffer = new DiagnosticLogBuffer();
        int count = 0;
        buffer.LineAdded += _ => count++;

        buffer.Add("");
        buffer.Add(null!);

        Assert.Equal(0, count);
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotBreakLogging()
    {
        var buffer = new DiagnosticLogBuffer();
        buffer.LineAdded += _ => throw new InvalidOperationException("subscriber boom");

        // Add must not propagate the subscriber's exception…
        var ex = Record.Exception(() => buffer.Add("still logged"));
        Assert.Null(ex);

        // …and the line still lands in the ring.
        Assert.Contains("still logged", buffer.Snapshot(10));
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var buffer = new DiagnosticLogBuffer();
        var seen = new List<string>();
        void Handler(string l) => seen.Add(l);

        buffer.LineAdded += Handler;
        buffer.Add("one");
        buffer.LineAdded -= Handler;
        buffer.Add("two");

        Assert.Equal(new[] { "one" }, seen);
    }
}
