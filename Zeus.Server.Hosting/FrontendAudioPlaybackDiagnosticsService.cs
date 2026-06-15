// SPDX-License-Identifier: GPL-2.0-or-later
//
// Mirrors browser-side RX audio playback health into the backend diagnostics
// snapshot. Server-side RX audio can be fresh while a browser client is still
// underrunning, over-buffering, or stalled, so keep this as a separate
// frontend-originated health surface.

namespace Zeus.Server;

public sealed class FrontendAudioPlaybackDiagnosticsService
{
    private const int MaxText = 180;
    private const long FreshMs = 5_000;
    private const long StaleMs = 15_000;
    private const long FutureSourceToleranceMs = 5_000;
    private readonly object _sync = new();
    private FrontendAudioPlaybackSnapshot? _latest;

    public FrontendAudioPlaybackSnapshot Update(FrontendAudioPlaybackDiagnosticsRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new FrontendAudioPlaybackSnapshot(
            SchemaVersion: 1,
            AtUtc: now,
            SourceAtUtc: request.SourceAtUtc,
            SourceClientId: Clean(request.SourceClientId, 64),
            PlaybackState: CleanPlaybackState(request.PlaybackState),
            ContextState: Clean(request.ContextState, 32),
            BufferedSamples: NonNegative(request.BufferedSamples),
            BufferedMs: NonNegativeDouble(request.BufferedMs),
            SampleRateHz: NonNegative(request.SampleRateHz),
            ContextSampleRateHz: NonNegative(request.ContextSampleRateHz),
            BaseLatencyMs: NonNegativeDouble(request.BaseLatencyMs),
            OutputLatencyMs: NonNegativeDouble(request.OutputLatencyMs),
            UnderrunCount: NonNegativeLong(request.UnderrunCount),
            DroppedSamples: NonNegativeLong(request.DroppedSamples),
            LatePushCount: NonNegativeLong(request.LatePushCount),
            LatenessVsScheduleCount: NonNegativeLong(request.LatenessVsScheduleCount),
            PendingSources: NonNegative(request.PendingSources),
            BufferTargetMs: NonNegativeDouble(request.BufferTargetMs),
            BufferMaxMs: NonNegativeDouble(request.BufferMaxMs),
            ErrorMessage: Clean(request.ErrorMessage, MaxText));

        lock (_sync)
        {
            _latest = snapshot;
            return snapshot;
        }
    }

    public FrontendAudioPlaybackDiagnosticsDto Snapshot()
    {
        lock (_sync)
        {
            if (_latest is null)
            {
                var health = Health(null, null, null, null);
                return FrontendAudioPlaybackDiagnosticsDto.Missing(health);
            }

            var timing = Timing(_latest, DateTimeOffset.UtcNow);
            return new(
                SchemaVersion: _latest.SchemaVersion,
                Available: true,
                AgeMs: timing.AgeMs,
                SourceAgeMs: timing.SourceAgeMs,
                SourceClockSkewMs: timing.SourceClockSkewMs,
                Status: timing.Health.Status,
                Fresh: timing.Health.Fresh,
                Stale: timing.Health.Stale,
                DiagnosticRecommendation: timing.Health.Recommendation,
                AtUtc: _latest.AtUtc,
                SourceAtUtc: _latest.SourceAtUtc,
                SourceClientId: _latest.SourceClientId,
                PlaybackState: _latest.PlaybackState,
                ContextState: _latest.ContextState,
                BufferedSamples: _latest.BufferedSamples,
                BufferedMs: _latest.BufferedMs,
                SampleRateHz: _latest.SampleRateHz,
                ContextSampleRateHz: _latest.ContextSampleRateHz,
                BaseLatencyMs: _latest.BaseLatencyMs,
                OutputLatencyMs: _latest.OutputLatencyMs,
                UnderrunCount: _latest.UnderrunCount,
                DroppedSamples: _latest.DroppedSamples,
                LatePushCount: _latest.LatePushCount,
                LatenessVsScheduleCount: _latest.LatenessVsScheduleCount,
                PendingSources: _latest.PendingSources,
                BufferTargetMs: _latest.BufferTargetMs,
                BufferMaxMs: _latest.BufferMaxMs,
                ErrorMessage: _latest.ErrorMessage);
        }
    }

