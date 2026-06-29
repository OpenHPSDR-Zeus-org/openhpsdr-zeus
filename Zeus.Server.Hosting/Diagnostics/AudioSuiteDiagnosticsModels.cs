// SPDX-License-Identifier: GPL-2.0-or-later
//
// Snapshot DTOs for the Audio Suite (VST / native insert chain) diagnostics
// provider. Plain records (not registered in DiagnosticsJsonContext) — they
// serialise via the diagnostics endpoint's reflection resolver, the same path
// the anonymous-typed providers use. Built off the realtime path by
// AudioPluginBridge.GetAudioSuiteDiagnostics().

namespace Zeus.Server;

/// <summary>
/// Live, real-time view of the Audio Suite: which route is hot, the
/// out-of-process engine's health, and per-chain (TX / RX) latency, DSP load,
/// and fidelity counters. Read-only; built from lock-free realtime telemetry.
/// </summary>
public sealed record AudioSuiteDiagnostics(
    string ProcessingMode,            // "native-in-process" | "vst-out-of-process"
    bool OutOfProcessEngineActive,
    long OutOfProcessDegradedBlocks,  // blocks the OOP engine missed its budget (passthrough)
    AudioChainDiagnostics Tx,
    AudioChainDiagnostics Rx);

/// <summary>Per-chain realtime metrics. <see cref="DspLoadPercent"/> is the
/// headroom signal — last block's processing time as a fraction of the block
/// period; well under 100% means comfortable realtime margin.</summary>
public sealed record AudioChainDiagnostics(
    int ActivePlugins,
    int VstPlugins,
    int SumLatencySamples,
    double SumLatencyMs,
    double InPeakDbfs,
    double OutPeakDbfs,
    double LastBlockProcMicros,
    double MaxBlockProcMicros,
    double BlockPeriodMicros,
    double DspLoadPercent,
    double PeakDspLoadPercent,
    long BlocksProcessed,
    long NonFiniteRepairs,
    IReadOnlyList<ChainPluginInfo> Plugins);

/// <summary>One plugin occupying a slot in the realtime chain, in chain order,
/// with its reported processing latency.</summary>
public sealed record ChainPluginInfo(
    int Slot,
    string Name,
    bool IsVst,
    bool Bypassed,
    int LatencySamples,
    double LatencyMs);
