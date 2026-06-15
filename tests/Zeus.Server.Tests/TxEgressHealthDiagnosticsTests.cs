// SPDX-License-Identifier: GPL-2.0-or-later
//
// Focused coverage for /api/tx/diag egress health classification.

using Zeus.Protocol2;

namespace Zeus.Server.Tests;

public sealed class TxEgressHealthDiagnosticsTests
{
    [Fact]
    public void BuildTxEgressHealth_MarksRecentP2RateAsLive()
    {
        var generated = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

        var health = ZeusEndpoints.BuildTxEgressHealth(
            generated,
            p1RingTotalWritten: 960,
            p1RingDropped: 0,
            p2: P2(
                inputComplexSamples: 240,
                packetsSent: 1,
                lastPacketsPerSecond: 801,
                lastRateTimestampUtc: generated.AddMilliseconds(-1000)));

        Assert.Equal(1, health.SchemaVersion);
        Assert.Equal("P2", health.ActiveTransport);
        Assert.Equal("p2-live", health.HealthStatus);
        Assert.True(health.P2Attached);
        Assert.True(health.P2Live);
        Assert.Equal(1000.0, health.P2LastActivityAgeMs.GetValueOrDefault());
        Assert.Equal(0.0, health.P1RingDropRatioPct);
        Assert.Contains("P2 DUC egress is live", health.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildTxEgressHealth_MarksPriorP2TrafficAsStaleWhenRateIsOld()
    {
        var generated = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

        var health = ZeusEndpoints.BuildTxEgressHealth(
            generated,
            p1RingTotalWritten: 960,
            p1RingDropped: 0,
            p2: P2(
                inputComplexSamples: 240,
                packetsSent: 1,
                lastPacketsPerSecond: 801,
                lastRateTimestampUtc: generated.AddSeconds(-10)));

        Assert.Equal("p2-stale", health.HealthStatus);
        Assert.True(health.P2Attached);
        Assert.False(health.P2Live);
        Assert.Equal(10_000.0, health.P2LastActivityAgeMs.GetValueOrDefault());
        Assert.Contains("not live right now", health.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildTxEgressHealth_PrioritizesP2TransportFailures()
    {
        var generated = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

        var health = ZeusEndpoints.BuildTxEgressHealth(
            generated,
            p1RingTotalWritten: 960,
            p1RingDropped: 0,
            p2: P2(
                inputComplexSamples: 240,
                packetsSent: 1,
                queueWriteFailures: 1,
                sendFailures: 2,
                lastPacketsPerSecond: 801,
                lastRateTimestampUtc: generated.AddMilliseconds(-500)));

        Assert.Equal("p2-send-failures", health.HealthStatus);
        Assert.True(health.P2Attached);
        Assert.True(health.P2Live);
        Assert.Contains("write/send failures", health.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildTxEgressHealth_FallsBackToP1WhenP2Unavailable()
    {
        var generated = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

        var health = ZeusEndpoints.BuildTxEgressHealth(
            generated,
            p1RingTotalWritten: 1000,
            p1RingDropped: 25,
            p2: null);

        Assert.Equal("P1", health.ActiveTransport);
        Assert.Equal("p2-unattached", health.HealthStatus);
        Assert.False(health.P2Attached);
        Assert.False(health.P2Live);
        Assert.Null(health.P2LastActivityAgeMs);
        Assert.Equal(2.5, health.P1RingDropRatioPct);
        Assert.Contains("P1 TX IQ ring is dropping samples", health.DiagnosticRecommendation);
    }

    static Protocol2TxIqDiagnostics P2(
        long inputComplexSamples = 0,
        long packetsQueued = 1,
        long packetsSent = 0,
        long queuedPackets = 0,
        long queueWriteFailures = 0,
        long sendFailures = 0,
        long resetDrainedPackets = 0,
        int scratchComplexSamples = 0,
        uint nextSequence = 1,
        int lastPacketsPerSecond = 0,
        long lastFifoModelSamples = 1200,
        DateTimeOffset? lastRateTimestampUtc = null,
        bool senderRunning = true)
    {
        return new Protocol2TxIqDiagnostics(
            InputComplexSamples: inputComplexSamples,
            PacketsQueued: packetsQueued,
            PacketsSent: packetsSent,
            QueuedPackets: queuedPackets,
            QueueWriteFailures: queueWriteFailures,
            SendFailures: sendFailures,
            ResetDrainedPackets: resetDrainedPackets,
            ScratchComplexSamples: scratchComplexSamples,
            NextSequence: nextSequence,
            LastPacketsPerSecond: lastPacketsPerSecond,
            LastFifoModelSamples: lastFifoModelSamples,
            LastRateTimestampUtc: lastRateTimestampUtc,
            SenderRunning: senderRunning);
    }
}