    private static FrontendAudioPlaybackTiming Timing(FrontendAudioPlaybackSnapshot latest, DateTimeOffset now)
    {
        var ageMs = Math.Max(0L, (long)(now - latest.AtUtc).TotalMilliseconds);
        long? sourceAgeMs = null;
        long? sourceClockSkewMs = null;
        if (latest.SourceAtUtc is { } sourceAt)
        {
            var sourceDeltaMs = (long)(now - sourceAt).TotalMilliseconds;
            if (sourceDeltaMs >= 0)
            {
                sourceAgeMs = sourceDeltaMs;
            }
            else
            {
                sourceAgeMs = 0;
                sourceClockSkewMs = -sourceDeltaMs;
            }
        }

        return new(ageMs, sourceAgeMs, sourceClockSkewMs, Health(ageMs, sourceAgeMs, sourceClockSkewMs, latest));
    }

    private static FrontendAudioPlaybackHealth Health(
        long? ageMs,
        long? sourceAgeMs,
        long? sourceClockSkewMs,
        FrontendAudioPlaybackSnapshot? latest)
    {
        if (ageMs is null || latest is null)
        {
            return new(
                "missing",
                Fresh: false,
                Stale: false,
                "No frontend RX playback diagnostics have been published yet; open a web client or use desktop native-audio diagnostics to audit what the operator actually hears.");
        }

        if (ageMs.Value <= StaleMs && sourceClockSkewMs is { } skewMs && skewMs > FutureSourceToleranceMs)
        {
            return new(
                "clock-skew",
                Fresh: false,
                Stale: false,
                $"Frontend RX playback timestamp is {(skewMs / 1000.0):0.0}s in the future; verify client and host clocks before trusting playback freshness.");
        }

        long effectiveAgeMs = Math.Max(ageMs.Value, sourceAgeMs ?? ageMs.Value);
        if (effectiveAgeMs > StaleMs)
        {
            return new(
                "stale",
                Fresh: false,
                Stale: true,
                "Frontend RX playback diagnostics are stale; verify the web client is open and connected before judging receive audio fidelity.");
        }

        if (latest.PlaybackState == "native")
        {
            return new(
                "native-audio",
                Fresh: effectiveAgeMs <= FreshMs,
                Stale: false,
                "This client reports desktop native audio mode; use NativeAudioSink and server RX audio diagnostics for the actual playback device path.");
        }

        if (latest.PlaybackState == "error")
        {
            var suffix = string.IsNullOrWhiteSpace(latest.ErrorMessage) ? "" : $" Error: {latest.ErrorMessage}";
            return new(
                "client-audio-error",
                Fresh: false,
                Stale: false,
                $"Frontend RX playback failed to start; fix browser audio permissions/device state before tuning weak-signal audio.{suffix}");
        }

        if (latest.PlaybackState is "idle" or "loading")
        {
            return new(
                $"client-audio-{latest.PlaybackState}",
                Fresh: effectiveAgeMs <= FreshMs,
                Stale: false,
                "Frontend RX playback is not actively playing through this browser; unmute the client or use the desktop native sink before judging operator audio fidelity.");
        }

        if (latest.DroppedSamples > 0)
        {
            return new(
                "client-buffer-overrun",
                Fresh: effectiveAgeMs <= FreshMs,
                Stale: false,
                "Frontend RX playback is dropping scheduled samples because the browser is buffered too far ahead; inspect websocket cadence and browser tab scheduling before changing DSP gain.");
        }

        if (latest.LatePushCount > 0)
        {
            return new(
                "client-main-thread-late",
                Fresh: effectiveAgeMs <= FreshMs,
                Stale: false,
                "Frontend RX playback underruns include late websocket/main-thread delivery; reduce browser load or competing tabs before treating this as RF/DSP distortion.");
        }

        if (latest.LatenessVsScheduleCount > 0 || latest.UnderrunCount > 0)
        {
            return new(
                "client-render-late",
                Fresh: effectiveAgeMs <= FreshMs,
                Stale: false,
                "Frontend RX playback underruns are visible in the browser schedule; check OS audio load, output device latency, and browser throttling before tuning DSP.");
        }

        if (latest.BufferedMs is { } buffered && latest.BufferTargetMs is { } target && buffered < target * 0.35)
        {
            return new(
                "client-buffer-low",
                Fresh: effectiveAgeMs <= FreshMs,
                Stale: false,
                "Frontend RX playback buffer is near the underrun floor; watch for late websocket delivery or browser audio scheduling stalls.");
        }

        return new(
            "client-playback-healthy",
            Fresh: effectiveAgeMs <= FreshMs,
            Stale: false,
            "Frontend RX playback is fresh with no reported underruns or dropped scheduled samples; correlate with server RX audio, RXA meters, and listenability diagnostics.");
    }

