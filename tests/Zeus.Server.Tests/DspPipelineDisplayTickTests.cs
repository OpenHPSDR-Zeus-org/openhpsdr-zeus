// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Pins the inline display-tick throttle (#960 G2E bench follow-up). The
/// display cadence is paced by whichever RX-thread frames arrive — user-RX IQ
/// frames normally, and PS-feedback frames during a single-ADC (HermesC10 /
/// ANAN-G2E) keyed PureSignal burst, when the board's only DDC is diverted to
/// feedback and no IQ frame arrives at all. Both producers funnel through
/// <see cref="DspPipelineService.ShouldTickInline"/>, so this predicate is
/// what guarantees (a) the panadapter/waterfall keep updating during a G2E PS
/// transmission instead of freezing, and (b) dual-ADC boards — where IQ and
/// feedback frames flow simultaneously — still tick at ~30 Hz, not 60.
/// </summary>
public sealed class DspPipelineDisplayTickTests
{
    private const long Period = 400; // arbitrary units; contract is relative

    [Fact]
    public void FirstCall_AlwaysTicks()
    {
        // last == 0 is the "never ticked" sentinel — the first frame after
        // sink attach must render immediately, whatever the clock reads.
        Assert.True(DspPipelineService.ShouldTickInline(
            nowTicks: 12345, lastTicks: 0, periodTicks: Period));
    }

    [Fact]
    public void WithinPeriod_DoesNotTick()
    {
        // A second producer (e.g. a PS-feedback frame landing right after an
        // IQ frame on a dual-ADC board) must not double the display rate.
        Assert.False(DspPipelineService.ShouldTickInline(
            nowTicks: 1000 + Period - 1, lastTicks: 1000, periodTicks: Period));
    }

    [Fact]
    public void AtOrPastPeriod_Ticks()
    {
        Assert.True(DspPipelineService.ShouldTickInline(
            nowTicks: 1000 + Period, lastTicks: 1000, periodTicks: Period));
        Assert.True(DspPipelineService.ShouldTickInline(
            nowTicks: 1000 + Period + 1, lastTicks: 1000, periodTicks: Period));
    }

    [Fact]
    public void SoleProducer_TicksEveryPeriod_LikeTheG2eBurst()
    {
        // Simulate the G2E keyed-burst regime: PS-feedback frames are the ONLY
        // pacer, arriving faster than the display period. Exactly one tick per
        // period must fire — a live display, throttled to cadence.
        long last = 0;
        int ticks = 0;
        for (long now = 100; now <= 100 + Period * 10; now += Period / 4)
        {
            if (DspPipelineService.ShouldTickInline(now, last, Period))
            {
                last = now;
                ticks++;
            }
        }

        Assert.Equal(11, ticks); // first immediate tick + one per full period
    }
}
