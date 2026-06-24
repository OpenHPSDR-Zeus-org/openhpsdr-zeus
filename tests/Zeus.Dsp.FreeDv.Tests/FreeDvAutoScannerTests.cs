// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;
using Zeus.Dsp.FreeDv;

namespace Zeus.Dsp.FreeDv.Tests;

/// <summary>
/// Deterministic tests for the auto submode scanner. It owns no clock, so we
/// drive it with explicit timestamps — no native modem, no real time, no flakiness.
/// </summary>
public class FreeDvAutoScannerTests
{
    [Fact]
    public void Order_StartsWith700D_AndCoversAllFiveModes()
    {
        var s = new FreeDvAutoScanner();
        Assert.Equal(5, s.Order.Count);
        Assert.Equal(FreeDvSubmode.Mode700D, s.Order[0]);
        Assert.Contains(FreeDvSubmode.Mode700E, s.Order);
        Assert.Contains(FreeDvSubmode.Mode700C, s.Order);
        Assert.Contains(FreeDvSubmode.Mode1600, s.Order);
        Assert.Contains(FreeDvSubmode.Mode800XA, s.Order);
    }

    [Fact]
    public void Unsynced_HoldsForDwell_ThenAdvancesInOrder()
    {
        var s = new FreeDvAutoScanner(dwellMs: 1000, reacquireMs: 2000);
        Assert.Null(s.Tick(0, synced: false, FreeDvSubmode.Mode700D));   // seeds timebase
        Assert.Null(s.Tick(999, synced: false, FreeDvSubmode.Mode700D)); // still dwelling
        Assert.Equal(FreeDvSubmode.Mode700E, s.Tick(1000, synced: false, FreeDvSubmode.Mode700D));
        // Caller applies 700E; dwell restarts at 1000.
        Assert.Null(s.Tick(1999, synced: false, FreeDvSubmode.Mode700E));
        Assert.Equal(FreeDvSubmode.Mode700C, s.Tick(2000, synced: false, FreeDvSubmode.Mode700E));
    }

    [Fact]
    public void Advance_WrapsPastLastMode()
    {
        var s = new FreeDvAutoScanner(dwellMs: 1000, reacquireMs: 2000);
        s.Tick(0, synced: false, FreeDvSubmode.Mode800XA);
        Assert.Equal(FreeDvSubmode.Mode700D, s.Tick(1000, synced: false, FreeDvSubmode.Mode800XA));
    }

    [Fact]
    public void Synced_HoldsIndefinitely_NeverAdvances()
    {
        var s = new FreeDvAutoScanner(dwellMs: 1000, reacquireMs: 2000);
        Assert.Null(s.Tick(0, synced: true, FreeDvSubmode.Mode700D));
        // Far past any dwell, but locked — must not move.
        Assert.Null(s.Tick(100_000, synced: true, FreeDvSubmode.Mode700D));
    }

    [Fact]
    public void AfterLock_LosesSync_HoldsThroughReacquireGrace_ThenResumes()
    {
        var s = new FreeDvAutoScanner(dwellMs: 1000, reacquireMs: 2000);
        s.Tick(0, synced: true, FreeDvSubmode.Mode700D); // lock
        // Sync drops; within the re-acquire grace the locked mode is held even
        // though the dwell has long since elapsed (covers QSO overs / fades).
        Assert.Null(s.Tick(1999, synced: false, FreeDvSubmode.Mode700D));
        // Grace expired and dwell elapsed — resume scanning from the locked mode.
        Assert.Equal(FreeDvSubmode.Mode700E, s.Tick(2000, synced: false, FreeDvSubmode.Mode700D));
    }

    [Fact]
    public void Reset_ReseedsDwell()
    {
        var s = new FreeDvAutoScanner(dwellMs: 1000, reacquireMs: 2000);
        s.Tick(0, synced: false, FreeDvSubmode.Mode700D);
        Assert.Equal(FreeDvSubmode.Mode700E, s.Tick(1000, synced: false, FreeDvSubmode.Mode700D));
        s.Reset(5000);
        Assert.Null(s.Tick(5000, synced: false, FreeDvSubmode.Mode700E));      // fresh dwell
        Assert.Null(s.Tick(5999, synced: false, FreeDvSubmode.Mode700E));
        Assert.Equal(FreeDvSubmode.Mode700C, s.Tick(6000, synced: false, FreeDvSubmode.Mode700E));
    }
}