    private static string? Clean(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var value = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string CleanPlaybackState(string? raw)
    {
        var state = Clean(raw, 32)?.ToLowerInvariant();
        return state is "idle" or "loading" or "playing" or "native" or "error"
            ? state
            : "unknown";
    }

    private static int NonNegative(int? value) => value is { } v && v > 0 ? v : 0;

    private static long NonNegativeLong(long? value) => value is { } v && v > 0 ? v : 0;

    private static double? NonNegativeDouble(double? value)
    {
        if (value is not { } v || !double.IsFinite(v) || v < 0) return null;
        return Math.Round(v, 1);
    }
}

public sealed record FrontendAudioPlaybackDiagnosticsRequest(
    string? SourceClientId,
    string? PlaybackState,
    string? ContextState,
    int? BufferedSamples,
    double? BufferedMs,
    int? SampleRateHz,
    int? ContextSampleRateHz,
    double? BaseLatencyMs,
    double? OutputLatencyMs,
    long? UnderrunCount,
    long? DroppedSamples,
    long? LatePushCount,
    long? LatenessVsScheduleCount,
    int? PendingSources,
    double? BufferTargetMs,
    double? BufferMaxMs,
    string? ErrorMessage,
    DateTimeOffset? SourceAtUtc = null);

public sealed record FrontendAudioPlaybackSnapshot(
    int SchemaVersion,
    DateTimeOffset AtUtc,
    DateTimeOffset? SourceAtUtc,
    string? SourceClientId,
    string PlaybackState,
    string? ContextState,
    int BufferedSamples,
    double? BufferedMs,
    int SampleRateHz,
    int ContextSampleRateHz,
    double? BaseLatencyMs,
    double? OutputLatencyMs,
    long UnderrunCount,
    long DroppedSamples,
    long LatePushCount,
    long LatenessVsScheduleCount,
    int PendingSources,
    double? BufferTargetMs,
    double? BufferMaxMs,
    string? ErrorMessage);

public sealed record FrontendAudioPlaybackDiagnosticsDto(
    int SchemaVersion,
    bool Available,
    long? AgeMs,
    long? SourceAgeMs,
    long? SourceClockSkewMs,
    string Status,
    bool Fresh,
    bool Stale,
    string DiagnosticRecommendation,
    DateTimeOffset? AtUtc,
    DateTimeOffset? SourceAtUtc,
    string? SourceClientId,
    string? PlaybackState,
    string? ContextState,
    int BufferedSamples,
    double? BufferedMs,
    int SampleRateHz,
    int ContextSampleRateHz,
    double? BaseLatencyMs,
    double? OutputLatencyMs,
    long UnderrunCount,
    long DroppedSamples,
    long LatePushCount,
    long LatenessVsScheduleCount,
    int PendingSources,
    double? BufferTargetMs,
    double? BufferMaxMs,
    string? ErrorMessage)
{
    internal static FrontendAudioPlaybackDiagnosticsDto Missing(FrontendAudioPlaybackHealth health) => new(
        SchemaVersion: 1,
        Available: false,
        AgeMs: null,
        SourceAgeMs: null,
        SourceClockSkewMs: null,
        Status: health.Status,
        Fresh: false,
        Stale: false,
        DiagnosticRecommendation: health.Recommendation,
        AtUtc: null,
        SourceAtUtc: null,
        SourceClientId: null,
        PlaybackState: null,
        ContextState: null,
        BufferedSamples: 0,
        BufferedMs: null,
        SampleRateHz: 0,
        ContextSampleRateHz: 0,
        BaseLatencyMs: null,
        OutputLatencyMs: null,
        UnderrunCount: 0,
        DroppedSamples: 0,
        LatePushCount: 0,
        LatenessVsScheduleCount: 0,
        PendingSources: 0,
        BufferTargetMs: null,
        BufferMaxMs: null,
        ErrorMessage: null);
}

internal sealed record FrontendAudioPlaybackHealth(
    string Status,
    bool Fresh,
    bool Stale,
    string Recommendation);

internal sealed record FrontendAudioPlaybackTiming(
    long AgeMs,
    long? SourceAgeMs,
    long? SourceClockSkewMs,
    FrontendAudioPlaybackHealth Health);
