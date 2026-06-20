// SPDX-License-Identifier: GPL-2.0-or-later
using System.Linq;
using Xunit;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

public class DiagnosticLogBufferTests
{
    [Fact]
    public void Snapshot_EmptyBuffer_ReturnsEmpty()
    {
        var buf = new DiagnosticLogBuffer();
        Assert.Empty(buf.Snapshot());
    }

    [Fact]
    public void AddThenSnapshot_ReturnsOldestFirst()
    {
        var buf = new DiagnosticLogBuffer();
        buf.Add("one");
        buf.Add("two");
        buf.Add("three");

        Assert.Equal(new[] { "one", "two", "three" }, buf.Snapshot().ToArray());
    }

    [Fact]
    public void Snapshot_MaxLines_ReturnsLastN_OldestFirst()
    {
        var buf = new DiagnosticLogBuffer();
        buf.Add("a");
        buf.Add("b");
        buf.Add("c");
        buf.Add("d");

        Assert.Equal(new[] { "c", "d" }, buf.Snapshot(2).ToArray());
    }

    [Fact]
    public void Snapshot_MaxLinesLargerThanCount_ReturnsAll()
    {
        var buf = new DiagnosticLogBuffer();
        buf.Add("a");
        buf.Add("b");

        Assert.Equal(new[] { "a", "b" }, buf.Snapshot(100).ToArray());
    }

    [Fact]
    public void Snapshot_NonPositiveMaxLines_ReturnsEmpty()
    {
        var buf = new DiagnosticLogBuffer();
        buf.Add("a");

        Assert.Empty(buf.Snapshot(0));
        Assert.Empty(buf.Snapshot(-5));
    }

    [Fact]
    public void WrapAround_PastCapacity_KeepsOnlyNewest_InOrder()
    {
        var buf = new DiagnosticLogBuffer();
        int total = DiagnosticLogBuffer.Capacity + 50;
        for (int i = 0; i < total; i++)
            buf.Add("line-" + i);

        // Ask for the whole ring.
        var snap = buf.Snapshot(DiagnosticLogBuffer.Capacity);

        Assert.Equal(DiagnosticLogBuffer.Capacity, snap.Count);
        // Oldest retained is the (total - Capacity)th line; newest is the last.
        Assert.Equal("line-" + (total - DiagnosticLogBuffer.Capacity), snap[0]);
        Assert.Equal("line-" + (total - 1), snap[^1]);

        // Strictly increasing / contiguous across the wrap point.
        for (int i = 0; i < snap.Count; i++)
            Assert.Equal("line-" + (total - DiagnosticLogBuffer.Capacity + i), snap[i]);
    }

    [Fact]
    public void WrapAround_TailRequest_ReturnsNewestN()
    {
        var buf = new DiagnosticLogBuffer();
        int total = DiagnosticLogBuffer.Capacity + 200;
        for (int i = 0; i < total; i++)
            buf.Add("line-" + i);

        var snap = buf.Snapshot(10);
        Assert.Equal(10, snap.Count);
        Assert.Equal("line-" + (total - 10), snap[0]);
        Assert.Equal("line-" + (total - 1), snap[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Add_NullOrEmpty_IsIgnored(string? line)
    {
        var buf = new DiagnosticLogBuffer();
        buf.Add("real");
        buf.Add(line!);
        buf.Add("also-real");

        Assert.Equal(new[] { "real", "also-real" }, buf.Snapshot().ToArray());
    }
}
