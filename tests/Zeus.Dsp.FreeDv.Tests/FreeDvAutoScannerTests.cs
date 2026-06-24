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
        var s = new FreeDvAutoScanner(dwellMs: 1000, lockConfirmMs: 1500, unlockMs: 4000);
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
        var s = new FreeDvAutoScanner(dwellMs: 1000, lockConfirmMs: 1500, unlockMs: 4000);
        s.Tick(0, synced: false, FreeDvSubmode.Mode800XA);
        Assert.Equal(FreeDvSubmode.Mode700D, s.Tick(1000, synced: false, FreeDvSubmode.Mode800XA));
    }

    [Fact]
    public void BriefSyncFlicker_DoesNotLock_AndStillAdvances()
    {
        // The marginal-signal chatter case: a sub-lockConfirm sync blip must NOT
        // camp the scanner, and must NOT reset the dwell — the mode still advances.
        var s = new FreeDvAutoScanner(dwellMs: 1000, lockConfirmMs: 1500, unlockMs: 4000);
        Assert.Null(s.Tick(0, synced: false, FreeDvSubmode.Mode700D));
        Assert.Null(s.Tick(400, synced: true, FreeDvSubmode.Mode700D));   // flicker up (300 ms)
        Assert.Null(s.Tick(700, synced: false, FreeDvSubmode.Mode700D));  // back to noise
        Assert.False(s.Locked);
        Assert.Equal(FreeDvSubmode.Mode700E, s.Tick(1000, synced: false, FreeDvSubmode.Mode700D));
    }

    [Fact]
    public void SustainedSync_Locks_AndCampsThroughFadesUntilUnlock()
    {
        var s = new FreeDvAutoScanner(dwellMs: 1000, lockConfirmMs: 1500, unlockMs: 4000);
        Assert.Null(s.Tick(0, synced: true, FreeDvSubmode.Mode700D));
        Assert.Null(s.Tick(1500, synced: true, FreeDvSubmode.Mode700D)); // 1.5 s continuous → lock
        Assert.True(s.Locked);
        // Lose sync; camped mode is held through the unlock window (overs/fades).
        Assert.Null(s.Tick(10_000, synced: false, FreeDvSubmode.Mode700D));
        Assert.True(s.Locked);
        Assert.Null(s.Tick(13_999, synced: false, FreeDvSubmode.Mode700D)); // still within unlock grace
        Assert.True(s.Locked);
        // Continuous loss exceeds unlock → release (this tick just unlocks)…
        Assert.Null(s.Tick(14_000, synced: false, FreeDvSubmode.Mode700D));
        Assert.False(s.Locked);
        // …then the resumed scan advances after a fresh dwell.
        Assert.Equal(FreeDvSubmode.Mode700E, s.Tick(15_000, synced: false, FreeDvSubmode.Mode700D));
    }

    [Fact]
    public void Reset_ReseedsDwell()
    {
        var s = new FreeDvAutoScanner(dwellMs: 1000, lockConfirmMs: 1500, unlockMs: 4000);
        s.Tick(0, synced: false, FreeDvSubmode.Mode700D);
        Assert.Equal(FreeDvSubmode.Mode700E, s.Tick(1000, synced: false, FreeDvSubmode.Mode700D));
        s.Reset(5000);
        Assert.Null(s.Tick(5000, synced: false, FreeDvSubmode.Mode700E));      // fresh dwell
        Assert.Null(s.Tick(5999, synced: false, FreeDvSubmode.Mode700E));
        Assert.Equal(FreeDvSubmode.Mode700C, s.Tick(6000, synced: false, FreeDvSubmode.Mode700E));
    }
}
