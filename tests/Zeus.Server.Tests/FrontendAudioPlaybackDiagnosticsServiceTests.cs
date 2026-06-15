// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class FrontendAudioPlaybackDiagnosticsServiceTests
{
    [Fact]
    public void Snapshot_ReportsMissingBeforeFrontendPublishesPlayback()
    {
        var service = new FrontendAudioPlaybackDiagnosticsService();

        var diag = service.Snapshot();

        Assert.False(diag.Available);
        Assert.Equal("missing", diag.Status);
        Assert.False(diag.Fresh);
        Assert.False(diag.Stale);
        Assert.Contains("No frontend RX playback", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void Update_StoresHealthyPlayingPlaybackDiagnostics()
    {
        var service = new FrontendAudioPlaybackDiagnosticsService();
        var sourceAt = DateTimeOffset.UtcNow.AddMilliseconds(-120);

        var stored = service.Update(new FrontendAudioPlaybackDiagnosticsRequest(
            SourceClientId: "  client   rx  ",
            PlaybackState: "playing",
            ContextState: "running",
            BufferedSamples: 14_400,
            BufferedMs: 300.2,
            SampleRateHz: 48_000,
            ContextSampleRateHz: 48_000,
            BaseLatencyMs: 22.4,
            OutputLatencyMs: 49.6,
            UnderrunCount: 0,
            DroppedSamples: 0,
            LatePushCount: 0,
            LatenessVsScheduleCount: 0,
            PendingSources: 15,
            BufferTargetMs: 300,
            BufferMaxMs: 500,
            ErrorMessage: null,
            SourceAtUtc: sourceAt));

        Assert.Equal("client rx", stored.SourceClientId);
        Assert.Equal(300.2, stored.BufferedMs);
        Assert.Equal(22.4, stored.BaseLatencyMs);
        Assert.Equal(49.6, stored.OutputLatencyMs);

        var diag = service.Snapshot();

        Assert.True(diag.Available);
        Assert.Equal("client-playback-healthy", diag.Status);
        Assert.True(diag.Fresh);
        Assert.False(diag.Stale);
        Assert.Equal("playing", diag.PlaybackState);
        Assert.Equal("running", diag.ContextState);
        Assert.Equal(14_400, diag.BufferedSamples);
        Assert.Equal(300.2, diag.BufferedMs);
        Assert.Equal(48_000, diag.ContextSampleRateHz);
        Assert.Equal(15, diag.PendingSources);
        Assert.Contains("no reported underruns", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void Snapshot_ClassifiesLateWebsocketMainThreadPlayback()
    {
        var service = new FrontendAudioPlaybackDiagnosticsService();

        service.Update(new FrontendAudioPlaybackDiagnosticsRequest(
            SourceClientId: "client",
            PlaybackState: "playing",
            ContextState: "running",
            BufferedSamples: 7_200,
            BufferedMs: 150,
            SampleRateHz: 48_000,
            ContextSampleRateHz: 48_000,
            BaseLatencyMs: 20,
            OutputLatencyMs: null,
            UnderrunCount: 3,
            DroppedSamples: 0,
            LatePushCount: 2,
            LatenessVsScheduleCount: 1,
            PendingSources: 7,
            BufferTargetMs: 300,
            BufferMaxMs: 500,
            ErrorMessage: null));

        var diag = service.Snapshot();

        Assert.Equal("client-main-thread-late", diag.Status);
        Assert.Equal(2, diag.LatePushCount);
        Assert.Equal(1, diag.LatenessVsScheduleCount);
        Assert.Contains("websocket/main-thread", diag.DiagnosticRecommendation);
    }

    [Fact]
    public void Snapshot_ClassifiesClientAudioError()
    {
        var service = new FrontendAudioPlaybackDiagnosticsService();

        service.Update(new FrontendAudioPlaybackDiagnosticsRequest(
            SourceClientId: "client",
            PlaybackState: "error",
            ContextState: null,
            BufferedSamples: null,
            BufferedMs: null,
            SampleRateHz: null,
            ContextSampleRateHz: null,
            BaseLatencyMs: null,
            OutputLatencyMs: null,
            UnderrunCount: null,
            DroppedSamples: null,
            LatePushCount: null,
            LatenessVsScheduleCount: null,
            PendingSources: null,
            BufferTargetMs: 300,
            BufferMaxMs: 500,
            ErrorMessage: " NotAllowedError: audio denied "));

        var diag = service.Snapshot();

        Assert.Equal("client-audio-error", diag.Status);
        Assert.False(diag.Fresh);
        Assert.Equal("NotAllowedError: audio denied", diag.ErrorMessage);
        Assert.Contains("browser audio permissions", diag.DiagnosticRecommendation);
    }
}
