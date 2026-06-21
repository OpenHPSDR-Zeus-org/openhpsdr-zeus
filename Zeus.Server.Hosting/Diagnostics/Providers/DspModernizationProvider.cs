// SPDX-License-Identifier: GPL-2.0-or-later
//
// v2 provider owning the DSP modernization evidence bundle. The legacy
// GET /api/dsp/modernization-snapshot route delegates here.

using Zeus.Contracts;

namespace Zeus.Server.Diagnostics;

public sealed class DspModernizationProvider : IDiagnosticsProvider
{
    private readonly FrontendDspSceneDiagnosticsService _scene;
    private readonly DspPipelineService _dsp;
    private readonly RadioService _radio;

    public DspModernizationProvider(
        FrontendDspSceneDiagnosticsService scene,
        DspPipelineService dsp,
        RadioService radio)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
    }

    public string Id => "dsp.modernization";
    public string RouteSegment => "dsp-modernization";
    public string Category => "dsp";
    public int SchemaVersion => 1;
    public string Description => "Read-only WDSP modernization evidence bundle for capture tooling.";

    public object Snapshot() => Build();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("evidence-completeness",
            "Modernization evidence completeness score.", DiagnosticsSeverity.Info,
            _ =>
            {
                var dto = Build();
                var detail = $"completeness={dto.EvidenceCompletenessScore} status={dto.Status}";
                return dto.EvidenceCompletenessScore >= 75
                    ? new SelfCheckResult(SelfCheckOutcome.Pass, detail, DateTimeOffset.UtcNow)
                    : new SelfCheckResult(SelfCheckOutcome.Warn, detail, DateTimeOffset.UtcNow);
            }),
    };

    private DspModernizationEvidenceSnapshotDto Build()
    {
        var state = _radio.Snapshot();
        var condition = _scene.SmartNrCondition(
            _dsp.SnapshotNrRuntime(),
            ZeusEndpoints.BuildSmartNrRxChainRuntime(state, _radio.GetAdcProtectionStatus()));
        var live = DspLiveDiagnosticsService.Build(condition, _dsp.SnapshotLiveRuntimeEvidence(), state);
        var plan = DspBenchmarkPlanCatalog.Build();
        var manifest = DspBenchmarkCaptureManifestService.Build(live, plan);
        return DspModernizationEvidenceSnapshotService.Build(
            condition, live, plan, manifest, DspExternalEngineCandidateCatalog.All());
    }
}
