// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeus.Contracts;

public enum RxMode : byte
{
    LSB, USB, CWL, CWU, AM, FM, SAM, DSB, DIGL, DIGU,
    // FreeDV digital voice (Codec2 / freedv_api). Zeus-level mode only — it
    // is NOT a WDSP demod mode. At the WDSP layer FreeDV runs as USB; the
    // FreeDV modem is inserted as a streaming filter in the RX/TX audio path
    // (see FreeDvService). Append-only: byte value 10 is fixed for prefs
    // persistence — never reorder this enum.
    FreeDv,
}

// PureSignal feedback antenna source. On G2/MkII the wire-format diff
// between Internal coupler and External (Bypass) is exactly one bit
// (ALEX_RX_ANTENNA_BYPASS = 0x00000800) in alex0 when xmit && PS armed.
// pihpsdr's three-way Internal/Ext1/Bypass collapses to two on the wire
// for this hardware, so Zeus exposes a two-way selector.
public enum PsFeedbackSource : byte { Internal = 0, External = 1 }

public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

// Thetis NR-button state: Off = no NR, Anr = NR1 (time-domain LMS),
// Emnr = NR2 (Ephraim-Malah), Sbnr = NR4 (libspecbleach, issue #79).
// NR3 (RNNR) is intentionally absent: training data for the bundled RNNoise
// model is voice-corpus-only and underperforms on HF noise. All modes are
// mutually exclusive in WDSP, so the button carries them in one enum. Byte
// order is fixed — appending only — because persisted DspSettingsStore rows
// would mis-deserialize on a reorder.
[JsonConverter(typeof(NrModeJsonConverter))]
public enum NrMode : byte
{
    Off,
    Anr,
    Emnr,
    Sbnr = 3,
}

public sealed class NrModeJsonConverter : JsonConverter<NrMode>
{
    public override NrMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetByte(out var numericValue))
            return numericValue <= (byte)NrMode.Sbnr ? (NrMode)numericValue : NrMode.Off;

        if (reader.TokenType == JsonTokenType.String)
        {
            var stringValue = reader.GetString();
            if (string.Equals(stringValue, nameof(NrMode.Anr), StringComparison.OrdinalIgnoreCase)) return NrMode.Anr;
            if (string.Equals(stringValue, nameof(NrMode.Emnr), StringComparison.OrdinalIgnoreCase)) return NrMode.Emnr;
            if (string.Equals(stringValue, nameof(NrMode.Sbnr), StringComparison.OrdinalIgnoreCase)) return NrMode.Sbnr;
        }

        return NrMode.Off;
    }

    public override void Write(Utf8JsonWriter writer, NrMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            NrMode.Anr => nameof(NrMode.Anr),
            NrMode.Emnr => nameof(NrMode.Emnr),
            NrMode.Sbnr => nameof(NrMode.Sbnr),
            _ => nameof(NrMode.Off),
        });
    }
}

// Pre-RXA time-domain blanker. Nb1 = ANB (noise blanker), Nb2 = NOB (noise gate).
// Engine silently ignores this until the pre-RXA pipeline lands (task #4);
// kept in the contract so the UI shape doesn't churn when it does.
public enum NbMode : byte { Off, Nb1, Nb2 }

// RXA AGC mode. Values MUST match WDSP / Thetis enums.cs:152-162
// (FIXD=0, LONG=1, SLOW=2, MED=3, FAST=4, CUSTOM=5) — they are passed
// straight to SetRXAAGCMode and persisted as bytes in zeus-prefs.db, so the
// byte order is fixed (appending only). Med is the Thetis (and Zeus) default.
public enum AgcMode : byte { Fixed = 0, Long = 1, Slow = 2, Med = 3, Fast = 4, Custom = 5 }

// Thetis default NbThreshold = 3.3 (WDSP units), which is `0.165 × 20` — the
// Thetis UI slider sitting at 20. Kept here so REST round-trips preserve the
// UI-space value rather than the scaled one.
//
// NR2 post2 + NR4 (Sbnr) tunables are nullable so legacy state frames (no
// fields present) deserialize unchanged; null at the engine seam means
// "use the WdspDspEngine.NrDefaults baseline". Persisted globally (not
// per-band/mode/profile) per Thetis behaviour — see DspSettingsStore.
public sealed record NrConfig(
    NrMode NrMode = NrMode.Off,
    bool AnfEnabled = false,
    bool SnbEnabled = false,
    bool NbpNotchesEnabled = false,
    NbMode NbMode = NbMode.Off,
    double NbThreshold = 20.0,
    // ---- NR2 (EMNR) post2 comfort-noise tunables ----
    // Already wired in WdspDspEngine.NrDefaults; surfacing them via the
    // right-click popover. Slider scale: factor/nlevel UI 0..100 → WDSP
    // 0..1 (Thetis divides by 100). Taper is bins (0..100), Rate is
    // time-constant in seconds. Run gates the comfort-noise injection.
    bool? EmnrPost2Run = null,
    double? EmnrPost2Factor = null,
    double? EmnrPost2Nlevel = null,
    double? EmnrPost2Rate = null,
    int? EmnrPost2Taper = null,
    // ---- NR4 (SBNR / libspecbleach) tunables ----
    // Defaults from Thetis radio.cs:2350-2462. Native setters take float;
    // we marshal to double on the wire and downcast at the P/Invoke seam.
    double? Nr4ReductionAmount = null,
    double? Nr4SmoothingFactor = null,
    double? Nr4WhiteningFactor = null,
    double? Nr4NoiseRescale = null,
    double? Nr4PostFilterThreshold = null,
    int? Nr4NoiseScalingType = null,
    int? Nr4Position = null,
    // ---- NR2 (EMNR) core algorithm selectors + trained-method tuning ----
    // Thetis Setup → DSP tab radio groups + AE checkbox + T1/T2 NUDs. Defaults
    // match Thetis (Gamma=2, OSMS=0, AE on, T1=-0.5, T2=2.0). T1/T2 are only
    // consulted by WDSP when EmnrGainMethod=3 (Trained); the engine still
    // writes them through so the channel state is coherent on mode-cycle.
    int? EmnrGainMethod = null,
    int? EmnrNpeMethod = null,
    bool? EmnrAeRun = null,
    double? EmnrTrainT1 = null,
    double? EmnrTrainT2 = null);

// Direct Smart NR diagnostic surface. The Smart NR analyzer still lives in
// the frontend DSP-scene path; this DTO exposes that live condition together
// with the backend NR runtime facts used by hardware diagnostics.
public sealed record SmartNrConditionDto(
    int SchemaVersion,
    bool Available,
    string Status,
    bool Fresh,
    bool Stale,
    long? AgeMs,
    DateTimeOffset? AtUtc,
    DateTimeOffset? SourceAtUtc,
    long? SourceAgeMs,
    long? SourceClockSkewMs,
    string? SourceClientId,
    string? Mode,
    string? Profile,
    string? Reason,
    string? Recommendation,
    bool? HeldByRxChain,
    string? RxChainLabel,
    string? RxChainRecommendation,
    string? RxChainTone,
    int? RxChainScore,
    double? MaxSnrDb,
    double? CoherentMaxSnrDb,
    double? OccupiedPct,
    double? CoherentOccupiedPct,
    double? ImpulsivePct,
    int? PeakCount,
    int? CoherentPeakCount,
    bool? CoherentSubthresholdSignal,
    FrontendDspSceneTopPeakDto[] TopPeaks,
    bool? AdjacentNoiseUsable,
    int? AdjacentNoiseBins,
    int? AdjacentNoiseLeftBins,
    int? AdjacentNoiseRightBins,
    double? AdjacentNoiseFloorDb,
    double? AdjacentNoiseP10Db,
    double? AdjacentNoiseP50Db,
    double? AdjacentNoiseP90Db,
    double? AdjacentNoiseLeftFloorDb,
    double? AdjacentNoiseRightFloorDb,
    double? AdjacentNoiseSlopeDbPerKhz,
    double? AdjacentNoiseRejectedPct,
    bool WdspActive,
    bool WdspNativeLoadable,
    bool WdspEmnrPost2Available,
    bool WdspNr4SbnrAvailable,
    string Nr4Readiness,
    string RequestedNrMode,
    string EffectiveNrMode,
    string? ExpectedNrMode,
    bool? RuntimeAligned,
    string RuntimeAlignmentStatus,
    string RuntimeAlignmentRecommendation,
    SmartNrRxChainRuntimeDto RxChain,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

public sealed record FrontendDspSceneTopPeakDto(
    long FrequencyHz,
    int OffsetHz,
    double SnrDb,
    double Dbfs,
    double? Confidence,
    bool Coherent);

// Machine-readable benchmark plan for WDSP modernization. These scenarios are
// evidence requirements, not runtime DSP behavior.
public sealed record DspBenchmarkScenarioDto(
    int SchemaVersion,
    string Id,
    string Name,
    string Phase,
    string SignalPath,
    string FixtureStatus,
    string[] AppliesTo,
    string[] RequiredComparisons,
    string[] RequiredMetrics,
    string[] AcceptanceGates,
    string[] RequiredArtifacts,
    string[] FailureModes,
    string[] RelatedTools);

public sealed record DspBenchmarkMetricDto(
    int SchemaVersion,
    string Id,
    string Name,
    string Direction,
    string AcceptanceThreshold,
    string AcceptanceComparator,
    string Unit,
    string SafetyClass,
    string[] AcceptanceScopes,
    string Rationale,
    string[] RelatedScenarios);

public sealed record DspBenchmarkMetricCatalogDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string Status,
    string RolloutPolicy,
    string[] DirectionValues,
    string[] ComparatorValues,
    DspBenchmarkMetricDto[] Metrics);

public sealed record DspBenchmarkPlanDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string Status,
    string RolloutGate,
    string FirstHardwareTarget,
    string[] RequiredHardwareBeforeGraduation,
    string[] RequiredComparisons,
    string[] GlobalAcceptanceGates,
    DspBenchmarkScenarioDto[] Scenarios);

public sealed record DspBenchmarkCaptureArtifactDto(
    int SchemaVersion,
    string Id,
    string Kind,
    string Source,
    string Purpose,
    string Cadence,
    bool Required,
    string[] ScenarioIds);

public sealed record DspBenchmarkCaptureManifestDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string ManifestId,
    string Status,
    bool ReadyForCapture,
    string CaptureGate,
    string HardwareTarget,
    string LiveDiagnosticsStatus,
    int LiveReadinessScore,
    string LiveDiagnosticsEndpoint,
    string BenchmarkPlanEndpoint,
    string ExternalEngineCandidatesEndpoint,
    string[] ScenarioIds,
    string[] RequiredComparisons,
    string[] GlobalAcceptanceGates,
    string[] PreflightChecks,
    string[] StopConditions,
    string[] Constraints,
    string[] RecommendedActions,
    DspBenchmarkCaptureArtifactDto[] RequiredArtifacts,
    string[] OperatorNotes);

public sealed record DspModernizationEvidenceSnapshotDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string SnapshotId,
    string Status,
    int EvidenceCompletenessScore,
    bool ReadyForLiveBenchmark,
    bool ReadyForCapture,
    string RolloutGate,
    string HardwareTarget,
    string[] IncludedEndpoints,
    string[] IncludedArtifacts,
    string[] MissingEvidence,
    string[] NextActions,
    SmartNrConditionDto SmartNrCondition,
    DspLiveDiagnosticsDto LiveDiagnostics,
    DspBenchmarkPlanDto BenchmarkPlan,
    DspBenchmarkCaptureManifestDto CaptureManifest,
    DspExternalEngineCandidateDto[] ExternalEngineCandidates);

// Read-only catalog entry for optional external DSP/ML engines. These are
// candidates for post-demod audio bakeoffs, not replacements for WDSP IQ
// processing or approval to change operator defaults.
public sealed record DspExternalEngineCandidateDto(
    int SchemaVersion,
    string Id,
    string Name,
    string Family,
    string IntegrationPoint,
    string DefaultState,
    string RolloutPolicy,
    string EvaluationStage,
    string[] AllowedSignalPaths,
    string[] ForbiddenSignalPaths,
    string[] RequiredControls,
    string FallbackPolicy,
    string License,
    string PackagingStatus,
    string RuntimeRisk,
    string LatencyRisk,
    string RadioSafetyRisk,
    string[] Strengths,
    string[] RequiredBenchmarks,
    string[] RequiredEvidence,
    string[] Blockers,
    string[] ReferenceUrls);

public sealed record DspLiveRuntimeEvidenceDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string Status,
    bool RxMetersFresh,
    bool RxMetersStale,
    long? RxMetersAgeMs,
    double? RxDbm,
    double? AdcHeadroomDb,
    double? AgcGainDb,
    bool AudioFresh,
    bool AudioStale,
    long? AudioAgeMs,
    string AudioStatus,
    string AudioSource,
    long AudioFramesBroadcast,
    uint AudioLastSeq,
    int AudioSampleRateHz,
    int AudioSampleCount,
    double? AudioRmsDbfs,
    double? AudioPeakDbfs,
    bool TxMonitorRequested,
    bool SquelchEnabled,
    bool SquelchOpen,
    bool SquelchTailActive,
    double? SquelchGateGain,
    double? RxAudioLevelerInputRmsDbfs,
    double? RxAudioLevelerOutputRmsDbfs,
    double? RxAudioLevelerInputPeakDbfs,
    double? RxAudioLevelerOutputPeakDbfs,
    double? RxAudioLevelerDesiredGainDb,
    double? RxAudioLevelerAppliedGainDb,
    double? RxAudioLevelerGainDeltaDb,
    double? RxAudioLevelerPeakHeadroomDb,
    double? RxAudioLevelerPreLimitPeakDbfs,
    double? RxAudioLevelerOutputLimitReductionDb,
    int? RxAudioLevelerOutputLimitSampleCount,
    int? RxAudioLevelerPauseHoldBlocks,
    bool? RxAudioLevelerBoostSlewLimited,
    bool? RxAudioLevelerPeakLimited,
    bool? RxAudioLevelerOutputLimited,
    long MonitorBacklogSamples,
    int AudioSinkCount,
    string DiagnosticRecommendation);

// Tool-facing live DSP modernization summary. This fuses the Smart NR scene,
// WDSP runtime capability, RX-chain health, and post-demod external-engine
// bakeoff readiness into one read-only gate for G2/on-air benchmarking. It is
// diagnostic evidence only:
// RolloutGate remains opt-in unless a separate benchmark + hardware review
// approves changing defaults.
public sealed record DspLiveDiagnosticsDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string Status,
    string QualityTone,
    int ReadinessScore,
    bool ReadyForLiveBenchmark,
    bool ReadyForExternalEngineBakeoff,
    string ExternalEngineBakeoffStatus,
    string[] ExternalEngineBakeoffConstraints,
    string RolloutGate,
    bool WdspActive,
    bool WdspNativeLoadable,
    bool WdspEmnrPost2Available,
    bool WdspNr4SbnrAvailable,
    string Nr4Readiness,
    bool FrontendSceneAvailable,
    string FrontendSceneStatus,
    bool FrontendSceneFresh,
    bool FrontendSceneStale,
    long? FrontendSceneAgeMs,
    FrontendDspSceneTopPeakDto[] FrontendTopPeaks,
    bool? FrontendAdjacentNoiseUsable,
    int? FrontendAdjacentNoiseBins,
    int? FrontendAdjacentNoiseLeftBins,
    int? FrontendAdjacentNoiseRightBins,
    double? FrontendAdjacentNoiseFloorDb,
    double? FrontendAdjacentNoiseP10Db,
    double? FrontendAdjacentNoiseP50Db,
    double? FrontendAdjacentNoiseP90Db,
    double? FrontendAdjacentNoiseLeftFloorDb,
    double? FrontendAdjacentNoiseRightFloorDb,
    double? FrontendAdjacentNoiseSlopeDbPerKhz,
    double? FrontendAdjacentNoiseRejectedPct,
    string? SmartNrProfile,
    string? ExpectedNrMode,
    bool? RuntimeAligned,
    string RuntimeAlignmentStatus,
    string RequestedNrMode,
    string EffectiveNrMode,
    bool? HeldByRxChain,
    int? RxChainScore,
    string? RxChainTone,
    string? RxChainLabel,
    int? RxChainFilterLowHz,
    int? RxChainFilterHighHz,
    int? RxChainFilterWidthHz,
    string? RxChainFilterPresetName,
    long? RadioVfoHz,
    long? RadioLoHz,
    string? RadioMode,
    bool? RadioCtunEnabled,
    int? RadioSampleRate,
    DspLiveRuntimeEvidenceDto? RuntimeEvidence,
    string[] Evidence,
    string[] Constraints,
    string[] RecommendedActions,
    string[] CandidateTools,
    string BenchmarkPlanEndpoint,
    int BenchmarkScenarioCount,
    string[] NextBenchmarkScenarios,
    string[] BenchmarkAcceptanceGates,
    DspExternalEngineCandidateDto[] ExternalEngineCandidates,
    string DiagnosticRecommendation);

// Read-only audit of what the connected/effective hardware can do versus
// what Zeus currently exposes as a safe control. This is deliberately
// diagnostics-only: write controls must be added through board-gated APIs
// after the exact wire mapping is verified.
public sealed record HardwareSampleRateCapabilityDto(
    int RateHz,
    string Label,
    bool SupportedByBoard,
    bool SupportedByActiveProtocol,
    bool CurrentlySelected,
    string Status,
    string Notes);

public sealed record HardwarePotentialItemDto(
    string Id,
    string Title,
    string Category,
    string ManualCapability,
    string CurrentExposure,
    string ImplementationStatus,
    string SafetyClass,
    bool UserConfigurable,
    string[] TelemetryPaths,
    string[] CurrentControls,
    string[] Blockers,
    string NextStep);

public sealed record HardwarePotentialDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string ConnectedBoard,
    string EffectiveBoard,
    string OrionMkIIVariant,
    bool G2Class,
    string ActiveProtocol,
    int CurrentSampleRateHz,
    int MaxRxSampleRateHz,
    int[] FullRxSampleRateLadderHz,
    HardwareSampleRateCapabilityDto[] SampleRates,
    HardwarePotentialItemDto[] Items,
    string[] DitherRandomAudit,
    string[] FilterAndWindowAudit,
    string DiagnosticRecommendation);

public sealed record SmartNrRxChainRuntimeDto(
    int SchemaVersion,
    string Source,
    int FilterLowHz,
    int FilterHighHz,
    int FilterWidthHz,
    string? FilterPresetName,
    bool AutoAgcEnabled,
    string AgcMode,
    double AgcTopDb,
    double AgcOffsetDb,
    double EffectiveAgcTopDb,
    bool AutoAttEnabled,
    bool AdcProtectionEnabled,
    int AttenDb,
    int AttOffsetDb,
    int EffectiveAttenDb,
    bool AdcOverloadWarning,
    int AdcOverloadLevel,
    byte LastOverloadBits,
    ushort? Adc0MaxMagnitude,
    ushort? Adc1MaxMagnitude,
    ushort Adc0MaxMagnitudeAtOverload,
    ushort Adc1MaxMagnitudeAtOverload,
    DateTimeOffset? LastAdcTelemetryUtc,
    bool SquelchEnabled,
    bool SquelchAdaptive,
    int SquelchLevel,
    bool PreampOn)
{
    public static SmartNrRxChainRuntimeDto Unknown { get; } = new(
        SchemaVersion: 2,
        Source: "unknown",
        FilterLowHz: 0,
        FilterHighHz: 0,
        FilterWidthHz: 0,
        FilterPresetName: null,
        AutoAgcEnabled: false,
        AgcMode: "unknown",
        AgcTopDb: 0,
        AgcOffsetDb: 0,
        EffectiveAgcTopDb: 0,
        AutoAttEnabled: false,
        AdcProtectionEnabled: false,
        AttenDb: 0,
        AttOffsetDb: 0,
        EffectiveAttenDb: 0,
        AdcOverloadWarning: false,
        AdcOverloadLevel: 0,
        LastOverloadBits: 0,
        Adc0MaxMagnitude: null,
        Adc1MaxMagnitude: null,
        Adc0MaxMagnitudeAtOverload: 0,
        Adc1MaxMagnitudeAtOverload: 0,
        LastAdcTelemetryUtc: null,
        SquelchEnabled: false,
        SquelchAdaptive: true,
        SquelchLevel: 0,
        PreampOn: false);
}

public sealed record ExternalPttStatusDto(
    int SchemaVersion,
    bool Available,
    string Protocol,
    bool? HardwarePtt,
    bool? CwKeyDown,
    bool OwnedMox,
    int HangTimeMs,
    bool MoxOn,
    bool TunOn,
    bool TwoToneOn,
    string? MoxOwner,
    bool CwMode,
    bool SidetoneAvailable,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc,
    // Hardware-PTT-IN → MOX enable gate (per-install, default OFF). Nullable so
    // the read-only diagnostic snapshot (/api/tx/diag, /api/cw/hardware-keying)
    // can omit it (null) while the dedicated /api/radio/ptt-status endpoint
    // populates it from PttSettingsStore. When the gate is OFF the PTT-IN lamp
    // still tracks the footswitch, but no MOX is promoted.
    bool? Enabled = null);

// Request body for PUT /api/radio/ptt-status — flips the per-install
// hardware-PTT-IN → MOX enable gate. Persisted in PttSettingsStore; a
// persisted ON flag only ARMS the gate, it never auto-keys MOX (MOX still
// requires a physical footswitch edge).
public sealed record PttEnableSetRequest(bool Enabled);

// State of the ANAN G2 / G2-Ultra hardware front-panel bridge — body of
// GET/PUT /api/radio/front-panel. The Enabled/DevicePath/Baud trio are the
// operator's stored settings (DevicePath empty + Baud 0 = auto-detect); the
// Connected/Active*/PanelType trio is the live bridge status (Connected =
// serial line open; PanelType 5 = a recognised G2-Ultra handshake).
public sealed record G2PanelSettingsDto(
    bool Enabled,
    string? DevicePath,
    int Baud,
    bool Connected,
    string? ActiveDevicePath,
    int ActiveBaud,
    int PanelType);

// Request body for PUT /api/radio/front-panel. Every field optional — only the
// supplied ones change. DevicePath "" clears the override (back to auto-detect);
// Baud 0 = auto.
public sealed record G2PanelSettingsSetRequest(
    bool? Enabled = null,
    string? DevicePath = null,
    int? Baud = null);

