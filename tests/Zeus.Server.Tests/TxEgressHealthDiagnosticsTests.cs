// SPDX-License-Identifier: GPL-2.0-or-later
//
// Focused coverage for /api/tx/diag egress health classification.

using Zeus.Protocol2;
using Zeus.Contracts;
using Zeus.Dsp;
using System.Text.Json;

namespace Zeus.Server.Tests;

public sealed class TxEgressHealthDiagnosticsTests
{
    [Fact]
    public void BuildTxStageDiagnostics_SuppressesIdleSentinelLevels()
    {
        using var doc = JsonSerializer.SerializeToDocument(
            ZeusEndpoints.BuildTxStageDiagnostics(TxStageMeters.Silent, hostTxActive: false));
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("wdsp-txa-meter-ring", root.GetProperty("source").GetString());
        Assert.Equal("idle", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("hostTxActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("micPkDbfs").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("outPkDbfs").ValueKind);
        Assert.Equal(0.0, root.GetProperty("alcGrDb").GetDouble());
    }

    [Fact]
    public void BuildTxStageDiagnostics_ExposesFiniteStageMeters()
    {
        var stage = new TxStageMeters(
            MicPk: -10.24f,
            MicAv: -21.4f,
            EqPk: -9.8f,
            EqAv: -20.5f,
            LvlrPk: -8.5f,
            LvlrAv: -18.4f,
            LvlrGr: 2.12f,
            CfcPk: -7.7f,
            CfcAv: -17.2f,
            CfcGr: 1.44f,
            CompPk: -6.8f,
            CompAv: -16.9f,
            AlcPk: -4.2f,
            AlcAv: -15.1f,
            AlcGr: 3.51f,
            OutPk: -1.8f,
            OutAv: -12.0f);

        using var doc = JsonSerializer.SerializeToDocument(
            ZeusEndpoints.BuildTxStageDiagnostics(stage, hostTxActive: true));
        var root = doc.RootElement;

        Assert.Equal("active", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("hostTxActive").GetBoolean());
        Assert.Equal(-10.2, root.GetProperty("micPkDbfs").GetDouble());
        Assert.Equal(2.1, root.GetProperty("lvlrGrDb").GetDouble());
        Assert.Equal(1.4, root.GetProperty("cfcGrDb").GetDouble());
        Assert.Equal(3.5, root.GetProperty("alcGrDb").GetDouble());
        Assert.Equal(-1.8, root.GetProperty("outPkDbfs").GetDouble());
        Assert.Contains("stage meters are live", root.GetProperty("diagnosticRecommendation").GetString());
    }

    [Fact]
    public void BuildTxAudioPathHealth_ClassifiesIdleP2StandbyRingPressure()
    {
        var generated = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

        var health = ZeusEndpoints.BuildTxAudioPathHealth(
            generated,
            ringTotalWritten: 520_192,
            ringTotalRead: 0,
            ringCount: 16_384,
            ringDropped: 487_424,
            ringCapacity: 16_384,
            ringRecentMag: 0.0004,
            totalMicSamples: 130_560,
            totalTxBlocks: 254,
            droppedFrames: 0,
            p2: P2(
                inputComplexSamples: 0,
                packetsQueued: 0,
                packetsSent: 0,
                queuedPackets: 0,
                lastPacketsPerSecond: 0,
                lastFifoModelSamples: 0),
            hostTxActive: false);

        Assert.Equal(1, health.SchemaVersion);
        Assert.Equal("standby-ring-pressure", health.Status);
        Assert.False(health.HostTxActive);
        Assert.True(health.P2Attached);
        Assert.False(health.P2DucLive);
        Assert.True(health.P2WaitingForTx);
        Assert.Null(health.P2LastActivityAgeMs);
        Assert.Equal(0, health.P2InputComplexSamples);
        Assert.Equal(0, health.P2PacketsSent);
        Assert.Equal(100.0, health.RingFillPct);
        Assert.Equal(93.701, health.RingDropRatioPct);
        Assert.Equal(0.0, health.RingRecentMag);
        Assert.Contains("standby ingest pressure", health.DiagnosticRecommendation);
    }

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
                lastRateTimestampUtc: generated.AddMilliseconds(-1000)),
            hostMoxOn: true,
            forwardWatts: 15.2);

