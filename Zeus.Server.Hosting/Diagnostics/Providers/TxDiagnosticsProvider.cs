// SPDX-License-Identifier: GPL-2.0-or-later
//
// v2 provider for the legacy /api/tx/diag snapshot. The legacy route now
// delegates here so TX diagnostic read logic has one owner while the existing
// bare-payload route stays wire-compatible.

using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Zeus.Plugins.Host;
using Zeus.Plugins.Host.Audio;
using Zeus.Protocol1;
using Zeus.Protocol2;

namespace Zeus.Server.Diagnostics;

public sealed class TxDiagnosticsProvider : IDiagnosticsProvider
{
    private readonly TxIqRing _ring;
    private readonly ITxIqSource _src;
    private readonly TxAudioIngest _ingest;
    private readonly DspPipelineService _dsp;
    private readonly TxService _tx;
    private readonly RadioService _radio;
    private readonly StreamingHub _hub;
    private readonly HardwareDiagnosticsService _hardware;
    private readonly ExternalPttService _externalPtt;
    private readonly AudioPluginBridge _pluginBridge;
    private readonly VstEngineController _vstEngine;
    private readonly RxVstEngineService _rxVstEngine;

    public TxDiagnosticsProvider(
        TxIqRing ring,
        ITxIqSource src,
        TxAudioIngest ingest,
        DspPipelineService dsp,
        TxService tx,
        RadioService radio,
        StreamingHub hub,
        HardwareDiagnosticsService hardware,
        ExternalPttService externalPtt,
        AudioPluginBridge pluginBridge,
        VstEngineController vstEngine,
        RxVstEngineService rxVstEngine)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _src = src ?? throw new ArgumentNullException(nameof(src));
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
        _dsp = dsp ?? throw new ArgumentNullException(nameof(dsp));
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        _externalPtt = externalPtt ?? throw new ArgumentNullException(nameof(externalPtt));
        _pluginBridge = pluginBridge ?? throw new ArgumentNullException(nameof(pluginBridge));
        _vstEngine = vstEngine ?? throw new ArgumentNullException(nameof(vstEngine));
        _rxVstEngine = rxVstEngine ?? throw new ArgumentNullException(nameof(rxVstEngine));
    }

    public string Id => "tx.diagnostics";
    public string RouteSegment => "tx";
    public string Category => "tx";
    public int SchemaVersion => 1;
    public string Description => "TX audio, WDSP stage, plugin, and P1/P2 egress health.";

    public object Snapshot() => Build();

    public IReadOnlyList<DiagnosticsSelfCheck> SelfChecks => new[]
    {
        new DiagnosticsSelfCheck("tx-diagnostics-snapshot",
            "TX diagnostics snapshot builds.", DiagnosticsSeverity.Info,
            _ => DiagnosticsProbe.NonNull(Build(), "tx diagnostics")),
        new DiagnosticsSelfCheck("tx-egress-no-transport-failures",
            "TX egress has no P2 write/send failures.", DiagnosticsSeverity.Warn,
            _ =>
            {
                var snapshot = Build();
                return snapshot.Egress.P2TransportFailures == 0
                    ? new SelfCheckResult(SelfCheckOutcome.Pass, "No P2 TX transport failures.", DateTimeOffset.UtcNow)
                    : new SelfCheckResult(SelfCheckOutcome.Warn,
                        $"P2 TX transport failures={snapshot.Egress.P2TransportFailures}.", DateTimeOffset.UtcNow);
            }),
    };

    private TxDiagnosticsSnapshotDto Build()
    {
        var generatedUtc = DateTimeOffset.UtcNow;
        var p2Tx = _dsp.ActiveP2Client?.TxIqDiagnosticsSnapshot();
        var micUplink = _hub.MicInboundDiagnosticsSnapshot(generatedUtc);
        var radioState = _radio.Snapshot();
        var keying = _hardware.KeyingSnapshot(_externalPtt.Snapshot());
        var power = _hardware.PowerCalibrationSnapshot();
        bool hostTxActive = _tx.IsMoxOn || _tx.IsTunOn || _tx.IsTwoToneOn;
        bool txStageActive = hostTxActive || radioState.TxMonitorEnabled;
        bool requiresMicUplink = _tx.IsMoxOn && !_tx.IsTunOn && !_tx.IsTwoToneOn;
        var txStage = _dsp.CurrentEngine?.GetTxStageMeters() ?? TxStageMeters.Silent;
        var activePower = string.Equals(power.ActiveProtocol, "P2", StringComparison.OrdinalIgnoreCase)
            ? power.P2
            : power.P1;
        bool? hardwarePtt = string.Equals(keying.ActiveProtocol, "P2", StringComparison.OrdinalIgnoreCase)
            ? keying.P2PttIn
            : keying.P1HardwarePtt;

        return new TxDiagnosticsSnapshotDto(
            GeneratedUtc: generatedUtc,
            IqSourceType: _src.GetType().FullName,
            IqSourceIsRing: ReferenceEquals(_src, _ring),
            Ring: new TxRingDiagnosticsDto(
                _ring.TotalWritten,
                _ring.TotalRead,
                _ring.Count,
                _ring.Dropped,
                _ring.Capacity,
                _ring.RecentMag),
            MicUplink: micUplink,
            Ingest: new TxIngestDiagnosticsDto(
                _ingest.TotalMicSamples,
                _ingest.TotalTxBlocks,
                _ingest.DroppedFrames),
            Protocol2: p2Tx,
            AudioPath: ZeusEndpoints.BuildTxAudioPathHealth(
                generatedUtc,
                _ring.TotalWritten,
                _ring.TotalRead,
                _ring.Count,
                _ring.Dropped,
                _ring.Capacity,
                _ring.RecentMag,
                _ingest.TotalMicSamples,
                _ingest.TotalTxBlocks,
                _ingest.DroppedFrames,
                p2Tx,
                hostTxActive,
                micUplink,
                requiresMicUplink),
            Stage: ZeusEndpoints.BuildTxStageDiagnostics(txStage, txStageActive),
            Egress: ZeusEndpoints.BuildTxEgressHealth(
                generatedUtc,
                _ring.TotalWritten,
                _ring.Dropped,
                p2Tx,
                _tx.IsMoxOn,
                _tx.IsTunOn,
                _tx.IsTwoToneOn,
                hardwarePtt,
                activePower.FwdWatts,
                radioState.Mode,
                IsG2DutyGuidance(power.EffectiveBoard, power.OrionMkIIVariant)),
            TxPlugins: new TxPluginDiagnosticsDto(
                _pluginBridge.IsMasterBypassed,
                _pluginBridge.IsBypassedForRemoteTxSource),
            VstEngine: new TxVstEngineDiagnosticsDto(
                _vstEngine.IsActive,
                _vstEngine.DegradedBlocks),
            RxVstEngine: new RxVstEngineDiagnosticsDto(
                _rxVstEngine.EngineActive,
                _rxVstEngine.EngineAvailable,
                _rxVstEngine.ActivePluginCount,
                _rxVstEngine.DegradedBlocks));
    }

    private static bool IsG2DutyGuidance(string? effectiveBoard, string? variant) =>
        string.Equals(effectiveBoard, HpsdrBoardKind.OrionMkII.ToString(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(variant, OrionMkIIVariant.G2.ToString(), StringComparison.OrdinalIgnoreCase);
}

internal sealed record TxDiagnosticsSnapshotDto(
    DateTimeOffset GeneratedUtc,
    string? IqSourceType,
    bool IqSourceIsRing,
    TxRingDiagnosticsDto Ring,
    TxMicUplinkDiagnosticsDto MicUplink,
    TxIngestDiagnosticsDto Ingest,
    Protocol2TxIqDiagnostics? Protocol2,
    TxAudioPathHealthDto AudioPath,
    object Stage,
    TxEgressHealthDto Egress,
    TxPluginDiagnosticsDto TxPlugins,
    TxVstEngineDiagnosticsDto VstEngine,
    RxVstEngineDiagnosticsDto RxVstEngine);

internal sealed record TxRingDiagnosticsDto(
    long TotalWritten,
    long TotalRead,
    int Count,
    long Dropped,
    int Capacity,
    double RecentMag);

internal sealed record TxIngestDiagnosticsDto(
    long TotalMicSamples,
    long TotalTxBlocks,
    long DroppedFrames);

internal sealed record TxPluginDiagnosticsDto(
    bool MasterBypassed,
    bool BypassedForRemoteTx);

internal sealed record TxVstEngineDiagnosticsDto(
    bool Active,
    long DegradedBlocks);

internal sealed record RxVstEngineDiagnosticsDto(
    bool Active,
    bool Available,
    int ActivePlugins,
    long DegradedBlocks);
