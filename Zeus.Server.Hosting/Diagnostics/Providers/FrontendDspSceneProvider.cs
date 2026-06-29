// SPDX-License-Identifier: GPL-2.0-or-later
//
// v2 provider wrapping the frontend-published DSP scene snapshot. Snapshot()
// returns the same object the legacy GET /api/radio/diagnostics/dsp-scene route
// returns (anonymous when present), so it serialises via the reflection resolver.

namespace Zeus.Server.Diagnostics;

public sealed class FrontendDspSceneProvider : IDiagnosticsProvider
{
    private readonly FrontendDspSceneDiagnosticsService _scene;

    public FrontendDspSceneProvider(FrontendDspSceneDiagnosticsService scene)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
    }

    public string Id => "frontend.dsp-scene";
    public string RouteSegment => "frontend-dsp-scene";
    public string Category => "frontend";
    public int SchemaVersion => 1;
    public string Description => "Frontend-published spectrum/Smart-NR scene evidence (client-originated).";

    public object Snapshot() => _scene.Snapshot();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("snapshot-available",
            "Frontend DSP scene snapshot builds without error.", DiagnosticsSeverity.Info,
            _ => _scene.Snapshot() is not null
                ? new SelfCheckResult(SelfCheckOutcome.Pass, "Frontend scene snapshot is available.", DateTimeOffset.UtcNow)
                : new SelfCheckResult(SelfCheckOutcome.Fail, "Frontend scene snapshot returned null.", DateTimeOffset.UtcNow)),
    };
}