        Assert.Equal(1, health.SchemaVersion);
        Assert.Equal("P2", health.ActiveTransport);
        Assert.Equal("p2-live", health.HealthStatus);
        Assert.True(health.P2Attached);
        Assert.True(health.P2Live);
        Assert.True(health.HostTxActive);
        Assert.True(health.RfDetected);
        Assert.Equal("rf-active", health.RfEvidenceStatus);
        Assert.Equal(15.2, health.ForwardWatts);
        Assert.Equal(1000.0, health.P2LastActivityAgeMs.GetValueOrDefault());
        Assert.Equal(0.0, health.P1RingDropRatioPct);
        Assert.Equal(100, health.QualityScore);
        Assert.Equal("ready", health.QualityTone);
        Assert.Equal("fresh", health.P2PacketRateStatus);
        Assert.Equal(801, health.P2LastPacketsPerSecond);
        Assert.Equal(1200, health.P2FifoModelSamples);
        Assert.Equal(0, health.P2QueuedPackets);
        Assert.Equal(0, health.P2TransportFailures);
        Assert.Contains("p2-rate-fresh", health.QualityReasons);
        Assert.Contains("rf-forward-power-present", health.QualityReasons);
        Assert.Equal("pep-intermittent", health.TxDutyProfile);
        Assert.Null(health.ContinuousDutyRecommendedMaxWatts);
        Assert.False(health.ContinuousDutyLimitExceeded);
        Assert.Contains("RF forward-power evidence", health.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildTxEgressHealth_FlagsG2ContinuousDutyPowerAboveManualLimit()
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
                lastRateTimestampUtc: generated.AddMilliseconds(-750)),
            hostMoxOn: true,
            forwardWatts: 45.0,
            txMode: RxMode.AM,
            g2DutyGuidance: true);

        Assert.Equal("p2-live", health.HealthStatus);
        Assert.True(health.RfDetected);
        Assert.Equal("continuous-duty", health.TxDutyProfile);
        Assert.Equal(30.0, health.ContinuousDutyRecommendedMaxWatts);
        Assert.True(health.ContinuousDutyLimitExceeded);
        Assert.Contains("30 W or less", health.ContinuousDutyManualReference);
        Assert.Equal("protect", health.QualityTone);
        Assert.Equal(35, health.QualityScore);
        Assert.Contains("continuous-duty-mode", health.QualityReasons);
        Assert.Contains("continuous-duty-limit-exceeded", health.QualityReasons);
    }

    [Fact]
    public void BuildTxEgressHealth_DistinguishesLiveTransportFromRfEvidence()
    {
        var generated = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

        var health = ZeusEndpoints.BuildTxEgressHealth(
            generated,
            p1RingTotalWritten: 0,
            p1RingDropped: 0,
            p2: P2(
                inputComplexSamples: 240,
                packetsSent: 1,
                lastPacketsPerSecond: 801,
                lastRateTimestampUtc: generated.AddMilliseconds(-500)),
            hostMoxOn: false,
            hostTunOn: false,
            hostTwoToneOn: false,
            hardwarePtt: false,
            forwardWatts: 0.0);

        Assert.Equal("p2-live", health.HealthStatus);
        Assert.True(health.P2Live);
        Assert.False(health.HostTxActive);
        Assert.False(health.RfDetected);
        Assert.Equal("transport-live-rf-idle", health.RfEvidenceStatus);
        Assert.Equal(74, health.QualityScore);
        Assert.Equal("verify", health.QualityTone);
        Assert.Contains("host-tx-idle", health.QualityReasons);
        Assert.Contains("rf-forward-power-missing", health.QualityReasons);
        Assert.Contains("off-air TX monitor or idle packet flow", health.DiagnosticRecommendation);
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
        Assert.Equal(46, health.QualityScore);
        Assert.Equal("verify", health.QualityTone);
        Assert.Equal("stale", health.P2PacketRateStatus);
        Assert.Contains("p2-rate-stale", health.QualityReasons);
        Assert.Contains("not live right now", health.DiagnosticRecommendation);
    }

    [Fact]
    public void BuildTxEgressHealth_MarksIdleP2RingDropsAsStandbyPressure()
    {
        var generated = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

        var health = ZeusEndpoints.BuildTxEgressHealth(
            generated,
            p1RingTotalWritten: 1000,
            p1RingDropped: 25,
            p2: P2(
                inputComplexSamples: 0,
                packetsQueued: 0,
                packetsSent: 0,
                queuedPackets: 0,
                lastPacketsPerSecond: 0,
                lastFifoModelSamples: 0),
            hostMoxOn: false,
            hostTunOn: false,
            hostTwoToneOn: false,
            forwardWatts: 0.0);

        Assert.Equal("p2-waiting-for-tx", health.HealthStatus);
        Assert.True(health.P2Attached);
        Assert.False(health.HostTxActive);
        Assert.Equal(2.5, health.P1RingDropRatioPct);
        Assert.Contains("p1-ring-standby-pressure", health.QualityReasons);
        Assert.DoesNotContain("p1-ring-drop-pressure", health.QualityReasons);
        Assert.Contains("standby drops are not on-air P2 egress evidence", health.DiagnosticRecommendation);
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
        Assert.Equal("transport-live-rf-idle", health.RfEvidenceStatus);
        Assert.Equal(18, health.QualityScore);
        Assert.Equal("protect", health.QualityTone);
        Assert.Equal(3, health.P2TransportFailures);
        Assert.Contains("p2-queue-write-failures", health.QualityReasons);
        Assert.Contains("p2-send-failures", health.QualityReasons);
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
        Assert.False(health.HostTxActive);
        Assert.False(health.RfDetected);
        Assert.Equal("rf-idle", health.RfEvidenceStatus);
        Assert.Equal(30, health.QualityScore);
        Assert.Equal("verify", health.QualityTone);
        Assert.Equal("missing", health.P2PacketRateStatus);
        Assert.Null(health.P2LastPacketsPerSecond);
        Assert.Null(health.P2FifoModelSamples);
        Assert.Null(health.P2QueuedPackets);
        Assert.Equal(0, health.P2TransportFailures);
        Assert.Contains("p2-unattached", health.QualityReasons);
        Assert.Contains("p1-ring-drop-pressure", health.QualityReasons);
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
