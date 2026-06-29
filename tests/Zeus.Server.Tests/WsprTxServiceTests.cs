// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// WsprTxServiceTests — drive the beacon keyer through its internal test
// constructor (no native, no real-time waits, injected clock + RNG). Asserts the
// safety invariants: never key disarmed / unconfigured, txPercent gating with a
// seeded RNG, watchdog auto-disarm, and 3840-byte audio blocks.

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class WsprTxServiceTests
{
    private sealed class Recorder
    {
        public int KeyUps;
        public int KeyDowns;
        public bool Keyed;
        public int Blocks;
        public int LastBlockBytes = -1;

        public bool Key(bool on, out string? error)
        {
            error = null;
            if (on) { KeyUps++; Keyed = true; } else { KeyDowns++; Keyed = false; }
            return true;
        }

        public void Audio(ReadOnlyMemory<byte> block) { Blocks++; LastBlockBytes = block.Length; }
        public void Broadcast(byte[] _) { }
    }

    private static float[] FakeAudio() => new float[960 * 4];

    // A deterministic RNG that replays a fixed sequence, then repeats the last.
    private static Func<double> SeededRandom(params double[] values)
    {
        int i = 0;
        return () => values[Math.Min(i++, values.Length - 1)];
    }

    private static WsprTxService NewService(
        Recorder rec, DateTime now, Func<double> random,
        Func<string, int, float[]?>? renderer = null)
    {
        return new WsprTxService(
            rec.Key,
            rec.Audio,
            rec.Broadcast,
            renderer ?? ((_, _) => FakeAudio()),
            () => now,
            static (_, _) => Task.CompletedTask,
            random,
            NullLogger.Instance);
    }

    private static DateTime AnInstant() => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Disarmed_NeverKeys()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.0));
        svc.SetSettings("KB2UKA", "FN12", 30, 1500, 1.0);
        Assert.False(svc.ShouldKeyForSlot(AnInstant()));
    }

    [Fact]
    public void Armed_NoSettings_NeverKeys()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.0));
        svc.SetArmed(true);
        Assert.False(svc.ShouldKeyForSlot(AnInstant()));
    }

    [Fact]
    public void Armed_FullDuty_AlwaysKeys()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.99));
        svc.SetArmed(true);
        svc.SetSettings("KB2UKA", "FN12", 30, 1500, 1.0);   // txPercent 1.0 → no RNG draw
        Assert.True(svc.ShouldKeyForSlot(AnInstant()));
    }

    [Fact]
    public void Armed_ZeroDuty_NeverKeys()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.0));
        svc.SetArmed(true);
        svc.SetSettings("KB2UKA", "FN12", 30, 1500, 0.0);
        Assert.False(svc.ShouldKeyForSlot(AnInstant()));
    }

    [Fact]
    public void TxPercentGate_HonoursSeededRng()
    {
        var rec = new Recorder();
        // pct 0.5: draw 0.3 (<0.5 → key), then 0.7 (≥0.5 → skip).
        var svc = NewService(rec, AnInstant(), SeededRandom(0.3, 0.7));
        svc.SetArmed(true);
        svc.SetSettings("KB2UKA", "FN12", 30, 1500, 0.5);
        Assert.True(svc.ShouldKeyForSlot(AnInstant()));
        Assert.False(svc.ShouldKeyForSlot(AnInstant()));
    }

    [Fact]
    public void Watchdog_DisarmsAfterWindow()
    {
        var rec = new Recorder();
        var armed = AnInstant();
        var clock = armed;
        var svc = new WsprTxService(
            rec.Key, rec.Audio, rec.Broadcast, (_, _) => FakeAudio(),
            () => clock, static (_, _) => Task.CompletedTask, SeededRandom(0.0), NullLogger.Instance);
        svc.SetArmed(true);
        Assert.True(svc.Armed);

        clock = armed.AddMinutes(WsprTxService.WatchdogMinutes + 1);
        svc.EnforceWatchdog(clock);
        Assert.False(svc.Armed);
    }

    [Fact]
    public async Task Transmit_KeysThenUnkeys_AndEmits3840ByteBlocks()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.0));
        svc.SetArmed(true);
        svc.SetSettings("KB2UKA", "FN12", 30, 1500, 1.0);

        await svc.TransmitBeaconAsync(CancellationToken.None);

        Assert.Equal(1, rec.KeyUps);
        Assert.Equal(1, rec.KeyDowns);
        Assert.False(rec.Keyed);
        Assert.True(rec.Blocks > 0);
        Assert.Equal(3840, rec.LastBlockBytes);
        Assert.False(svc.Transmitting);
    }

    [Fact]
    public async Task RenderFailure_DoesNotKey()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.0), renderer: (_, _) => null);
        svc.SetArmed(true);
        svc.SetSettings("KB2UKA", "FN12", 30, 1500, 1.0);

        await svc.TransmitBeaconAsync(CancellationToken.None);

        Assert.Equal(0, rec.KeyUps);
        Assert.Equal(0, rec.Blocks);
    }

    [Fact]
    public void SetSettings_RejectsEmptyCallOrGrid()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.0));
        Assert.NotNull(svc.SetSettings("", "FN12", 30, 1500, 0.2));
        Assert.NotNull(svc.SetSettings("KB2UKA", "  ", 30, 1500, 0.2));
        Assert.Null(svc.SetSettings("KB2UKA", "FN12", 30, 1500, 0.2));
    }

    [Fact]
    public void SnapWsprDbm_SnapsToNearestCanonicalStep()
    {
        Assert.Equal(0, WsprTxService.SnapWsprDbm(-5));    // clamp low
        Assert.Equal(60, WsprTxService.SnapWsprDbm(100));  // clamp high
        Assert.Equal(30, WsprTxService.SnapWsprDbm(30));   // already canonical
        Assert.Equal(30, WsprTxService.SnapWsprDbm(31));   // 31 -> 30
        Assert.Equal(33, WsprTxService.SnapWsprDbm(32));   // 32 -> 33
        Assert.Equal(3, WsprTxService.SnapWsprDbm(5));     // tie 3/7 -> lower (3)
    }

    [Fact]
    public void SetSettings_SnapsNonCanonicalDbm()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.0));
        // 31 dBm is NOT a valid WSPR power step; the encoder would reject it and the
        // beacon would silently never key. SetSettings must snap it to 30.
        Assert.Null(svc.SetSettings("KB2UKA", "FN12", 31, 1500, 0.2));
        Assert.Equal("KB2UKA FN12 30", svc.Status().Message);
    }

    [Fact]
    public void Status_ReportsWsprModeAndBeaconMessage()
    {
        var rec = new Recorder();
        var svc = NewService(rec, AnInstant(), SeededRandom(0.0));
        svc.SetArmed(true);
        svc.SetSettings("KB2UKA", "FN12", 30, 1400, 0.2);
        var s = svc.Status();
        Assert.Equal("WSPR", s.Mode);
        Assert.Equal("", s.Slot);
        Assert.Equal("KB2UKA FN12 30", s.Message);
        Assert.Equal(1400, s.AudioHz);
        Assert.True(s.Armed);
    }
}