public sealed record HardwareKeyingStatusDto(
    int SchemaVersion,
    string? ActiveProtocol,
    long P1Packets,
    DateTimeOffset? P1LastUpdatedUtc,
    bool? P1HardwarePtt,
    bool? P1CwKeyDown,
    long P2Packets,
    DateTimeOffset? P2LastUpdatedUtc,
    bool? P2PttIn,
    bool? P2DotIn,
    bool? P2DashIn,
    bool? P2SidetoneActive,
    ExternalPttStatusDto ExternalPtt,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

public sealed record RadioPowerReadingDto(
    long Packets,
    DateTimeOffset? LastUpdatedUtc,
    ushort? ExciterAdc,
    ushort? FwdAdc,
    ushort? RevAdc,
    double? FwdWatts,
    double? RefWatts,
    double? Swr);

public sealed record RadioPowerCalibrationDto(
    int SchemaVersion,
    string? ActiveProtocol,
    string ConnectedBoard,
    string EffectiveBoard,
    string OrionMkIIVariant,
    string CalibrationBoard,
    double BridgeVolt,
    double RefVoltage,
    int AdcCalOffset,
    double CalibrationMaxWatts,
    bool CalibrationFallbackApplied,
    double CapabilityMaxPowerWatts,
    RadioPowerReadingDto P1,
    RadioPowerReadingDto P2,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

public sealed record RadioSupplyReadingDto(
    long Packets,
    DateTimeOffset? LastUpdatedUtc,
    ushort? SupplyVoltsAdc,
    double? SupplyVolts,
    double? RawScaledSupplyVolts,
    bool SupplyVoltsTrusted,
    string ScaleStatus);

public sealed record RadioSupplyAlarmsDto(
    int SchemaVersion,
    string? ActiveProtocol,
    string EffectiveBoard,
    string OrionMkIIVariant,
    bool SupportsSupplyTelemetry,
    int AdcSupplyMv,
    bool ActiveThresholdsConfigured,
    bool AlarmActive,
    string AlarmStatus,
    RadioSupplyReadingDto P1,
    RadioSupplyReadingDto P2,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

public sealed record RadioPaThermalDiagnosticsDto(
    int SchemaVersion,
    string? ActiveProtocol,
    string ConnectedBoard,
    string EffectiveBoard,
    string OrionMkIIVariant,
    bool SupportsTemperatureTelemetry,
    bool TemperatureDecoded,
    bool TemperatureAvailable,
    string Source,
    string Status,
    double? TempC,
    double? RawAdc,
    long? AgeMs,
    DateTimeOffset? LastUpdatedUtc,
    double WarningTempC,
    double CriticalTempC,
    string ManualReference,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

public sealed record RadioNetworkCountersDto(
    bool Attached,
    long TotalFrames,
    long DroppedFrames,
    double DropRatioPct,
    long? HiPriorityPackets,
    long? PsPairedPackets);

public sealed record RadioNetworkProfileDto(
    int SchemaVersion,
    string ConnectionStatus,
    string? Endpoint,
    string? ActiveProtocol,
    int SampleRateHz,
    string ConnectedBoard,
    string EffectiveBoard,
    string OrionMkIIVariant,
    string Transport,
    RadioNetworkCountersDto P1,
    RadioNetworkCountersDto P2,
    string HealthStatus,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

public sealed record UserIoLineDto(
    string Id,
    string Kind,
    string Label,
    ushort? RawAdc,
    double? NormalizedPct,
    bool? DigitalState);

public sealed record UserIoLabelsDto(
    int SchemaVersion,
    string? ActiveProtocol,
    bool P2Attached,
    long P2Packets,
    DateTimeOffset? P2LastUpdatedUtc,
    IReadOnlyList<UserIoLineDto> Lines,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

public sealed record UserIoActionsDto(
    int SchemaVersion,
    string? ActiveProtocol,
    bool P2Attached,
    long P2Packets,
    DateTimeOffset? P2LastUpdatedUtc,
    bool ActionBindingsConfigured,
    IReadOnlyList<UserIoLineDto> Lines,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

// Operator-facing AGC configuration (issue: DSP controls Thetis parity §4).
// Mode selects a canned profile (Long/Slow/Med/Fast/Fixed) or Custom; the
// nullable params are only consulted in Custom mode (and FixedGainDb only in
// Fixed mode) — null at the engine seam means "use the canned-preset value".
// AGC max-gain ("top") is NOT carried here: it stays on StateDto.AgcTopDb with
// its own /api/agcGain path and auto-AGC loop. UI ranges (Thetis radio.cs):
// Slope 0..20 (engine multiplies ×10), Decay/Hang 1..5000 ms, HangThreshold
// 0..100 %, FixedGainDb -20..120 dB. Default mode Med matches Thetis.
public sealed record AgcConfig(
    AgcMode Mode = AgcMode.Med,
    int? Slope = null,
    int? DecayMs = null,
    int? HangMs = null,
    int? HangThreshold = null,
    double? FixedGainDb = null);

// Operator-facing RX squelch configuration (issue: DSP controls Thetis parity
// §5). A single mode-aware control: the engine routes run + threshold to the
// WDSP squelch stage matching the current RX mode (SSB/CW → SSQL, AM/SAM →
// AMSQ, FM → FMSQ) and clears the other two. Adaptive=true uses the live
// S-meter/noise-floor gate in DspPipelineService; Adaptive=false uses WDSP's
// fixed per-mode squelch stages. Level is a unitless 0..100 where higher =
// tighter squelch. FixedSensitivity shapes the fixed-mode level mapping:
// higher values keep weak/moderate signals open more easily. Defaults
// Enabled=false, Level=0, Adaptive=true keep squelch off while making new/old
// clients land on the more intuitive dynamic mode once enabled. Persisted
// globally via DspSettingsStore — same pattern as Agc/Nr.
public sealed record SquelchConfig(
    bool Enabled = false,
    int Level = 0,
    bool Adaptive = true,
    int FixedSensitivity = 70)
{
    public const int MinFixedSensitivity = 0;
    public const int MaxFixedSensitivity = 100;
    public const int DefaultFixedSensitivity = 70;
}

// Operator-facing TX leveling configuration (issue: DSP controls Thetis parity
// §6.1-6.3). Bundles the three TXA dynamics stages the operator reaches for:
// ALC (max-gain + decay — the ALC run state is ALWAYS on, never exposed; the
// SSB modulator emits zero IQ if ALC is off, see NativeMethods.SetTXAALCSt),
// the Leveler (operator on/off + decay), and the Compressor/CPDR (on/off +
// gain). The Leveler MAX-GAIN ("top") is intentionally NOT carried here — it
// stays on StateDto.LevelerMaxGainDb with its own /api/tx/leveler-max-gain
// path. Ranges/defaults mirror Thetis verbatim (radio.cs / setup.designer.cs):
// AlcMaxGainDb 0..120 (default 3), AlcDecayMs 1..50 (default 10), LevelerEnabled
// default on, LevelerDecayMs 1..5000 (default 100), CompressorEnabled default
// off, CompressorGainDb 0..20 (default 0). Persisted globally via
// DspSettingsStore — same pattern as Agc/Squelch.
public sealed record TxLevelingConfig(
    double AlcMaxGainDb = 3.0,
    int AlcDecayMs = 10,
    bool LevelerEnabled = true,
    int LevelerDecayMs = 100,
    bool CompressorEnabled = false,
    double CompressorGainDb = 0.0);

// A notch filter (MNF) — a band the operator paints, or Signal Intelligence
// auto-detects, to remove EMF/birdies from the RX audio via WDSP's notch
// database (nbp.c). CenterHz/WidthHz are ABSOLUTE RF in Hz (WDSP repositions
// them as the radio tunes, via RXANBPSetTuneFrequency). Active mirrors the
// per-notch enable flag. Source is null/manual for operator notches and "auto"
// for live-detected EMF bars that the frontend may refresh independently.
public sealed record NotchDto(double CenterHz, double WidthHz, bool Active = true, string? Source = null);

// Full notch list — the client posts the complete set on every change (and on
// connect), so the server/engine never has to reconcile deltas.
public sealed record NotchListRequest(IReadOnlyList<NotchDto> Notches);

/// <summary>Single source of truth for the SignalR / JSON wire contract
/// version and the receiver-DDC ceiling shared between server and frontend.
/// </summary>
public static class WireContract
{
    /// <summary>Broadcast contract version. v1 was the implicit pre-multi-DDC
    /// baseline (no <see cref="StateDto.Receivers"/>); v2 introduces the
    /// per-receiver <see cref="StateDto.Receivers"/> array so the frontend can
    /// feature-detect multi-DDC support. Surfaced on the wire via
    /// <see cref="StateDto.WireVersion"/>.</summary>
    public const int Version = 2;

    /// <summary>Maximum concurrent DDC receivers. Protocol 2's DDC-enable
    /// command is a single byte (8 bits ⇒ DDC0..DDC7), so the ceiling is 8.
    /// <see cref="Zeus.Contracts"/> owns the canonical value;
    /// <c>Zeus.Protocol2.Protocol2Client.MaxRxDdc</c> references it so the wire
    /// foundation and the contract can never drift apart.</summary>
    public const int MaxReceivers = 8;
}

/// <summary>Per-receiver (per-DDC) state for the multi-DDC model. Index 0 is
/// RX1, index 1 is RX2, and indices ≥ 2 are additional DDCs (up to
/// <see cref="WireContract.MaxReceivers"/> − 1).
/// <para>The first usable dual-receive path mirrors the <see cref="StateDto"/>
/// flat RX1 fields (<see cref="StateDto.VfoHz"/> etc.) into index 0 and the
/// RX2 / VFO-B fields (<see cref="StateDto.VfoBHz"/> etc.) into index 1;
/// <see cref="Zeus.Server"/>'s RadioService projects them on every state
/// change. Additional DDCs live only in this array. The frontend migrates from
/// the flat fields to this array (multi-DDC UI), after which the flat dupes are
/// retired.</para></summary>
public sealed record ReceiverDto(
    int Index,
    bool Enabled,
    // Which phase-synchronous 16-bit ADC feeds this DDC (0 or 1). Defaults to
    // ADC0; per-DDC ADC assignment is exposed in Settings by the multi-DDC UI.
    byte AdcSource,
    long VfoHz,
    RxMode Mode,
    int FilterLowHz,
    int FilterHighHz,
    string? FilterPresetName,
    double AfGainDb,
    int SampleRateHz,
    // Per-receiver audio mute (Thetis chkMUT / chkRX2Mute — RXOutputGain=0).
    // Distinct from Rx2AudioMode routing: muting silences this receiver's audio
    // contribution while leaving every other receiver audible. Defaults false so
    // pre-mute wire frames deserialize unchanged. Mirrors the per-RX MUTE_RX1/
    // RX2 keypad controls.
    bool Muted = false);

public sealed record StateDto(
    ConnectionStatus Status,
    string? Endpoint,
    long VfoHz,
    RxMode Mode,
    int FilterLowHz,
    int FilterHighHz,
    int SampleRate,
    double AgcTopDb = 90.0,
    // AGC mode + custom params (issue: DSP controls Thetis parity §4). Nullable
    // so legacy state frames (no Agc field) deserialize unchanged; null at the
    // engine seam means "use the Med canned profile". Persisted globally via
    // DspSettingsStore — same pattern as Nr. The AGC max-gain ("top") stays on
    // AgcTopDb above; Agc only carries mode + the custom/fixed tunables.
    AgcConfig? Agc = null,
    // RX squelch (issue: DSP controls Thetis parity §5). Nullable so legacy
    // state frames (no Squelch field) deserialize unchanged; null at the engine
    // seam means "squelch off" (SquelchConfig default). Persisted globally via
    // DspSettingsStore — same pattern as Agc. The engine picks the WDSP squelch
    // stage from the live RX mode and clears the others.
    SquelchConfig? Squelch = null,
    // TX leveling (issue: DSP controls Thetis parity §6.1-6.3). Nullable so
    // legacy state frames (no TxLeveling field) deserialize unchanged; null at
    // the engine seam means "use the TxLevelingConfig defaults" (ALC 3 dB/10 ms,
    // Leveler on/100 ms, Compressor off). Persisted globally via DspSettingsStore
    // — same pattern as Agc/Squelch. The Leveler max-gain stays on
    // LevelerMaxGainDb below; TxLeveling never duplicates it.
    TxLevelingConfig? TxLeveling = null,
    // User-baseline attenuator in dB, 0..31. Hardware receives
    // <c>AttenDb + AttOffsetDb</c> (clamped to 31) while auto-ATT is engaged.
    // Default is 0 — auto-ATT ramps the offset up on observed ADC overloads.
    int AttenDb = 0,
    NrConfig? Nr = null,
    int ZoomLevel = 1,
    // Auto-attenuator control loop. When on (default), the server raises
    // AttOffsetDb by 1 per ~100 ms window in which any ADC-overload bit was
    // seen, and decays it by 1 per clean window. Ported from Thetis
    // console.cs:22167 (handleOverload).
    bool AutoAttEnabled = true,
    int AttOffsetDb = 0,
    // Red-lamp flag derived from Thetis' overload-level counter
    // (+2 per overload cycle, -1 per clean, clamped 0..5, warn when >3).
    bool AdcOverloadWarning = false,
    // Currently active filter preset slot name (e.g. "F6", "VAR1"). Null when
    // the filter was set by a drag edit without a named slot context.
    string? FilterPresetName = null,
    // Advanced-filter ribbon visibility, persisted across server restarts via
    // FilterPresetStore so the operator's close-the-ribbon choice sticks.
    bool FilterAdvancedPaneOpen = false,
    // TX bandpass filter (WDSP TXA SetTXABandpassFreqs). Signed per sideband
    // like RX FilterLowHz/HighHz: USB positive, LSB negative, AM/FM symmetric
    // around 0. RadioService keeps per-mode-family memory so switching USB →
    // LSB flips the sign and USB → AM swaps to AM's remembered width.
    // Default 150/2850 matches Thetis's stock SSB TX bandpass.
    int TxFilterLowHz = 150,
    int TxFilterHighHz = 2850,
    // Master RX AF gain in dB. 0 dB ≡ WDSP SetRXAPanelGain1(1.0), the
    // engine's open-time default — a fresh session that never touches this
    // field is audibly identical to pre-#77 builds. Operator slider range
    // is −50..+20 dB (see RadioService.SetRxAfGain). Per-RX not supported
    // yet; when multi-RX lands this becomes the master and the per-RX
    // values layer on top.
    double RxAfGainDb = 0.0,
    // TX mic gain in dB. WDSP applies via SetTXAPanelGain1(10^(db/20)); the
    // server stores the operator-friendly dB and converts at the engine seam.
    // Range matches the /api/mic-gain endpoint clamp ([-40, +10]) which in
    // turn matches Thetis's MicGainMin/Max defaults (console.cs:19151/19163).
    // Persisted server-side via RadioStateStore so a fresh frontend connect
    // (or a desktop relaunch that wiped localStorage) lands on the last
    // operator value instead of the engine's 0 dB seed. Wire name omits the
    // Tx prefix to match the existing /api/mic-gain endpoint POST response.
    int MicGainDb = 0,
    // TX Leveler max-gain ceiling in dB. Range [0, 20] (Thetis parity) — 0
    // disables the headroom entirely; Thetis's stock default is 15
    // (radio.cs:2979 tx_leveler_max_gain = 15.0). Default 8.0 matches
    // WdspDspEngine.DefaultLevelerMaxGainDb (Brian's HL2 starting point;
    // softer than Thetis stock). Persisted server-side; previously
    // localStorage-only on the client and reverted on every restart. Wire
    // name matches the existing /api/tx/leveler-max-gain endpoint response.
    double LevelerMaxGainDb = 8.0,
    // Auto-AGC control loop. When on, the server automatically adjusts
    // AgcTopDb based on signal conditions. Similar to Auto-ATT but for AGC.
    // Default is OFF — operator must explicitly enable. The control loop
    // adjusts AgcOffsetDb, which is added to the user baseline AgcTopDb.
    bool AutoAgcEnabled = false,
    double AgcOffsetDb = 0.0,
    // AGC threshold ("knee") in operator/displayed dBm — the signal-relative
    // level below which the AGC applies increasing gain (up to the AgcTopDb
    // cap). This is the smooth, signal-relative control Thetis exposes via the
    // panadapter knee line (WDSP SetRXAAGCThresh). NULL = operator has not set
    // the knee, so WDSP's per-mode default threshold is left in effect (no
    // behavioural change vs. pre-#741). When set, the server converts displayed
    // dBm → WDSP scale with the per-board RX meter offset before pushing it.
    double? AgcThresholdDbm = null,

    // ---- PureSignal predistortion (TXA-side; WDSP calcc/iqc stages) ----
    // PsEnabled is the master arm bit. Persisted server-side as a standing
    // operator preference so PS stays armed across restarts until changed.
    // Actual transmit/keying actions (MOX/TUN/TwoToneEnabled) remain
    // session-only.
    bool PsEnabled = false,
    // PsMonitorEnabled — operator-facing "Monitor PA output" toggle
    // (issue #121). When true AND PsEnabled AND PS has converged
    // (info[14]==1), DspPipelineService.Tick switches the TX panadapter /
    // waterfall source from the post-CFIR predistorted-IQ analyzer to the
    // PS-feedback analyzer fed from the radio's loopback ADC, so the
    // operator sees the actual on-air signal. Default off — preserves the
    // Thetis-style predistorted view. Hidden / disabled in the UI on
    // boards that have no PS feedback path (e.g. HermesLite2). NOT
    // persisted server-side: this is an operator viewing preference,
    // resets to off each session.
    bool PsMonitorEnabled = false,
    // TX Monitor — operator-facing preview toggle (issue #106 follow-up).
    // When true, the engine demodulates the post-CFIR TX IQ back to mono
    // baseband audio so the operator hears the chain output (mic → EQ →
    // Leveler → VST → CFC → ALC → bandpass) at the actual TX bandwidth
    // profile. Equivalent to Thetis MON, but also runs the chain when MOX
    // is OFF so VST plugins receive samples and meters animate continuously.
    // RX audio is suppressed in the broadcast while monitor is on so the
    // operator hears only the TX preview. NOT persisted across sessions —
    // resets to off each connect, matching MOX/TUN discipline.
    bool TxMonitorEnabled = false,
    bool PsAuto = true,             // continuous adapt by default once armed
    bool PsSingle = false,          // one-shot SetPSControl(1,1,0,0)
    bool PsPtol = false,            // false = strict 0.4; true = relax 0.8
    bool PsAutoAttenuate = true,
    double PsMoxDelaySec = 0.2,
    double PsLoopDelaySec = 0.0,
    double PsAmpDelayNs = 150.0,
    // PS hardware peak — set per protocol/hardware by RadioService at connect
    // time. P1 = 0.4072 (Hermes/ANAN-10/100); P2 OrionMkII/Saturn = 0.6121;
    // P2 ANAN-7000/8000 = 0.2899. Default here (P1) is a safe neutral; the
    // RadioService HW-peak switch overrides on the first ConnectAsync /
    // ConnectP2Async. See PLAN section 7 / hermes.md §7.1.
    double PsHwPeak = 0.4072,
    // PS hardware-peak per-board default — frozen at connect time from
    // ResolvePsHwPeak(isProtocol2, board) and surfaced for the UI to compare
    // against the live PsHwPeak so the operator gets a "differs from default"
    // hint when they've dialed away from the factory curve.
    // mi0bot ref: PSForm.cs:830 `pbWarningSetPk.Visible = _PShwpeak !=
    // HardwareSpecific.PSDefaultPeak;` + clsHardwareSpecific.cs:303-328
    // PSDefaultPeak per-board switch.
    double PsHwPeakDefault = 0.4072,
    // PS TX feedback attenuation (dB) currently applied to the radio's
    // feedback path. Surfaced so the operator can set it directly — a manual
    // alternative to AutoAttenuate for a fixed external-tap chain — and see
    // the persisted value restored on connect. Written by the AutoAttenuate
    // dance, the manual control, and the connect-time restore.
    int PsTxFeedbackAttenuationDb = 0,
    // Per-board minimum for the above. HL2's AD9866 TX PGA reaches -28 dB;
    // the bare-HPSDR / P2 step attenuator floors at 0. Max is 31 everywhere.
    int PsTxFeedbackAttenuationDbMin = 0,
    PsFeedbackSource PsFeedbackSource = PsFeedbackSource.Internal,
    string PsIntsSpiPreset = "16/256",
    double PsFeedbackLevel = 0.0,   // info[4] read-back, 0..256
    byte PsCalState = 0,            // info[15] enum
    bool PsCorrecting = false,      // info[14]
    // Set by PsAutoAttenuateService when calcc has been alive (PS armed +
    // keyed) for >5 s without producing a fit (CalibrationAttempts pinned
    // at 0). Almost always means hw_peak is set higher than the actual TX
    // envelope peak — calcc bin 15 never fills so COLLECT never advances.
    // Frontend shows a banner pointing the operator at HW peak. See
    // PsAutoAttenuateService stall detection + project_hl2_ps_hwpeak_calibration.
    bool PsCalibrationStalled = false,
    // ---- TwoTone test generator (TXA PostGen mode=1; protocol-agnostic) ----
    // Standard PureSignal calibration excitation. Defaults match pihpsdr's
    // TwoTone defaults — 700/1900 Hz, 0.49 linear amplitude per tone.
    bool TwoToneEnabled = false,
    double TwoToneFreq1 = 700.0,
    double TwoToneFreq2 = 1900.0,
    double TwoToneMag = 0.49,

    // ---- CFC (Continuous Frequency Compressor) — issue #123 ----
    // Nullable so legacy state frames (no Cfc field) deserialize unchanged;
    // null at the engine seam means "use CfcConfig.Default" — same pattern
    // as the Nr field above. Persisted globally via DspSettingsStore.
    CfcConfig? Cfc = null,

    // ---- Drive slider state ----
    // Operator drive slider position 0..100 (% of MaxPowerWatts via the
    // per-board PA-gain table). Server is authoritative: persisted to LiteDB
    // via RadioStateStore, hydrated on construction, and broadcast on every
    // SetDrive so a fresh frontend connect lands on the persisted value
    // instead of pushing its own localStorage default back over the wire.
    // Default 0 mirrors RadioService._drivePct seed.
    int DrivePct = 0,
    // Independent TUN drive slider 0..100. Same persistence pattern as
    // DrivePct. Default 10 mirrors RadioService._tunePct seed — a 0 default
    // would make pressing TUN appear to do nothing on first key.
    int TunePct = 10,

    // ---- TX pre-key (MOX) delay ----
    // Milliseconds to withhold modulated RF after a UI MOX/TUNE key-down so an
    // external amplifier's T/R relay has time to settle before RF appears
    // (Thetis "RF Delay" parity; issue #630). Zeus keys only via the software
    // MOX bit — there is no hardware PTT-OUT line — so this is framed as a MOX
    // delay. 0..500, default 0 = no behaviour change. The keying bit is still
    // asserted immediately on key-down; only the IQ is muted (replaced with
    // silence, never dropped — dropping starves the P2 DUC FIFO). Persisted to
    // LiteDB via RadioStateStore, same pattern as DrivePct. The setter clamps
    // this strictly below the PureSignal MOX hold-off so PS can never try to
    // calibrate on muted RF — see RadioService.SetTxMoxPreKeyDelayMs.
    int TxMoxPreKeyDelayMs = 0,

    // Hardware NCO frequency in Hz. Independent of VfoHz: the dial roams over
    // the sampled spectrum while the radio's hardware centre stays put.
    // Updated only by explicit calls to <c>POST /api/radio/lo</c> (or by the
    // band-change / reconnect paths inside RadioService). RadioService is
    // authoritative; persisted to LiteDB so the radio re-tunes to the same
    // hardware centre on reconnect. Zero on a fresh server before the first
    // state hydration; RadioService snaps it to VfoHz at construction so the
    // displayed centre is never zero. Mirrors Thetis CTUN's frozen-NCO model
    // (console.cs:43143-43170), now Zeus's only tuning model.
    long RadioLoHz = 0,

    // RX2 / VFO B. Zeus implements the first usable dual-receive path by
    // feeding the current wide IQ stream into a second WDSP RXA channel and
    // tuning it with an independent VFO-B shift. That gives simultaneous
    // RX1/RX2 inside the captured bandwidth immediately; future protocol
    // work can map this same state onto a second hardware DDC for wider
    // splits without changing the UI contract.
    bool Rx2Enabled = false,
    long VfoBHz = 14_200_000,
    RxMode ModeB = RxMode.USB,
    int FilterLowHzB = 100,
    int FilterHighHzB = 2850,
    string? FilterPresetNameB = "VAR1",
    Zeus.Contracts.Rx2AudioMode Rx2AudioMode = Zeus.Contracts.Rx2AudioMode.Both,
    double Rx2AfGainDb = 0.0,
    Zeus.Contracts.TxVfo TxVfo = Zeus.Contracts.TxVfo.A,

    // CW sidetone pitch in Hz. Currently a baked-in constant
    // (CwDefaults.PitchHz); will become a user-settable preference
    // (Thetis: Setup → DSP → Keyer → CW Pitch). On the wire now so
    // the frontend already consumes the live value — when the setting
    // lands, only the server-side source changes.
    int CwPitchHz = CwDefaults.PitchHz,

    // CTUN (click-tune / centred-tuning) toggle. When true, SetVfo moves only
    // the dial (VfoHz) and leaves the hardware NCO (RadioLoHz) frozen, so the
    // operator can click-tune anywhere on the panadapter without recentring
    // the display; WDSP's shift stage relocates the tuned signal for RX, and
    // the radio retunes the shared P1/P2 VFO register to the dial on key-down
    // for TX (RadioService.SetMox → AlignLoForTx) then restores the frozen
    // centre on un-key. When false, every tune recentres the NCO on the dial
    // (classic "radio follows the dial"). Persisted in zeus-prefs.db. Mirrors
    // Thetis ClickTuneDisplay (console.cs:43143).
    bool CtunEnabled = false,

    // RX preamp toggle. Persisted with the rest of the radio-state controls so
    // PRE comes back exactly as the operator left it after a backend restart.
    // Hidden on HL2 in the frontend because that board has no hardware preamp.
    bool PreampOn = false,

    // ---- TX-audio source (external-audio-jacks re-port) ----
    // The RESOLVED (board-clamped) TX-audio source the server is currently
    // pushing. The frontend hydrates the audio picker from this and never
    // clobbers the server on connect (PR #359/#360 anti-clobber pattern).
    // Default Host is byte-identical to today on every board.
    TxAudioSource TxAudioSource = TxAudioSource.Host,

    // ---- Multi-DDC receivers array (wire v2) ----
    // Canonical per-receiver list: index 0 = RX1, index 1 = RX2, index ≥ 2 =
    // additional DDCs. The flat RX1 fields (VfoHz/Mode/Filter*/RxAfGainDb) and
    // the RX2 / VFO-B fields (VfoBHz/ModeB/Filter*B/Rx2AfGainDb) remain the
    // authoritative source for indices 0/1; RadioService projects them into
    // this array on every state change (Mutate + Snapshot), so the array is
    // never stale. Null only on a pre-projection seed — every wire-bound path
    // populates it. The frontend migrates to this array (multi-DDC UI); the
    // flat dupes are retired once that lands.
    IReadOnlyList<ReceiverDto>? Receivers = null,

    // Wire contract version (WireContract.Version) so the frontend can
    // feature-detect the Receivers[] array and per-DDC controls. v1 = implicit
    // pre-multi-DDC baseline; v2 = Receivers[] present.
    int WireVersion = WireContract.Version,

    // DDC / receiver ceiling for this build (WireContract.MaxReceivers). The
    // multi-DDC UI gates the "exposed receivers" control against this.
    int MaxReceivers = WireContract.MaxReceivers,

    // ---- VFO lock (Thetis chkVFOLock) ----
    // Pure software guard: when true, operator dial tuning (panadapter click,
    // wheel, typed entry) is rejected so an accidental knob bump can't move the
    // VFO. CAT / TCI / calibration (fromExternal) still tune — they are
    // intentional. No hardware effect. Ephemeral — defaults unlocked each
    // session (Thetis resets the lock on restart).
    bool VfoLocked = false,

    // ---- RIT (Receiver Incremental Tuning, Thetis chkRIT/udRIT) ----
    // Temporary RX-only frequency offset applied without moving the displayed
    // VFO digits. In Zeus's CTUN model the offset is folded into the WDSP shift
    // stage (DspPipelineService), so the tuned signal relocates while the dial
    // reads unchanged. RitHz range ±99999 (Thetis udRIT). RX1 only.
    bool RitEnabled = false,
    long RitHz = 0,

    // ---- XIT (Transmit Incremental Tuning, Thetis chkXIT/udXIT) ----
    // Temporary TX-only carrier offset applied on key-down without moving the
    // displayed VFO. Folded into the TX effective-LO computation
    // (RadioService.TxEffectiveLoHz / AlignLoForTx). XitHz range ±99999.
    bool XitEnabled = false,
    long XitHz = 0,

    // ---- Per-RX mute (Thetis chkMUT / chkRX2Mute) ----
    // RX1 / RX2 audio mute (RXOutputGain=0 equivalent). Index ≥ 2 receivers
    // carry their mute on ReceiverDto.Muted. Distinct from Rx2AudioMode routing.
    bool Rx1Muted = false,
    bool Rx2Muted = false,

    // ---- Multi-DDC TX target ----
    // Authoritative transmit target as a receiver index (0 = RX1/VFO A, 1 = RX2/
    // VFO B, >= 2 = an extra DDC). TxVfo stays the legacy A/B projection;
    // RadioService.TxFrequencyHz resolves the carrier from this index so TX can
    // key on any receiver's VFO. Ephemeral — resets to RX1 each session (never
    // auto-transmit on a receiver the operator can't see after a restart).
    int TxReceiverIndex = 0,

    // ---- Diversity combiner (Thetis DiversityForm / WDSP xdivEXT) ----
    // Two phase-synchronous ADC streams combined with a per-source complex
    // rotation (gain magnitude + phase) to null an interferer or peak a signal.
    // Null = diversity off (default), byte-identical to today's single-ADC RX
    // path. See DiversityConfig. Ephemeral — re-armed each session (like PS),
    // never auto-armed on restart.
    DiversityConfig? Diversity = null);

/// <summary>Canonical CW constants shared between backend and wire DTOs.
/// Single source of truth — CwOffset (server-side) and StateDto both
/// reference these instead of duplicating magic numbers.</summary>
public static class CwDefaults
{
    public const int PitchHz = 600;
}

public sealed record RadioInfo(
    string MacAddress,
    string IpAddress,
    string BoardId,
    string FirmwareVersion,
    bool Busy,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record ConnectRequest(
    string Endpoint,
    int SampleRate = 192_000,
    bool? PreampOn = null,
    int? Atten = null,
    // Raw HPSDR board byte from discovery (P2's reply parser maps this to
    // <see cref="HpsdrBoardKind"/>). When provided on /api/connect/p2 the
    // server uses it as the connected board kind instead of the historical
    // "P2 active ⇒ assume OrionMkII" fallback. Null/omitted = legacy
    // behaviour. Issue #171.
    byte? BoardId = null,
    // Operator opt-in to take over a radio another controller is already
    // driving. /api/connect/p2 normally refuses to become a SECOND master on a
    // radio whose discovery reply reports Busy — connecting alongside another
    // controller makes the band/antenna/T-R relay matrix chatter and can brown
    // out the radio (observed on a co-located Saturn all-in-one running
    // saturn-go + p2app). The takeover flow sends a reclaim stop first and sets
    // this so the post-reclaim re-connect isn't re-blocked by the busy guard
    // while the radio is still settling. Default false = guard enforced.
    bool Force = false);

public sealed record VfoSetRequest(long Hz, int Receiver = 0);

/// <summary>Body of <c>POST /api/radio/vfo-lock</c>.</summary>
public sealed record VfoLockSetRequest(bool Locked);

/// <summary>Body of <c>POST /api/rx/rit</c>. Both fields optional; only the
/// supplied ones change. <c>Hz</c> is clamped to ±99999 (Thetis udRIT range).</summary>
public sealed record RitSetRequest(bool? Enabled = null, long? Hz = null);

/// <summary>Body of <c>POST /api/tx/xit</c>. Both fields optional; only the
/// supplied ones change. <c>Hz</c> is clamped to ±99999 (Thetis udXIT range).</summary>
public sealed record XitSetRequest(bool? Enabled = null, long? Hz = null);

/// <summary>Body of <c>POST /api/receivers/{index}/mute</c>.</summary>
public sealed record MuteSetRequest(bool Muted);

/// <summary>Body of <c>POST /api/radio/atu/tune</c>. <c>DurationMs</c> is how long
/// the Apollo/Alex auto-tune request bit (C0=0x12 C2[4]) is held on the wire
/// before auto-clearing; default 1000 ms matches Thetis's ATUTune sequence.</summary>
public sealed record AtuTuneRequest(int DurationMs = 1000);

/// <summary>Diversity-combiner configuration. Two phase-synchronous ADC streams
/// are combined as <c>out = rx[Reference] + r·e^{jθ}·rx[Source]</c>, where
/// <c>r = Gain</c> (magnitude, 0..2, 1.0 = unity) and <c>θ = PhaseDeg</c> in
/// degrees (−180..180). The reference receiver (RX1/ADC0) is the phase anchor;
/// <c>SourceRx</c> selects the second ADC's receiver (default RX2/index 1).
/// Mirrors Thetis DiversityForm's I/Q rotate (Irotate=r·cosθ, Qrotate=r·sinθ)
/// fed to WDSP <c>SetEXTDIVRotate</c>.</summary>
public sealed record DiversityConfig(
    bool Enabled = false,
    double Gain = 1.0,
    double PhaseDeg = 0.0,
    int SourceRx = 1);

/// <summary>Body of <c>POST /api/rx/diversity</c>. Every field optional; only
/// the supplied ones change.</summary>
public sealed record DiversitySetRequest(
    bool? Enabled = null,
    double? Gain = null,
    double? PhaseDeg = null,
    int? SourceRx = null);

public enum Rx2AudioMode
{
    Both = 0,
    Rx1 = 1,
    Rx2 = 2,
}

public enum TxVfo : byte
{
    A = 0,
    B = 1,
}

public sealed record Rx2SetRequest(
    bool? Enabled = null,
    long? VfoBHz = null,
    Rx2AudioMode? AudioMode = null,
    double? AfGainDb = null);

/// <summary>Configure one receiver by index (RX1=0, RX2=1, RX3+=2..) for the
/// full multi-DDC model — the body of <c>POST /api/receivers/{index}</c>. Every
/// field is optional; only the supplied ones change. For index ≥ 2 these drive
/// an extra hardware DDC (<c>AdcSource</c> selects the phase-synchronous ADC 0/1).
/// </summary>
public sealed record ReceiverSetRequest(
    bool? Enabled = null,
    long? VfoHz = null,
    byte? AdcSource = null,
    RxMode? Mode = null,
    int? FilterLowHz = null,
    int? FilterHighHz = null,
    double? AfGainDb = null);

/// <summary>Operator settings for the POTA/SOTA Spots feature. Persisted in
/// zeus-prefs.db (<c>SpotsSettingsStore</c>) and shared with the frontend.
/// <para>The server-side poller honours <see cref="Enabled"/> /
/// <see cref="PotaEnabled"/> / <see cref="SotaEnabled"/> /
/// <see cref="PollIntervalSeconds"/>. Everything else is consumed by the
/// frontend: the display filters (<see cref="Bands"/>, <see cref="Modes"/>,
/// <see cref="HideQrt"/>, <see cref="MaxAgeMinutes"/>,
/// <see cref="LatestPerActivator"/>) decide which cached spots the panel
/// shows, and the click-to-tune options (<see cref="SetModeOnTune"/>,
/// <see cref="TuneOnlyWhenConnected"/>, <see cref="CwSideband"/>,
/// <see cref="CwTuneOffsetHz"/>, <see cref="DigiTuneOffsetHz"/>) decide what a
/// click does.</para>
/// <para><see cref="Bands"/> holds band keys (e.g. "20m"); empty means "all
/// bands". <see cref="Modes"/> holds mode-group keys (CW / PHONE / DIGITAL /
/// FM / AM); empty means "all modes". These are intentionally string lists so
/// new bands/modes don't need a wire-format change.</para></summary>
public sealed record SpotsSettings(
    bool Enabled = true,
    bool PotaEnabled = true,
    bool SotaEnabled = true,
    int PollIntervalSeconds = 60,
    bool SetModeOnTune = true,
    bool TuneOnlyWhenConnected = true,
    string CwSideband = "CWU",
    // --- display filters (empty list = no restriction) ---
    IReadOnlyList<string>? Bands = null,
    IReadOnlyList<string>? Modes = null,
    bool HideQrt = true,
    int MaxAgeMinutes = 0,
    bool LatestPerActivator = false,
    // --- click-to-tune dial offsets (Hz, added to the spot frequency) ---
    int CwTuneOffsetHz = 0,
    int DigiTuneOffsetHz = 0,
    // --- DX-cluster source (off by default; POTA + SOTA stay on) ---
    bool DxEnabled = false,
    // --- per-source feed URLs (blank falls back to the built-in default) ---
    string PotaUrl = SpotsSettings.DefaultPotaUrl,
    string SotaUrl = SpotsSettings.DefaultSotaUrl,
    string DxUrl = SpotsSettings.DefaultDxUrl,
    // --- watchlist + alerts (frontend-consumed; persisted for cross-device) ---
    // Callsigns to flag with a ★ and (optionally) raise a desktop/sound alert
    // for when they appear in the feed. Empty = watch nothing.
    IReadOnlyList<string>? Watchlist = null,
    bool AlertsEnabled = false,
    bool AlertSound = true,
    // --- worked-before + QRZ enrichment (frontend-consumed) ---
    // HideWorked drops spots whose activator is already in the local logbook;
    // EnrichQrz lazily resolves operator names via the QRZ session (off by
    // default to respect the XML-API quota).
    bool HideWorked = false,
    bool EnrichQrz = false,
    // --- scan mode: seconds the VFO dwells on each spot before stepping ---
    int ScanDwellSeconds = 8)
{
    public const int MinPollSeconds = 30;
    public const int MaxPollSeconds = 600;
    public const int MaxAgeMinutesLimit = 1440;   // 24 h
    public const int MaxTuneOffsetHz = 5_000;
    public const int MinScanDwellSeconds = 2;
    public const int MaxScanDwellSeconds = 120;

    // Built-in source endpoints. POTA reports kHz, SOTA reports MHz, DXSummit
    // (the de-facto public JSON DX-cluster feed) reports kHz. Operators can
    // override any of these in Settings -> Spots to point at a mirror or an
    // alternative cluster that speaks the same JSON shape.
    public const string DefaultPotaUrl = "https://api.pota.app/spot/activator";
    public const string DefaultSotaUrl = "https://api2.sota.org.uk/api/spots/50/all";
    public const string DefaultDxUrl = "https://www.dxsummit.fi/api/v1/spots?limit=50";

    /// <summary>Clamp numeric ranges and coerce CwSideband to a valid value, so a
    /// hand-crafted POST or a stale persisted row can't wedge the poller or feed
    /// nonsense to the radio. Band/mode keys are trimmed and de-duplicated
    /// (case-insensitively) but their case is preserved — the frontend matches
    /// them case-insensitively, so canonical "20m" / "CW" round-trip intact.</summary>
    public SpotsSettings Normalized() => this with
    {
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, MinPollSeconds, MaxPollSeconds),
        CwSideband = string.Equals(CwSideband, "CWL", StringComparison.OrdinalIgnoreCase) ? "CWL" : "CWU",
        Bands = NormalizeKeys(Bands),
        Modes = NormalizeKeys(Modes),
        MaxAgeMinutes = Math.Clamp(MaxAgeMinutes, 0, MaxAgeMinutesLimit),
        CwTuneOffsetHz = Math.Clamp(CwTuneOffsetHz, -MaxTuneOffsetHz, MaxTuneOffsetHz),
        DigiTuneOffsetHz = Math.Clamp(DigiTuneOffsetHz, -MaxTuneOffsetHz, MaxTuneOffsetHz),
        PotaUrl = NormalizeUrl(PotaUrl, DefaultPotaUrl),
        SotaUrl = NormalizeUrl(SotaUrl, DefaultSotaUrl),
        DxUrl = NormalizeUrl(DxUrl, DefaultDxUrl),
        Watchlist = NormalizeCalls(Watchlist),
        ScanDwellSeconds = Math.Clamp(ScanDwellSeconds, MinScanDwellSeconds, MaxScanDwellSeconds),
    };

    // Blank or non-http(s) URLs fall back to the default so a cleared field
    // can't silently disable a source or smuggle a file:// / unexpected scheme.
    private static string NormalizeUrl(string? url, string fallback)
    {
        var u = url?.Trim();
        if (string.IsNullOrEmpty(u)) return fallback;
        return Uri.TryCreate(u, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? u
            : fallback;
    }

    private static IReadOnlyList<string>? NormalizeKeys(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0) return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<string>(keys.Count);
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            var t = k.Trim();
            if (seen.Add(t)) outp.Add(t);
        }
        return outp.Count == 0 ? null : outp;
    }

    // Watchlist entries are callsigns: trim, upper-case (so matching against the
    // upper-cased activator is exact), and de-duplicate. Blank list → null.
    private static IReadOnlyList<string>? NormalizeCalls(IReadOnlyList<string>? calls)
    {
        if (calls is null || calls.Count == 0) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outp = new List<string>(calls.Count);
        foreach (var c in calls)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var t = c.Trim().ToUpperInvariant();
            if (seen.Add(t)) outp.Add(t);
        }
        return outp.Count == 0 ? null : outp;
    }
}

/// <summary>A POTA or SOTA activation spot, normalized for the Spots panel.
/// <para><see cref="FreqHz"/> is absolute Hz — the upstream feeds disagree on
/// units (POTA reports kHz, SOTA reports MHz) and both are converted to Hz by
/// <c>ActivationSpotsService</c> so the frontend's click-to-tune can pass it
/// straight to /api/vfo.</para>
/// <para><see cref="Source"/> is "POTA" or "SOTA". <see cref="Reference"/> is
/// the park (e.g. US-2518) or summit (e.g. W4A/HR-001) code; <see cref="Name"/>
/// is its human name. <see cref="Mode"/> is the raw upstream mode string
/// (SSB / CW / FT8 / …) — the UI maps it to an <c>RxMode</c> with a
/// band-aware sideband at tune time. This is the POTA/SOTA activation feed and
/// is unrelated to the TCI DX-cluster <c>SpotManager</c>.</para></summary>
public sealed record ActivationSpotDto(
    string Source,
    string Activator,
    long FreqHz,
    string Mode,
    string Reference,
    string? Name,
    string? Location,
    string? Grid,
    string? Comments,
    string? Spotter,
    string SpotTime);

/// <summary>Set the hardware NCO (radio LO) frequency in Hz. Does not move
/// the operator's tuned frequency (VfoHz). Used by the panadapter pure-pan
/// gesture when a drag would otherwise carry the viewport outside the IQ
/// capture window. Out-of-range values are rejected with 400.</summary>
public sealed record RadioLoSetRequest(long Hz);

/// <summary>Enable or disable CTUN (click-tune / centred tuning). When
/// enabled, panadapter clicks move only the dial and leave the hardware NCO
/// frozen so the operator can tune off-centre; see <see cref="StateDto.CtunEnabled"/>.</summary>
public sealed record CtunSetRequest(bool Enabled);

public sealed record ModeSetRequest(RxMode Mode, int Receiver = 0);

public sealed record BandwidthSetRequest(int Low, int High);

/// <summary>TX bandpass set request — signed Hz pair matching StateDto's
/// TxFilterLowHz/TxFilterHighHz convention (LSB-style passbands are negative,
/// DSB/AM/FM symmetric around 0).</summary>
public sealed record TxFilterSetRequest(int LowHz, int HighHz);

public sealed record SampleRateSetRequest(int Rate);

public sealed record PreampSetRequest(bool On);

public sealed record AgcGainSetRequest(double TopDb);

public sealed record AgcThresholdSetRequest(double ThresholdDbm);

public sealed record RxAfGainSetRequest(double Db);

public sealed record AttenuatorSetRequest(int Db);

public sealed record MoxSetRequest(bool On);

public sealed record TxVfoSetRequest(TxVfo TxVfo);

/// <summary>Body of <c>POST /api/tx/receiver</c> — select the transmit target by
/// receiver index (0 = RX1, 1 = RX2, >= 2 = an extra DDC). Generalises
/// <see cref="TxVfoSetRequest"/> beyond the A/B pair.</summary>
public sealed record TxReceiverSetRequest(int Index);

public sealed record DriveSetRequest(int Percent);

/// <summary>TX pre-key (MOX) delay in milliseconds, 0..500. See
/// <see cref="StateDto.TxMoxPreKeyDelayMs"/>.</summary>
public sealed record TxPreKeyDelaySetRequest(int DelayMs);

// TUN has its own drive % so the operator can pre-set a lower tune level
// without touching the MOX drive. Same per-band PA gain compensates both,
// so equal slider positions yield equal watts on air (Thetis parity —
// `console.cs:46756-46788`).
public sealed record TuneDriveSetRequest(int Percent);

public sealed record NrSetRequest(NrConfig Nr);

// AGC mode + custom-params set request. Replace-style (the whole AgcConfig is
// posted on every change), matching NrSetRequest. The separate AGC max-gain
// path (/api/agcGain) is untouched.
public sealed record AgcSetRequest(AgcConfig Agc);

// RX squelch set request. Replace-style (the whole SquelchConfig is posted on
// every change), matching AgcSetRequest. The server clamps Level to 0..100.
public sealed record SquelchSetRequest(SquelchConfig Squelch);

// TX leveling set request. Replace-style (the whole TxLevelingConfig is posted
// on every change), matching SquelchSetRequest. The server clamps every range
// (AlcMaxGainDb 0..120, AlcDecayMs 1..50, LevelerDecayMs 1..5000,
// CompressorGainDb 0..20). The separate Leveler max-gain path
// (/api/tx/leveler-max-gain) is untouched.
public sealed record TxLevelingSetRequest(TxLevelingConfig TxLeveling);

// Per-popover save requests for the NR right-click panels. Nullable shape so
// the popover can PATCH a single field without disturbing siblings (the server
// merges on top of the persisted NrConfig and re-applies the engine state).
public sealed record Nr2Post2ConfigSetRequest(
    bool? Post2Run = null,
    double? Post2Factor = null,
    double? Post2Nlevel = null,
    double? Post2Rate = null,
    int? Post2Taper = null);

public sealed record Nr4ConfigSetRequest(
    double? ReductionAmount = null,
    double? SmoothingFactor = null,
    double? WhiteningFactor = null,
    double? NoiseRescale = null,
    double? PostFilterThreshold = null,
    int? NoiseScalingType = null,
    int? Position = null);

// NR2 (EMNR) core algorithm selectors + trained-method tuning. Mirrors
// Nr2Post2ConfigSetRequest's nullable-merge pattern: each field absent
// from the PATCH leaves the persisted value untouched.
//   GainMethod: 0=Linear, 1=Log, 2=Gamma (default), 3=Trained
//   NpeMethod : 0=OSMS (default), 1=MMSE, 2=NSTAT
//   TrainT1/T2 are only meaningful when GainMethod=3.
public sealed record Nr2CoreConfigSetRequest(
    int? GainMethod = null,
    int? NpeMethod = null,
    bool? AeRun = null,
    double? TrainT1 = null,
    double? TrainT2 = null);

// Panadapter/waterfall zoom levels. Level=1 means the analyzer covers the full
// sample-rate span; level=2 means VFO-centered half-span (×2 bins/Hz), and so
// on. The span-centering math lives in the engine; this contract just carries
// the discrete factor on the wire.
public sealed record ZoomSetRequest(int Level);

public sealed record AutoAttSetRequest(bool Enabled);

// RX ADC protection policy. This is the operator-facing superset of the
// legacy Auto-ATT toggle: existing /api/auto-att still maps to Enabled, while
// /api/rx/adc-protection exposes the ramp timing, step size, maximum automatic
// offset, warning threshold, and optional Protocol-2 max-magnitude soft limit.
// Defaults preserve the original Thetis-style loop: 100 ms windows, 1 dB
// attack/release steps, 31 dB maximum offset, warning when overload level > 3,
// and no magnitude-only attack unless the operator explicitly sets a limit.
public sealed record AdcProtectionConfig(
    bool Enabled = true,
    int AttackMs = 100,
    int ReleaseMs = 100,
    int AttackStepDb = 1,
    int ReleaseStepDb = 1,
    int MaxOffsetDb = 31,
    int WarningThreshold = 3,
    int MagnitudeSoftLimit = 0);

public sealed record AdcProtectionSetRequest(
    bool? Enabled = null,
    int? AttackMs = null,
    int? ReleaseMs = null,
    int? AttackStepDb = null,
    int? ReleaseStepDb = null,
    int? MaxOffsetDb = null,
    int? WarningThreshold = null,
    int? MagnitudeSoftLimit = null);

public sealed record AdcProtectionStatusDto(
    AdcProtectionConfig Config,
    int AttenDb,
    int OffsetDb,
    int EffectiveDb,
    bool Warning,
    int OverloadLevel,
    byte LastOverloadBits,
    ushort? Adc0MaxMagnitude,
    ushort? Adc1MaxMagnitude,
    ushort Adc0MaxMagnitudeAtOverload,
    ushort Adc1MaxMagnitudeAtOverload,
    DateTimeOffset? LastTelemetryUtc);

public sealed record AutoAgcSetRequest(bool Enabled);

public sealed record TunSetRequest(bool On);

// /api/cw/send body. Text is the ASCII transcript to key out; Wpm is the
// playback speed (PARIS-method words per minute, clamped to 5..50 at the
// engine seam). Wpm null means "use the operator's stored CwSettings.Wpm
// default" (CwSettingsStore — written by /api/cw/settings).
public sealed record CwSendRequest(string Text, int? Wpm = null);

// Persisted CW operator settings. Wpm is the default speed for new sends
// when /api/cw/send is called without an explicit wpm. FarnsworthWpm is
// the character-rate floor for Farnsworth spacing (null = pure WPM, no
// Farnsworth). Macros is exactly six slots — the macro pad is a fixed
// 2×3 grid; empty strings are valid (renders a "(empty)" button). The
// sidetone fields are surfaced here so the UI sliders persist alongside
// the macros, even though sidetone audio routing itself lands later
// (zeus-5ue) — keeps the wire shape stable across the epic.
public sealed record CwSettingsDto(
    int Wpm,
    int? FarnsworthWpm,
    string[] Macros,
    double SidetoneGainDb,
    int SidetoneHz,
    CwKeyerMode KeyerMode);

// PATCH-shaped: every field nullable so the frontend can save one slider
// (or one macro) without re-sending the whole record. Server merges on top
// of the persisted row before applying.
public sealed record CwSettingsSetRequest(
    int? Wpm = null,
    int? FarnsworthWpm = null,
    string[]? Macros = null,
    double? SidetoneGainDb = null,
    int? SidetoneHz = null,
    CwKeyerMode? KeyerMode = null);

// Hermes-Lite 2 (and the wider openHPSDR family) on-board CW keyer mode,
// written to C&C register 0x0B C3[7:6] (gateware rtl/cw_openhpsdr.sv:32).
// Straight is the default-safe choice: in this mode the gateware passes the
// key line through directly and ignores keyer speed, so a straight/bug key
// is never mis-interpreted as a paddle. Iambic A/B generate dits & dahs
// from the two paddle inputs at the configured WPM.
public enum CwKeyerMode : byte
{
    Straight = 0,  // 00 — straight key / external keyer / bug; speed ignored
    IambicA = 1,   // 01 — iambic Mode A
    IambicB = 2,   // 10 — iambic Mode B
}

public sealed record MicGainSetRequest(int Db);

// Leveler max-gain ceiling in dB. Server clamps to [0, 20]; outside that is
// 400. Frontend POSTs this whenever the slider moves and on WS reconnect so
// the operator's preferred ceiling is re-applied after a server restart
// (backend holds no persistent state for this setting).
public sealed record LevelerMaxGainSetRequest(double Gain);

// Per-band memory: last-used frequency and mode for a given ham band
// (e.g. "20m"). The server keeps these in an unencrypted LiteDB file so they
// survive restarts and follow the backend (not the browser). Band buttons
// read the full map on mount and write on every tune/mode change
// (debounced on the web).
public sealed record BandMemoryDto(string Band, long Hz, RxMode Mode);

public sealed record BandMemorySetRequest(long Hz, RxMode Mode);

// UI layout: opaque workspace JSON persisted server-side so the operator's
// panel arrangement survives page reloads and reinstalls. The JSON is stored
// as a string to avoid strongly-typing the workspace tree on the wire — the
// frontend owns the schema.
//
// `UiLayoutDto` / `UiLayoutSetRequest` are the legacy single-layout shape
// (one workspace per server). Kept so older clients keep working and so the
// new multi-layout system can migrate the legacy row on first read.
public sealed record UiLayoutDto(string LayoutJson, long UpdatedUtc);

public sealed record UiLayoutSetRequest(string LayoutJson);

// Multi-layout shape (issue #241). Layouts are keyed per radio (board kind /
// "default" while disconnected). Each radio holds a list of named layouts and
// remembers which one was active.
//
// `Icon` is a short string (typically a single emoji) shown above the layout
// label in the LeftLayoutBar; `Description` is a longer free-form string used
// as the hover tooltip. Both are optional — older layouts without these
// fields render with a letter fallback and the layout name as tooltip.
public sealed record NamedLayoutDto(
    string Id,
    string Name,
    string LayoutJson,
    long UpdatedUtc,
    string? Icon = null,
    string? Description = null);

public sealed record RadioLayoutsDto(string RadioKey, IReadOnlyList<NamedLayoutDto> Layouts, string ActiveLayoutId);

public sealed record SaveNamedLayoutRequest(
    string RadioKey,
    string LayoutId,
    string Name,
    string LayoutJson,
    string? Icon = null,
    string? Description = null);

public sealed record SetActiveLayoutRequest(string RadioKey, string LayoutId);

// Prefs-database (profile) selector. All Zeus settings/layouts/prefs persist in
// a single LiteDB file resolved by PrefsDbPath.Get() at startup. The operator
// can keep several named databases and pick which is active from the connect
// screen; the choice lives in a pointer file (active-profile.txt) — NOT inside
// any prefs DB — and applies on the next launch. The legacy zeus-prefs.db is the
// always-present "Default" profile. RelativePath is under the Zeus data dir
// ("zeus-prefs.db" or "profiles/<name>.db"). ModifiedUtcMs is Unix epoch ms.
public sealed record PrefsDatabaseInfo(
    string Name,
    string RelativePath,
    long SizeBytes,
    long ModifiedUtcMs,
    bool Active);

public sealed record PrefsDatabasesDto(
    string ActiveRelativePath,
    IReadOnlyList<PrefsDatabaseInfo> Databases);

public sealed record SetActiveDatabaseRequest(string RelativePath);

public sealed record CreateDatabaseRequest(string Name);

public sealed record ImportDatabaseRequest(string SourcePath, string? Name);

// Per-band PA settings. Mirrors Thetis `PAProfile._gainValues[]` / piHPSDR
// `band->pa_calibration` (single scalar dB per band — 9-point curve is a
// Phase-4 follow-up). OcTx / OcRx are 7-bit Open-Collector masks driving the
// N2ADR filter board on HL2 and ALEX/OC outputs on Orion-class radios; they
// are OR'd with the board's auto-filter logic so stock HL2 filter switching
// keeps working when the user hasn't set anything.
//
// AutoOcMask is informational only — the read-only N2ADR board mask the
// firmware will OR onto OcRx/OcTx when HasN2adr is on (HL2). PUT requests
// ignore it; the server recomputes from the connected board on the next GET.
//
// OcDxTx / OcDxRx are 4-bit masks (bits 0..3 -> DX OUT 7..10) for the
// Anvelina-PRO3-only "Open Collector DX" extension (USEROUT7..10), wire-
// encoded into Protocol-2 high-priority byte 1397 bits [4:1]. Per EU2AV's
// Open_Collector_Anvelina_DX spec (issue #407). Honoured by the wire path
// only when the connected board is OrionMkII + AnvelinaPro3 variant on
// Protocol 2; persisted on every band so DX wiring travels with the band.
public sealed record PaBandSettingsDto(
    string Band,
    double PaGainDb = 0.0,
    bool DisablePa = false,
    byte OcTx = 0,
    byte OcRx = 0,
    byte AutoOcMask = 0,
    byte OcDxTx = 0,
    byte OcDxRx = 0);

// Globals shared across bands. PaMaxPowerWatts=0 disables the watts
// conversion path and falls back to the legacy "drive% = raw 0-255 byte"
// behavior so existing installs behave identically until the user runs
// a calibration. OC bits during TUN follow the per-band OcTx mask (same
// as TX) — Thetis behaves this way and the inherited piHPSDR-style
// "OcTune" override was removed in #124 for hardware-safety reasons (a
// global override can hand an external amp a confused band-select state
// during a steady tune carrier and damage finals).
public sealed record PaGlobalSettingsDto(
    bool PaEnabled = true,
    int PaMaxPowerWatts = 0);

public sealed record PaSettingsDto(
    PaGlobalSettingsDto Global,
    IReadOnlyList<PaBandSettingsDto> Bands);

public sealed record PaSettingsSetRequest(
    PaGlobalSettingsDto Global,
    IReadOnlyList<PaBandSettingsDto> Bands);

// Radio-selection header for the Settings menu. `Preferred` is the operator's
// explicit pick ("Auto" = no override); `Connected` is what discovery found
// on the wire ("Unknown" when nothing's connected); `Effective` is the board
// whose defaults the PA / per-band tables seed from. Discovery wins whenever
// a radio is actually connected **unless** `OverrideDetection` is true — the
// preference is normally a before-connect hint, but with override it forces
// specific board behavior even when a different board is detected.
public sealed record RadioSelectionDto(
    string Preferred,
    string Connected,
    string Effective,
    bool OverrideDetection);

public sealed record RadioSelectionSetRequest(
    string Preferred,
    bool? OverrideDetection);

// Operator-selected variant for the 0x0A wire-byte alias family
// (issue #218). String-typed for forward compatibility — server parses
// against the OrionMkIIVariant enum. Empty / unknown rejected with 400.
public sealed record RadioVariantSetRequest(string Variant);

// Global (per-radio, NOT per-band) TX-audio SOURCE selection
// (external-audio-jacks re-port). GET carries the per-board source-availability
// gates so the single-select picker renders only the sources the connected
// board offers: HasOnboardCodec gates RadioMic; HasRadioLineIn gates
// RadioLineIn; HasBalancedXlr gates RadioBalancedXlr; HasMicBias gates the bias
// toggle; HermesLite2MicFrontEnd flags the (inert v1) HL2 mic front-end.
// `Source` is the RESOLVED (board-clamped) source the server is pushing — the
// frontend hydrates from it and never clobbers the server on connect. MicBoost /
// MicBias / LineInGain are the parameters OF the selected source.
public sealed record AudioFrontEndDto(
    bool HasOnboardCodec,
    bool HermesLite2MicFrontEnd,
    bool HasRadioLineIn,
    bool HasBalancedXlr,
    bool HasMicBias,
    TxAudioSource Source,
    bool MicBoost,
    bool MicBias,
    int LineInGain);

// Mutating version — sets the whole global TX-audio source. LineInGain is
// clamped to 0..31 server-side. The server clamps Source against the board's
// capabilities (an unsupported jack → Host) and returns the resolved state plus
// the live capability gates.
public sealed record AudioFrontEndSetRequest(
    TxAudioSource Source,
    bool MicBoost,
    bool MicBias,
    int LineInGain);

// HL2-specific optional toggles surfaced via /api/radio/hl2-options.
// Shape is an object (not a bare bool) so future mi0bot HL2 toggles can
// slot in without breaking the contract. Currently carries Band Volts
// PWM enable (issue #279) — the C3 bit 3 Protocol-1 Config flag the HL2
// fork repurposes from the obsolete LT2208 DITHER bit; lit, HL2 emits
// per-band-tagged PWM voltage on the FAN connector so an external amp
// (Xiegu XPA125B etc.) can auto-band-switch.
public sealed record Hl2OptionsDto(bool BandVolts);

// Mutating version — currently a passthrough of Hl2OptionsDto but kept
// distinct so the GET-vs-PUT request shapes can diverge in the future
// (e.g. PUT becoming a partial update with nullable fields).
public sealed record Hl2OptionsSetRequest(bool BandVolts);

// ANAN-G2 / Saturn-class ADC options surfaced via /api/radio/g2-options.
// Dither and randomizer default on, matching the Thetis G2 option block.
// MaxRxFreqMHz is read-only: Zeus enforces the same 0..60 MHz ceiling in
// the VFO/radio-LO clamps rather than duplicating a second user setting.
public sealed record G2OptionsDto(
    bool DitherEnabled,
    bool RandomEnabled,
    double MaxRxFreqMHz,
    bool Supported,
    int Rx1AttenuatorDb = 0,
    int Rx1AttenuatorMinDb = 0,
    int Rx1AttenuatorMaxDb = 31,
    bool Rx1AttenuatorSupported = false);

// Partial update so future G2 options can be added without forcing clients
// to echo fields they did not intend to change.
public sealed record G2OptionsSetRequest(
    bool? DitherEnabled = null,
    bool? RandomEnabled = null,
    int? Rx1AttenuatorDb = null);

// Panadapter background settings — Mode is one of "basic" | "beam-map" |
// "image"; Fit is one of "fit" | "fill" | "stretch". Image bytes are NOT
// shipped in this DTO; HasImage signals whether GET /api/display-settings/image
// will return content. RxTraceColor is the panadapter signal trace colour
// as #RRGGBB (default "#FFA028"). Db* fields are the panadapter/waterfall dB
// window bounds persisted so the operator's scale survives a backend restart.
// Null means the server has never stored that field; the frontend falls back
// to its built-in defaults (FIXED_DB_MIN / TX_FIXED_DB_MIN etc.) and pushes
// the current value up on next interaction. All fields persisted server-side
// so the settings follow the operator across browsers / devices — Photino
// desktop mode in particular binds the webview to a fresh random loopback
// port on every launch, which orphans any per-origin localStorage value.
public sealed record DisplaySettingsDto(
    string Mode,
    string Fit,
    bool HasImage,
    string? ImageMime,
    string RxTraceColor,
    double? DbMin,
    double? DbMax,
    double? TxDbMin,
    double? TxDbMax,
    double? WfDbMin,
    double? WfDbMax,
    double? WfTxDbMin,
    double? WfTxDbMax,
    // TX display analyzer parameters (issue: live TX waterfall). All
    // display-only — they shape the transmitted-signal panadapter/waterfall
    // visualization and never touch the transmitted audio, drive, or PA.
    // Null on legacy rows / requests → server falls back to its defaults.
    //   TxDisplayCalOffsetDb — dB added to the TX trace/waterfall pixels so the
    //     operator can calibrate the absolute level (Thetis TXDisplayCalOffset).
    //   TxDisplayFftSize     — WDSP TX analyzer FFT size (power of two).
    //   TxDisplayWindow      — WDSP analyzer window type (win_type).
    //   TxDisplayAvgTauMs    — TX trace visual smoothing time-constant (ms).
    double? TxDisplayCalOffsetDb = null,
    int? TxDisplayFftSize = null,
    int? TxDisplayWindow = null,
    double? TxDisplayAvgTauMs = null);

public sealed record DisplaySettingsSetRequest(
    string Mode,
    string Fit,
    string RxTraceColor,
    double? DbMin = null,
    double? DbMax = null,
    double? TxDbMin = null,
    double? TxDbMax = null,
    double? WfDbMin = null,
    double? WfDbMax = null,
    double? WfTxDbMin = null,
    double? WfTxDbMax = null,
    double? TxDisplayCalOffsetDb = null,
    int? TxDisplayFftSize = null,
    int? TxDisplayWindow = null,
    double? TxDisplayAvgTauMs = null);

// Server-side mirror of the frontend Signal Intelligence weak-signal display
// controls. The DSP math remains in zeus-web's signal-estimator; this DTO lets
// the active operator profile and tuning follow the radio across browsers and
// lets diagnostics audit which weak-signal display policy is active.
public sealed record DisplayIntelligenceSettingsDto(
    string ProfileId,
    bool PopEnabled,
    bool SnapEnabled,
    bool AutoNotchEnabled,
    bool AutoProfileEnabled,
    bool VisualAgcEnabled,
    bool ImpulseRejectEnabled,
    double PopFloorDb,
    double PopSpanDb,
    double PopGamma,
    int PopRenderIntensity,
    int WaterfallReliefDepth,
    int WaterfallSmoothness,
    double CoherenceHoldGate,
    double CoherenceBoostDb,
    double RidgeBoost,
    double RidgeMaxBoostDb,
    int VisualAgcStrength,
    int ImpulseRejectDb,
    int SnapRadiusHz,
    double SnapMinSnrDb,
    double PeakMinSnrDb);

// Per-mode disclosure state for the inline NR settings accordion that hangs
// below the DSP NR toggle row. Three independent booleans — one per NR
// algorithm. Persisted server-side (LiteDB) so the operator's "I always
// have NR2 tunables open" preference follows them across browsers.
public sealed record NrUiPrefsDto(
    bool Nr1Expanded,
    bool Nr2Expanded,
    bool Nr4Expanded);

public sealed record NrUiPrefsSetRequest(
    bool Nr1Expanded,
    bool Nr2Expanded,
    bool Nr4Expanded);

// Operator UI theme + per-token colour overrides. `Theme` is one of "dark"
// | "light" — the theme overlay attribute set on <html data-theme="…">.
// `Overrides` maps CSS custom-property names (e.g. "--accent") to upper-case
// 6-digit hex strings; an empty/missing map means "use stylesheet defaults".
// Server-side LiteDB persistence (previously localStorage) so the operator's
// look-and-feel follows them across browsers and devices pointed at the
// same Zeus instance — same pattern as DisplaySettingsStore / NrUiPrefsStore.
public sealed record ThemeSettingsDto(
    string Theme,
    IReadOnlyDictionary<string, string> Overrides);

public sealed record ThemeSettingsSetRequest(
    string Theme,
    IReadOnlyDictionary<string, string> Overrides);

// Per-slot pin state for the classic-layout bottom row (Logbook + TX
// Stage Meters). True = panel is pinned (full body visible). False =
// collapsed to a chip strip below the pinned tier. Persisted server-side
// so the layout choice follows the operator across browsers / devices,
// same as DisplaySettings.
public sealed record BottomPinDto(
    bool Logbook,
    bool TxMeters);

public sealed record BottomPinSetRequest(
    bool Logbook,
    bool TxMeters);

// Vertical split between the panadapter and the waterfall in the Hero
// panel. PanPercent is the panadapter share, clamped 10..90; the
// waterfall takes the remainder. Single global value for now. Persisted
// server-side in zeus-prefs.db (same pattern as BottomPinDto) so the
// choice follows the operator across browsers / devices.
public sealed record PanWfSplitDto(double PanPercent);

public sealed record PanWfSplitSetRequest(double PanPercent);

// Toolbar Mode/Band/Step favorite-slot pins plus the currently-selected
// tuning step. Each favorite array holds exactly three slot keys; StepHz is
// the live tuning step in Hz. Persisted server-side in zeus-prefs.db so the
// settings follow the operator across browsers / devices — Photino desktop
// mode binds the webview to a fresh random loopback port on every launch,
// which orphans any per-origin localStorage value (the bug this fixes). Null
// arrays / StepHz mean the server has never stored a value; the frontend
// falls back to its built-in defaults and pushes the current value up on the
// next interaction.
public sealed record ToolbarSettingsDto(
    IReadOnlyList<string>? Mode,
    IReadOnlyList<string>? Band,
    IReadOnlyList<string>? Step,
    int? StepHz);

public sealed record ToolbarSettingsSetRequest(
    IReadOnlyList<string>? Mode = null,
    IReadOnlyList<string>? Band = null,
    IReadOnlyList<string>? Step = null,
    int? StepHz = null);

// ---- PureSignal request records ----
// PsControlSetRequest = master arm (Enabled) + mode (Auto vs Single).
// PsAdvancedSetRequest = nullable so partial updates from the settings
// panel don't reset other fields.
public sealed record PsControlSetRequest(bool Enabled, bool Auto, bool Single);

public sealed record PsAdvancedSetRequest(
    bool? Ptol = null,
    bool? AutoAttenuate = null,
    double? MoxDelaySec = null,
    double? LoopDelaySec = null,
    double? AmpDelayNs = null,
    double? HwPeak = null,
    string? IntsSpiPreset = null);

public sealed record PsResetRequest();

public sealed record PsSaveRequest(string Filename);

public sealed record PsRestoreRequest(string Filename);

// Feedback antenna selector — Internal coupler vs External (Bypass).
// Sent from the PS settings panel. Affects only the radio-side ALEX bit;
// the WDSP cal/iqc stages operate on whatever IQ arrives at DDC0/DDC1.
public sealed record PsFeedbackSourceSetRequest(PsFeedbackSource Source);

// Manual PS TX feedback attenuation (dB). Operator alternative to
// AutoAttenuate for a fixed external-tap chain: set the value that lands the
// feedback in calcc's range once, and it persists per board. Clamped
// server-side to the connected board's range (P2 0..31, HL2 -28..31).
public sealed record PsFeedbackAttenuationSetRequest(int Db);

// "Monitor PA output" toggle (issue #121). Pure UI/source-routing flag —
// no WDSP setter, no wire-format change. RadioService just stamps the
// StateDto, DspPipelineService reads it on Tick to pick which analyzer
// to drain. Default off; operator opt-in.
public sealed record PsMonitorSetRequest(bool Enabled);

// TX Monitor toggle (issue #106 follow-up). Engages a parallel demod of the
// post-CFIR TX IQ so the operator hears the chain output at the actual TX
// bandwidth profile, with or without keying. Implemented in WdspDspEngine via
// a private RXA channel; pure operator toggle, no persistence. See StateDto
// .TxMonitorEnabled for the discipline notes.
public sealed record TxMonitorSetRequest(bool Enabled);

// Two-tone test generator (used as PS calibration excitation but works
// standalone too). Protocol-agnostic.
public sealed record TwoToneSetRequest(
    bool Enabled,
    double? Freq1 = null,
    double? Freq2 = null,
    double? Mag = null);

// ---- CFC (Continuous Frequency Compressor) — issue #123 ------------------
// Multi-band frequency-domain compressor exposed by WDSP's xcfcomp stage
// (already wired in xtxa between xeqp and xbandpass). Mirrors pihpsdr's
// classic 10-band non-parametric design — see cfc_menu.c. The architecture
// proposal on issue #123 enumerates every WDSP CFCOMP setter we surface.
//
// Persisted GLOBALLY (not per-band/mode) per kb2uka's spec — operator
// profiles are a future feature. CFC defaults to OFF so existing operators
// (including the project owner's external analog rack workflow) see no
// behavior change unless they enable. PostEqEnabled is a separate toggle
// from the master Enabled to mirror pihpsdr — operators may want CFC
// compression without the EQ branch.

/// <summary>One CFC band: centre frequency in Hz (operator-typed),
/// compression-level threshold in dB, and post-comp makeup gain in dB.
/// WDSP sorts the band array internally (cfcomp.c:147), so the on-the-wire
/// order is informational only — the engine relies on WDSP to canonicalise.
/// </summary>
public sealed record CfcBand(double FreqHz, double CompLevelDb, double PostGainDb);

/// <summary>Operator-tunable CFC configuration. <c>Bands</c> length is
/// fixed at 10 to match pihpsdr's classic-mode default and keep the panel
/// layout stable. Engine validates length at the seam.</summary>
public sealed record CfcConfig(
    bool Enabled,
    bool PostEqEnabled,
    double PreCompDb,
    double PrePeqDb,
    CfcBand[] Bands)
{
    /// <summary>Pihpsdr's vfo.c:284-314 baseline — 10 bands at the voice-band
    /// frequencies operators recognise from PowerSDR. All compression and
    /// gains zeroed so enabling neutral CFC is audibly transparent.</summary>
    public static CfcConfig Default => new(
        Enabled: false,
        PostEqEnabled: false,
        PreCompDb: 0.0,
        PrePeqDb: 0.0,
        Bands: new[]
        {
            new CfcBand(50,    0, 0),
            new CfcBand(100,   0, 0),
            new CfcBand(200,   0, 0),
            new CfcBand(500,   0, 0),
            new CfcBand(1000,  0, 0),
            new CfcBand(1500,  0, 0),
            new CfcBand(2000,  0, 0),
            new CfcBand(2500,  0, 0),
            new CfcBand(3000,  0, 0),
            new CfcBand(5000,  0, 0),
        });
}

public sealed record CfcSetRequest(CfcConfig Config);

// ---- TX station profiles -------------------------------------------------
// Operator-tunable macro profiles for the TX voice chain. The frontend owns
// the built-in defaults; the server persists edited overrides so Studio SSB,
// eSSB, and DX punch profiles survive restart and can become a stable API
// surface for settings/diagnostics.
public sealed record TxStationProfileDto(
    string Id,
    string Label,
    string Summary,
    string ApplyTitle,
    string AudioSuiteRoute,
    bool AudioSuiteBypassed,
    string? AudioSuiteProfileName,
    double MicGainDb,
    double LevelerMaxGainDb,
    TxLevelingConfig TxLeveling,
    CfcConfig CfcConfig,
    int LowCutHz,
    int HighCutHz,
    int SpectralDensity);

public sealed record TxStationProfilesResponse(IReadOnlyList<TxStationProfileDto> Profiles);

// ---- TX Audio Profiles (unified) ----------------------------------------
// A single operator-named macro that captures the ENTIRE TX-audio shaping
// state in one recallable snapshot. This REPLACES both the named audio-suite
// plugin profiles and the fixed 3-up TX station profiles — there is now one
// profile concept. Captured fields are a superset of everything proven
// reachable, reusing the existing nested records verbatim (TxLevelingConfig /
// CfcConfig) so there is no parallel schema.
//
// EXCLUDED on purpose (not audio-shaping / global): drive %, tune drive,
// pre-key delay, PureSignal, two-tone, TX monitor/preview, the named
// CFC/filter preset libraries, installed VST3 registrations, CESSB(auto).
//
// ProcessingMode is "native" or "vst" (lower-case) to keep Zeus.Contracts free
// of any dependency on the server-side AudioProcessingMode enum — matching how
// the existing audio-suite endpoints already serialise that field.
public sealed record TxAudioProfileDto(
    string Id,                       // slug, lowercased; PK; seeds: studio-ssb / essb-wide / dx-punch
    string Name,                     // operator display name (captured by the Save dialog)
    // ---- mic / leveler scalars (reuse RadioService Set* clamps) ----
    int MicGainDb,                   // [-40,10]
    double LevelerMaxGainDb,         // [0,20]
    // ---- whole-config reuse ----
    TxLevelingConfig TxLeveling,     // leveler on/decay, ALC max-gain/decay, CPDR on/gain
    CfcConfig CfcConfig,             // enabled/postEq/preComp/prePeq + 10 bands x2
    // ---- TX bandpass + per-mode-family memory ----
    int LowCutHz, int HighCutHz,     // operator-typed positive magnitudes; server re-signs per mode-family
    // ---- audio processing mode + suite chain state ----
    string ProcessingMode,           // "native" | "vst"
    bool MasterBypass,
    List<string> ChainOrder,         // active plugin ids, head-first
    List<string> ChainParked,        // installed-but-out-of-chain ids
    // ---- EVERY plugin's settings ----
    Dictionary<string, string> VstPluginStates,                       // zeusId -> base64 getStateInformation
    Dictionary<string, Dictionary<string, string>> NativePluginStates, // zeusId -> {settingKey -> jsonValue}
    // ---- fidelity policy ----
    int TargetSpectralDensity,       // [0,100]
    DateTime CreatedUtc, DateTime UpdatedUtc);

public sealed record TxAudioProfilesResponse(IReadOnlyList<TxAudioProfileDto> Profiles);

// POST body for "save current live state as <Name>". The backend snapshots the
// live state — the client never assembles the profile body (avoids the
// frontend-clobbers-server pattern).
public sealed record SaveTxAudioProfileRequest(string Name);

// PUT body for the persisted "last loaded profile" pointer. Null/empty Id
// clears it (nothing is applied at startup).
public sealed record LastLoadedTxAudioProfileDto(string? Id);

public sealed record TxFidelityPolicyDto(
    string ProfileId,
    int TargetSpectralDensity);

/// <summary>Status of the local install relative to the latest available Zeus
/// PRODUCTION build, for the Settings -> Updates panel (GET /api/system/update).
/// <para>The latest build is read from the download domain
/// (downloads.openhpsdrzeus.com), published from <c>main</c> only. The
/// <c>Release*</c> fields describe the platform-matched download asset when a
/// network check has completed: <see cref="ReleaseDownloadUrl"/> +
/// <see cref="ReleaseAssetName"/> + <see cref="ReleaseAssetDigest"/> (sha256).
/// <see cref="UpdateAction"/> is "download" when a matching asset exists,
/// "openRelease" when only the website is available, or "none".
/// <see cref="IsGitRepo"/>/<see cref="Branch"/>/<see cref="CurrentShortSha"/> are
/// diagnostics for source checkouts; the git fast-forward path was removed, so
/// <see cref="Behind"/>/<see cref="Ahead"/>/<see cref="CanFastForward"/>/
/// <see cref="UpstreamRef"/> are always 0/false/null. <see cref="Error"/> carries
/// a human message when the network check failed; the rest of the fields hold the
/// last locally-known values.</para></summary>
public sealed record RepoUpdateStatus(
    bool IsGitRepo,
    string? Branch,
    string? CurrentSha,
    string? CurrentShortSha,
    string? CurrentSubject,
    string? UpstreamRef,
    int Behind,
    int Ahead,
    bool Dirty,
    bool CanFastForward,
    string? LatestRemoteSha,
    string? LatestRemoteSubject,
    string? RemoteUrl,
    string? CheckedUtc,
    string? Error)
{
    public string InstalledVersion { get; init; } = "unknown";
    public string RuntimePlatform { get; init; } = "unknown";
    public string RuntimeArchitecture { get; init; } = "unknown";
    public bool UpdateAvailable { get; init; }
    public string UpdateAction { get; init; } = "none";
    public string? LatestVersion { get; init; }
    public string? ReleaseTag { get; init; }
    public string? ReleaseName { get; init; }
    public string? ReleaseUrl { get; init; }
    public string? ReleasePublishedUtc { get; init; }
    public string? ReleaseAssetName { get; init; }
    public string? ReleaseDownloadUrl { get; init; }
    public long? ReleaseAssetSizeBytes { get; init; }
    public string? ReleaseAssetDigest { get; init; }
}

// ---- External antenna ports (external-ports plan — antenna slice, #804) ----
//
// Per-band TX/RX antenna relay + RX-aux selection. Surfaced via
// /api/radio/antenna, NEVER via StateDto — antenna state is server-authoritative
// and pushed to the live client on the Changed → RecomputePaAndPush path, so a
// frontend reconnect can never clobber it (PR #359/#360 no-clobber pattern).
//
// TxAnt / RxAnt are antenna strings ("Ant1" | "Ant2" | "Ant3"); RxAux is the
// auxiliary RX input string ("None" | "Ext1" | "Ext2" | "Xvtr" | "Bypass").
// "Ant1" / "None" reproduce today's wire bytes bit-for-bit (default-inert).
public sealed record AntennaBandDto(string Band, string TxAnt, string RxAnt, string RxAux = "None");

// GET /api/radio/antenna response. HasTxAntennaRelays / HasRxAntennaRelays are
// the board-capability gates the frontend renders the right selectors from; the
// per-band rows list every HF band. AvailableRxAux is the set of aux-input
// strings the connected board exposes (empty on HL2 — no aux). AlexRevision is
// always "Modern" in this slice: the wire path routes PureSignal external
// feedback to the BYPASS/K36 bit (Rev 24+ behaviour), and the operator-set
// legacy Rev15/16 EXT1 routing is not wire-discoverable so it is deferred.
public sealed record AntennaSettingsDto(
    bool HasTxAntennaRelays,
    bool HasRxAntennaRelays,
    IReadOnlyList<AntennaBandDto> Bands,
    IReadOnlyList<string>? AvailableRxAux = null,
    string AlexRevision = "Modern");

// PUT /api/radio/antenna — sets ONE band's antenna + RX-aux selection. Band must
// be a known HF band; TxAnt/RxAnt must parse to HpsdrAntenna; RxAux to the
// server-side RxAuxInputSel. The server returns 409 for a relay/aux the
// connected board lacks (non-ANT1 on a relay-less board, an aux the board does
// not expose), 400 on a malformed body / unknown band / unparseable value.
public sealed record AntennaSetRequest(string Band, string TxAnt, string RxAnt, string RxAux = "None");
