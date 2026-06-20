// SPDX-License-Identifier: GPL-2.0-or-later
//
// v2 provider wrapping the frontend RX audio-playback health snapshot. Returns
// the same typed DTO as the legacy GET /api/radio/diagnostics/audio-playback
// route (registered with the source-gen JSON context).

namespace Zeus.Server.Diagnostics;

public sealed class FrontendAudioPlaybackProvider : IDiagnosticsProvider
{
    private readonly FrontendAudioPlaybackDiagnosticsService _playback;

    public FrontendAudioPlaybackProvider(FrontendAudioPlaybackDiagnosticsService playback)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
    }

    public string Id => "frontend.audio-playback";
    public string RouteSegment => "frontend-audio-playback";
    public string Category => "frontend";
    public int SchemaVersion => 1;
    public string Description => "Frontend RX audio playback health (buffering, underruns, dropouts).";

    public object Snapshot() => _playback.Snapshot();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("playback-published",
            "A frontend client has published RX playback diagnostics.", DiagnosticsSeverity.Info,
            _ => _playback.Snapshot().Available
                ? new SelfCheckResult(SelfCheckOutcome.Pass, "Frontend playback diagnostics present.", DateTimeOffset.UtcNow)
                : new SelfCheckResult(SelfCheckOutcome.Warn, "No frontend playback diagnostics published yet.", DateTimeOffset.UtcNow)),

        new DiagnosticsSelfCheck("playback-fresh",
            "Frontend RX playback diagnostics are not stale.", DiagnosticsSeverity.Warn,
            _ =>
            {
                var dto = _playback.Snapshot();
                if (!dto.Available)
                    return new SelfCheckResult(SelfCheckOutcome.Warn, "No playback diagnostics to evaluate.", DateTimeOffset.UtcNow);
                return dto.Stale
                    ? new SelfCheckResult(SelfCheckOutcome.Warn, "Frontend playback diagnostics are stale.", DateTimeOffset.UtcNow)
                    : new SelfCheckResult(SelfCheckOutcome.Pass, $"Playback status '{dto.Status}'.", DateTimeOffset.UtcNow);
            }),

        new DiagnosticsSelfCheck("playback-no-dropouts",
            "No reported underruns or dropped scheduled samples.", DiagnosticsSeverity.Warn,
            _ =>
            {
                var dto = _playback.Snapshot();
                if (!dto.Available)
                    return new SelfCheckResult(SelfCheckOutcome.Pass, "No playback diagnostics to evaluate.", DateTimeOffset.UtcNow);
                return dto.UnderrunCount > 0 || dto.DroppedSamples > 0
                    ? new SelfCheckResult(SelfCheckOutcome.Warn,
                        $"underruns={dto.UnderrunCount} dropped={dto.DroppedSamples}", DateTimeOffset.UtcNow)
                    : new SelfCheckResult(SelfCheckOutcome.Pass, "No underruns or dropped samples.", DateTimeOffset.UtcNow);
            }),
    };
}
