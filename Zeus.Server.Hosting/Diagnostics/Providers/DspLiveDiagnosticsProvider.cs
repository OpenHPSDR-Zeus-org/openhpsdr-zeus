// SPDX-License-Identifier: GPL-2.0-or-later
//
// v2 provider wrapping the existing DSP modernization scorecard. Reproduces the
// exact body of the legacy GET /api/dsp/live-diagnostics lambda so the unified
// surface and the legacy route return byte-identical JSON. No behaviour change.

using Zeus.Contracts;

namespace Zeus.Server.Diagnostics;

public sealed class DspLiveDiagnosticsProvider : IDiagnosticsProvider
{
    private readonly FrontendDspSceneDiagnosticsService _scene;
    private readonly DspPipelineService _dsp;
    private readonly RadioService _radio;

    public DspLiveDiagnosticsProvider(
        FrontendDspSceneDiagnosticsService scene,
        DspPipelineService dsp,
        RadioService radio)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
    }

    public string Id => "dsp.live";
    public string RouteSegment => "dsp-live";
    public string Category => "dsp";
    public int SchemaVersion => 1;
    public string Description => "Live WDSP/Smart-NR modernization readiness scorecard.";

    public object Snapshot() => Build();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("wdsp-native-loadable",
            "WDSP native library is loadable.", DiagnosticsSeverity.Error,
            _ => Build() is { WdspNativeLoadable: true }
                ? Pass("WDSP native is loadable.")
                : Fail("WDSP native is not loadable; fix native packaging before judging DSP quality.")),

        new DiagnosticsSelfCheck("wdsp-active",
            "WDSP is the active DSP engine.", DiagnosticsSeverity.Warn,
            _ => Build() is { WdspActive: true }
                ? Pass("WDSP is active.")
                : Warn("WDSP is not active; connect the radio or restart the DSP engine for live telemetry.")),

        new DiagnosticsSelfCheck("frontend-scene-fresh",
            "Frontend DSP scene evidence is present and fresh.", DiagnosticsSeverity.Warn,
            _ =>
            {
                var dto = Build();
                if (!dto.FrontendSceneAvailable)
                    return Warn("No frontend DSP scene yet; open Signal Intelligence / Smart NR.");
                return dto.FrontendSceneFresh
                    ? Pass("Frontend scene is fresh.")
                    : Warn("Frontend scene is stale; refresh or reconnect the client.");
            }),

        new DiagnosticsSelfCheck("smart-nr-runtime-aligned",
            "Smart NR runtime is aligned with the requested profile.", DiagnosticsSeverity.Warn,
            _ => Build().RuntimeAligned == false
                ? Warn("Smart NR runtime is not aligned; reapply or inspect the DSP apply path.")
                : Pass("Smart NR runtime alignment is OK or not applicable.")),
    };

    private DspLiveDiagnosticsDto Build()
    {
        var state = _radio.Snapshot();
        var condition = _scene.SmartNrCondition(
            _dsp.SnapshotNrRuntime(),
            ZeusEndpoints.BuildSmartNrRxChainRuntime(state, _radio.GetAdcProtectionStatus()));
        return DspLiveDiagnosticsService.Build(condition, _dsp.SnapshotLiveRuntimeEvidence(), state);
    }

    private static SelfCheckResult Pass(string detail) =>
        new(SelfCheckOutcome.Pass, detail, DateTimeOffset.UtcNow);

    private static SelfCheckResult Warn(string detail) =>
        new(SelfCheckOutcome.Warn, detail, DateTimeOffset.UtcNow);

    private static SelfCheckResult Fail(string detail) =>
        new(SelfCheckOutcome.Fail, detail, DateTimeOffset.UtcNow);
}
