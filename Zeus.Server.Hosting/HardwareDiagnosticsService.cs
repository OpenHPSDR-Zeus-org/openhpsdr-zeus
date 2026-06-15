// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;

namespace Zeus.Server;

internal sealed record G2DigInDiagnosticsDto(
    int SchemaVersion,
    string? ActiveProtocol,
    string ConnectedBoard,
    string EffectiveBoard,
    string OrionMkIIVariant,
    bool P2Attached,
    long P2Packets,
    DateTimeOffset? P2LastUpdatedUtc,
    byte? UserDigitalIn,
    string TxDisableLineId,
    string TxDisableLineName,
    int TxDisableBit,
    bool? TxDisableRawHigh,
    bool? TxDisableActive,
    string TxDisablePolarity,
    string TxDisableMappingStatus,
    bool TxInhibitBehaviorArmed,
    string CwKeyTipSource,
    bool? CwKeyTipDown,
    bool? CwDashInputDown,
    string ManualReference,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

internal sealed record G2MappedSensorDto(
    string Id,
    string Label,
    string TelemetryPath,
    string Source,
    int? RawValue,
    string Status,
    string Notes);

internal sealed record G2UnmappedManualSensorDto(
    string Id,
    string Label,
    string ManualEvidence,
    string CurrentTelemetryStatus,
    string RequiredCapture,
    string SafetyClass);

internal sealed record G2CandidateTelemetryWordDto(
    int Offset,
    string HexOffset,
    string? Known,
    ushort Last,
    ushort Min,
    ushort Max,
    int ChangeCount,
    string Status,
    string MappingHint);

internal sealed record G2SensorMappingDiagnosticsDto(
    int SchemaVersion,
    string? ActiveProtocol,
    string ConnectedBoard,
    string EffectiveBoard,
    string OrionMkIIVariant,
    bool G2Class,
    bool P2Attached,
    long P2Packets,
    DateTimeOffset? P2LastUpdatedUtc,
    string Status,
    G2MappedSensorDto[] MappedSensors,
    G2UnmappedManualSensorDto[] UnmappedManualSensors,
    G2CandidateTelemetryWordDto[] CandidateWords,
    string ManualReference,
    string DiagnosticRecommendation,
    DateTimeOffset GeneratedUtc);

/// <summary>
/// Read-only hardware diagnostic accumulator. Mirrors the live fields Thetis
/// keeps in ChannelMaster network.c/networkproto1.c, but exposes them as a
/// safe REST snapshot instead of turning them directly into operator controls.
/// </summary>
public sealed class HardwareDiagnosticsService : IHostedService, IDisposable
{
    private const double PsFeedbackTargetRaw = 152.293;
    private const int PsFeedbackUsableMinRaw = 128;
    private const int PsFeedbackUsableMaxRaw = 181;
    private const int PsFeedbackCenteredMinRaw = 138;
    private const int PsFeedbackCenteredMaxRaw = 176;
    private const double G2SupplyPlausibleMinVolts = 10.0;
    private const double G2SupplyPlausibleMaxVolts = 18.0;
    private const double GenericSupplyPlausibleMaxVolts = 35.0;

    private readonly RadioService _radio;
    private readonly DspPipelineService _dsp;
    private readonly WdspWisdomInitializer _wisdom;
    private readonly FrontendDspSceneDiagnosticsService _frontendDspScene;
    private readonly object _sync = new();
    private IProtocol1Client? _p1Client;
    private Zeus.Protocol2.Protocol2Client? _p2Client;

    private string? _activeProtocol;

    private long _p1Packets;
    private DateTimeOffset? _p1LastUpdatedUtc;
    private byte? _p1LastC0Address;
    private ushort? _p1LastAin0;
    private ushort? _p1LastAin1;
    private ushort? _p1ExciterAdc;
    private ushort? _p1FwdAdc;
    private ushort? _p1RevAdc;
    private ushort? _p1UserAdc0;
    private ushort? _p1UserAdc1;
    private ushort? _p1SupplyVoltsAdc;
    private byte _p1AdcOverloadBits;
    private bool? _p1HardwarePtt;
    private bool? _p1CwKeyDown;

    private long _p2Packets;
    private DateTimeOffset? _p2LastUpdatedUtc;
    private Zeus.Protocol2.P2TelemetryReading _p2Last;
    private bool _p2HadSample;
    private bool _p2PrevAdc0Overload;
    private bool _p2PrevAdc1Overload;
    private ushort _p2Adc0MaxMagnitudeAtOverload;
    private ushort _p2Adc1MaxMagnitudeAtOverload;
    private readonly P1TelemetryMap _p1TelemetryMap = new();
    private readonly ByteStreamMap _p2HiPriorityMap = new(
        "p2.hiPriorityStatus",
        "UDP 1025 payload after 4-byte sequence prefix",
        56,
        P2KnownByteFields,
        P2KnownWordFields);
    private readonly List<MappingMarker> _mappingMarkers = new();
    private int _nextMarkerId = 1;

    public HardwareDiagnosticsService(
        RadioService radio,
        DspPipelineService dsp,
        WdspWisdomInitializer wisdom,
        FrontendDspSceneDiagnosticsService frontendDspScene)
    {
        _radio = radio;
        _dsp = dsp;
        _wisdom = wisdom;
        _frontendDspScene = frontendDspScene;
        _radio.Connected += OnP1Connected;
        _radio.Disconnected += OnP1Disconnected;
        _radio.P2Connected += OnP2Connected;
        _radio.P2Disconnected += OnP2Disconnected;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_radio.ActiveClient is { } p1)
            OnP1Connected(p1);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        OnP1Disconnected();
        OnP2Disconnected();
        return Task.CompletedTask;
    }

    public object Snapshot()
    {
        var state = _radio.Snapshot();
        var connected = _radio.ConnectedBoardKind;
        var effective = _radio.EffectiveBoardKind;
        var variant = _radio.EffectiveOrionMkIIVariant;
        var caps = BoardCapabilitiesTable.For(effective, variant);

        lock (_sync)
        {
            return new
            {
                hardwareDiagnosticsApiVersion = 1,
                generatedUtc = DateTimeOffset.UtcNow,
                connectionStatus = state.Status.ToString(),
                state.Endpoint,
                state.VfoHz,
                state.SampleRate,
                mode = state.Mode.ToString(),
                connectedBoard = connected.ToString(),
                effectiveBoard = effective.ToString(),
                orionMkIIVariant = variant.ToString(),
                capabilities = caps,
                dsp = _dsp.SnapshotDiagnostics(_wisdom),
                frontendDspScene = _frontendDspScene.Snapshot(),
                pureSignal = BuildPureSignalDiagnostics(state, connected, effective),
                digIn = BuildDigInDiagnostics(
                    _activeProtocol,
                    _p2Client is not null,
                    _p2Packets,
                    _p2LastUpdatedUtc,
                    connected,
                    effective,
                    variant,
                    _p2HadSample ? _p2Last : null),
                g2Sensors = BuildG2SensorMappingDiagnostics(
                    _activeProtocol,
                    _p2Client is not null,
                    _p2Packets,
                    _p2LastUpdatedUtc,
                    connected,
                    effective,
                    variant,
                    _p2HadSample ? _p2Last : null,
                    _p2HiPriorityMap.CandidateUnknownWords(12)),
                activeProtocol = _activeProtocol,
                p1 = new
                {
                    packets = _p1Packets,
                    lastUpdatedUtc = _p1LastUpdatedUtc,
                    lastC0Address = _p1LastC0Address,
                    lastAin0 = _p1LastAin0,
                    lastAin1 = _p1LastAin1,
                    exciterAdc = _p1ExciterAdc,
                    fwdAdc = _p1FwdAdc,
                    revAdc = _p1RevAdc,
                    userAdc0 = _p1UserAdc0,
                    userAdc1 = _p1UserAdc1,
                    supplyVoltsAdc = _p1SupplyVoltsAdc,
                    adcOverloadBits = _p1AdcOverloadBits,
                    hardwarePtt = _p1HardwarePtt,
                    cwKeyDown = _p1CwKeyDown,
                },
                p2 = new
                {
                    packets = _p2Packets,
                    lastUpdatedUtc = _p2LastUpdatedUtc,
                    pttIn = _p2HadSample ? _p2Last.PttIn : (bool?)null,
                    dotIn = _p2HadSample ? _p2Last.DotIn : (bool?)null,
                    dashIn = _p2HadSample ? _p2Last.DashIn : (bool?)null,
                    pllLocked = _p2HadSample ? _p2Last.PllLocked : (bool?)null,
                    sidetoneActive = _p2HadSample ? _p2Last.SidetoneActive : (bool?)null,
                    adcOverloadBits = _p2HadSample ? _p2Last.AdcOverloadBits : (byte?)null,
                    exciterAdc = _p2HadSample ? _p2Last.ExciterAdc : (ushort?)null,
                    fwdAdc = _p2HadSample ? _p2Last.FwdAdc : (ushort?)null,
                    revAdc = _p2HadSample ? _p2Last.RevAdc : (ushort?)null,
                    adc0MaxMagnitude = _p2HadSample ? _p2Last.Adc0MaxMagnitude : (ushort?)null,
                    adc1MaxMagnitude = _p2HadSample ? _p2Last.Adc1MaxMagnitude : (ushort?)null,
                    adc0MaxMagnitudeAtOverload = _p2Adc0MaxMagnitudeAtOverload,
                    adc1MaxMagnitudeAtOverload = _p2Adc1MaxMagnitudeAtOverload,
                    supplyVoltsAdc = _p2HadSample ? _p2Last.SupplyVoltsAdc : (ushort?)null,
                    userAdc0 = _p2HadSample ? _p2Last.UserAdc0 : (ushort?)null,
                    userAdc1 = _p2HadSample ? _p2Last.UserAdc1 : (ushort?)null,
                    userAdc2 = _p2HadSample ? _p2Last.UserAdc2 : (ushort?)null,
                    userAdc3 = _p2HadSample ? _p2Last.UserAdc3 : (ushort?)null,
                    userDigitalIn = _p2HadSample ? _p2Last.UserDigitalIn : (byte?)null,
                    hardwareLeds = _p2HadSample ? _p2Last.HardwareLeds : (ushort?)null,
                },
                mapping = new
                {
                    schemaVersion = 2,
                    p1 = _p1TelemetryMap.Snapshot(),
                    p2HiPriority = _p2HiPriorityMap.Snapshot(),
                    markers = _mappingMarkers
                        .OrderByDescending(m => m.Id)
                        .Select(m => m.Snapshot())
                        .ToArray(),
                },
                referenceMap = ReferenceMap,
                candidateSettings = CandidateSettings,
                featureSurfaces = FeatureSurfaces,
            };
        }
    }

    internal G2SensorMappingDiagnosticsDto G2SensorMappingSnapshot()
    {
        var connected = _radio.ConnectedBoardKind;
        var effective = _radio.EffectiveBoardKind;
        var variant = _radio.EffectiveOrionMkIIVariant;

        lock (_sync)
        {
            return BuildG2SensorMappingDiagnostics(
                _activeProtocol,
                _p2Client is not null,
                _p2Packets,
                _p2LastUpdatedUtc,
                connected,
                effective,
                variant,
                _p2HadSample ? _p2Last : null,
                _p2HiPriorityMap.CandidateUnknownWords(12));
        }
    }

    internal G2DigInDiagnosticsDto DigInSnapshot()
    {
        var connected = _radio.ConnectedBoardKind;
        var effective = _radio.EffectiveBoardKind;
        var variant = _radio.EffectiveOrionMkIIVariant;

        lock (_sync)
        {
            return BuildDigInDiagnostics(
                _activeProtocol,
                _p2Client is not null,
                _p2Packets,
                _p2LastUpdatedUtc,
                connected,
                effective,
                variant,
                _p2HadSample ? _p2Last : null);
        }
    }

    private static object BuildPureSignalDiagnostics(
        StateDto state,
        HpsdrBoardKind connected,
        HpsdrBoardKind effective)
    {
        bool external = state.PsFeedbackSource == PsFeedbackSource.External;
        bool externalPathSupported = SupportsExternalPureSignalFeedback(connected, effective);
        double feedback = Math.Clamp(state.PsFeedbackLevel, 0.0, 256.0);
        string health = EvaluatePureSignalFeedbackHealth(state, feedback, external, externalPathSupported);

        return new
        {
            schemaVersion = 1,
            enabled = state.PsEnabled,
            monitorEnabled = state.PsMonitorEnabled,
            auto = state.PsAuto,
            single = state.PsSingle,
            autoAttenuate = state.PsAutoAttenuate,
            feedbackSource = state.PsFeedbackSource.ToString(),
            externalFeedback = external,
            externalFeedbackPathSupported = externalPathSupported,
            rfBypassRequired = external,
            rfBypassSelected = external,
            feedbackLevelRaw = Round(feedback, 3),
            feedbackLevelPct = Round(feedback / 256.0 * 100.0, 1),
            feedbackTargetRaw = Round(PsFeedbackTargetRaw, 3),
            feedbackUsableMinRaw = PsFeedbackUsableMinRaw,
            feedbackUsableMaxRaw = PsFeedbackUsableMaxRaw,
            feedbackCenteredMinRaw = PsFeedbackCenteredMinRaw,
            feedbackCenteredMaxRaw = PsFeedbackCenteredMaxRaw,
            txFeedbackAttenuationDb = state.PsTxFeedbackAttenuationDb,
            txFeedbackAttenuationDbMin = state.PsTxFeedbackAttenuationDbMin,
            hwPeak = state.PsHwPeak,
            hwPeakDefault = state.PsHwPeakDefault,
            calState = state.PsCalState,
            correcting = state.PsCorrecting,
            calibrationStalled = state.PsCalibrationStalled,
            healthStatus = health,
            manualReference = external
                ? "ANAN G2 manual: external PureSignal feedback should enter RF Bypass from a coupler just above 0 dBm; the G2 has no external amp ALC, so software drive/PA gain must prevent saturation."
                : "Internal PureSignal feedback uses the radio's internal coupler path; external amplifier linearization requires the RF Bypass feedback source.",
            diagnosticRecommendation = PureSignalFeedbackRecommendation(health, external),
        };
    }

    private static bool SupportsExternalPureSignalFeedback(
        HpsdrBoardKind connected,
        HpsdrBoardKind effective)
    {
        // The G2 manual describes the external feedback path as RF Bypass into
        // the Saturn/OrionMkII family. Keep this diagnostic conservative until
        // another board family is explicitly verified.
        return connected == HpsdrBoardKind.OrionMkII
               || effective == HpsdrBoardKind.OrionMkII;
    }

    private static string EvaluatePureSignalFeedbackHealth(
        StateDto state,
        double feedback,
        bool external,
        bool externalPathSupported)
    {
        if (!state.PsEnabled) return "off";
        if (state.PsCalibrationStalled) return "calibration-stalled";
        if (external && !externalPathSupported) return "external-source-unverified";
        if (feedback <= 0) return state.PsCorrecting ? "feedback-missing" : "waiting-for-feedback";
        if (feedback < PsFeedbackUsableMinRaw) return "feedback-low";
        if (feedback > PsFeedbackUsableMaxRaw) return "feedback-high";
        if (state.PsCorrecting && feedback >= PsFeedbackCenteredMinRaw && feedback <= PsFeedbackCenteredMaxRaw)
            return "centered-correcting";
        if (state.PsCorrecting) return "correcting-usable";
        return "collecting-usable-feedback";
    }

    private static string PureSignalFeedbackRecommendation(string health, bool external) => health switch
    {
        "off" => "PureSignal is not armed; enable PS and key a safe two-tone or normal TX before judging correction or feedback health.",
        "calibration-stalled" => "PureSignal calibration is stalled; verify HW peak and feedback level before increasing drive or judging IMD.",
        "external-source-unverified" => "External feedback is selected on a board family without a verified RF Bypass path; use internal feedback or validate the board-specific routing first.",
        "feedback-missing" => external
            ? "PS says it is correcting but external feedback is now missing; stop relying on the correction and verify the RF Bypass coupler path."
            : "PS says it is correcting but feedback is now missing; verify the internal coupler path before judging TX quality.",
        "waiting-for-feedback" => external
            ? "External feedback has not reached calcc yet; verify RF Bypass is the selected feedback source, the coupler is connected, and the transmitter is keyed into a safe load."
            : "PureSignal is waiting for feedback; key a safe two-tone or normal TX long enough for calcc to collect samples.",
        "feedback-low" => external
            ? "External RF Bypass feedback is below the useful calcc window. The G2 manual expects a coupler sample just above 0 dBm at normal operation; reduce attenuation or check coupler direction before raising drive."
            : "PureSignal feedback is below the useful calcc window; reduce feedback attenuation or verify the internal coupler path.",
        "feedback-high" => external
            ? "External RF Bypass feedback is hot. Add feedback attenuation or reduce the coupler sample before increasing drive; the G2 has no external amp ALC to save a saturated amplifier."
            : "PureSignal feedback is hot; add feedback attenuation before increasing drive.",
        "centered-correcting" => external
            ? "External RF Bypass feedback is centered and correcting. Keep using software drive/PA gain as the saturation guard because the G2 provides no external amp ALC."
            : "PureSignal feedback is centered and correcting.",
        "correcting-usable" => external
            ? "External RF Bypass feedback is usable and correcting, but not centered. Fine-tune feedback attenuation toward the 138-176 raw window before peak-fidelity IMD work."
            : "PureSignal feedback is usable and correcting; fine-tune feedback attenuation toward the centered window.",
        _ => external
            ? "External RF Bypass feedback is in range but correction is not active yet; keep the safe two-tone/TX condition steady until calcc converges."
            : "PureSignal feedback is in range but correction is not active yet; keep TX conditions steady until calcc converges.",
    };

    internal static G2SensorMappingDiagnosticsDto BuildG2SensorMappingDiagnostics(
        string? activeProtocol,
        bool p2Attached,
        long p2Packets,
        DateTimeOffset? p2LastUpdatedUtc,
        HpsdrBoardKind connected,
        HpsdrBoardKind effective,
        OrionMkIIVariant variant,
        Zeus.Protocol2.P2TelemetryReading? p2Reading,
        IReadOnlyList<G2CandidateTelemetryWordDto>? candidateWords = null)
    {
        bool g2Class = IsG2ManualSensorBoard(effective, variant);
        bool hasP2Sample = p2Reading is not null;
        string status = G2SensorMappingStatus(g2Class, activeProtocol, hasP2Sample);

        var mapped = new[]
        {
            MappedSensor("exciter-power", "Exciter power ADC", "p2.exciterAdc", Raw(p2Reading?.ExciterAdc),
                "Thetis P2 hi-priority word 0x02", hasP2Sample,
                "Raw exciter-drive evidence for TX chain health and low-level RF presence."),
            MappedSensor("pa-forward-power", "PA forward power ADC", "p2.fwdAdc", Raw(p2Reading?.FwdAdc),
                "Thetis P2 hi-priority word 0x0A", hasP2Sample,
                "Feeds the existing PA watts/SWR calibration snapshot; use with TX egress diagnostics before changing drive."),
            MappedSensor("pa-reverse-power", "PA reverse power ADC", "p2.revAdc", Raw(p2Reading?.RevAdc),
                "Thetis P2 hi-priority word 0x12", hasP2Sample,
                "Feeds reflected-power and SWR evidence; required before any automated TX protection decision."),
            MappedSensor("supply-voltage", "Supply volts ADC", "p2.supplyVoltsAdc", Raw(p2Reading?.SupplyVoltsAdc),
                "Thetis P2 hi-priority word 0x2D", hasP2Sample,
                "Raw supply-voltage ADC is live; G2 scaling remains advisory until per-radio calibration is verified."),
            MappedSensor("user-adc0", "User ADC 0", "p2.userAdc0", Raw(p2Reading?.UserAdc0),
                "Thetis P2 hi-priority word 0x35", hasP2Sample,
                "Accessory analog input; label and action bindings stay separate from PA sensor mapping."),
            MappedSensor("user-adc1", "User ADC 1", "p2.userAdc1", Raw(p2Reading?.UserAdc1),
                "Thetis P2 hi-priority word 0x33", hasP2Sample,
                "Accessory analog input; useful as a correlation reference when mapping unknown sensor words."),
            MappedSensor("user-adc2", "User ADC 2", "p2.userAdc2", Raw(p2Reading?.UserAdc2),
                "Thetis P2 hi-priority word 0x31", hasP2Sample,
                "Accessory analog input; useful as a correlation reference when mapping unknown sensor words."),
            MappedSensor("user-adc3", "User ADC 3", "p2.userAdc3", Raw(p2Reading?.UserAdc3),
                "Thetis P2 hi-priority word 0x2F", hasP2Sample,
                "Accessory analog input; useful as a correlation reference when mapping unknown sensor words."),
            MappedSensor("user-digital", "User digital inputs", "p2.userDigitalIn", Raw(p2Reading?.UserDigitalIn),
                "Thetis P2 hi-priority byte 0x37", hasP2Sample,
                "Includes the decoded G2 Dig In TX Disable line; read-only until a board-gated inhibit policy is explicitly armed."),
            MappedSensor("hardware-leds", "Hardware LED/front-panel word", "p2.hardwareLeds", Raw(p2Reading?.HardwareLeds),
                "Thetis P2 hi-priority word 0x1A", hasP2Sample,
                "Remote mirror for hardware/front-panel reconciliation, not a PA protection sensor."),
        };

        var unmapped = g2Class
            ? new[]
            {
                UnmappedSensor(
                    "pa-temperature",
                    "PA temperature sensor",
                    "The G2 manual lists temperature sensors and internal/external fan cooling support.",
                    "p2-g2-temperature-slot-unmapped",
                    "Add baseline, low-power TX warm-up, fan transition, and cool-down markers; accept only a monotonic thermal candidate before publishing degrees C.",
                    "tx-protection-mapping-required"),
                UnmappedSensor(
                    "pa-current",
                    "PA current",
                    "The G2 biascheck utility reads PA Current from hardware telemetry.",
                    "p2-g2-pa-current-unmapped",
                    "Capture receive idle, TUN into a dummy load at low drive, and 30 W continuous-duty markers; correlate against supply volts and forward power before scaling amps.",
                    "tx-monitoring-only"),
                UnmappedSensor(
                    "driver-current",
                    "Driver current",
                    "The G2 biascheck utility reads Driver Current separately from PA Current.",
                    "p2-g2-driver-current-unmapped",
                    "Capture receive idle plus staged TUN drive markers; require separation from forward-power ADC before using it as bias evidence.",
                    "tx-monitoring-only"),
                UnmappedSensor(
                    "fan-state",
                    "Internal/external fan state",
                    "The G2 manual documents internal fan and external fan support tied to PA cooling.",
                    "p2-g2-fan-state-unmapped",
                    "Capture manual fan transitions or thermal threshold crossings with mapping markers; do not infer fan state from temperature alone.",
                    "tx-monitoring-only"),
            }
            : Array.Empty<G2UnmappedManualSensorDto>();

        return new G2SensorMappingDiagnosticsDto(
            SchemaVersion: 1,
            ActiveProtocol: activeProtocol,
            ConnectedBoard: connected.ToString(),
            EffectiveBoard: effective.ToString(),
            OrionMkIIVariant: variant.ToString(),
            G2Class: g2Class,
            P2Attached: p2Attached,
            P2Packets: p2Packets,
            P2LastUpdatedUtc: p2LastUpdatedUtc,
            Status: status,
            MappedSensors: mapped,
            UnmappedManualSensors: unmapped,
            CandidateWords: candidateWords?.ToArray() ?? [],
            ManualReference: "ANAN G2 manual V1.4: PA/supply feature list and biascheck tooling document current, voltage, temperature sensors, thermally compensated bias, and internal/external fan support.",
            DiagnosticRecommendation: G2SensorMappingRecommendation(status, mapped, unmapped),
            GeneratedUtc: DateTimeOffset.UtcNow);
    }

    private static G2MappedSensorDto MappedSensor(
        string id,
        string label,
        string telemetryPath,
        int? rawValue,
        string source,
        bool hasP2Sample,
        string notes) =>
        new(
            Id: id,
            Label: label,
            TelemetryPath: telemetryPath,
            Source: source,
            RawValue: rawValue,
            Status: hasP2Sample ? "mapped-live" : "mapped-waiting-for-p2-sample",
            Notes: notes);

    private static G2UnmappedManualSensorDto UnmappedSensor(
        string id,
        string label,
        string manualEvidence,
        string currentTelemetryStatus,
        string requiredCapture,
        string safetyClass) =>
        new(
            Id: id,
            Label: label,
            ManualEvidence: manualEvidence,
            CurrentTelemetryStatus: currentTelemetryStatus,
            RequiredCapture: requiredCapture,
            SafetyClass: safetyClass);

    private static int? Raw(ushort? value) => value.HasValue ? value.Value : null;

    private static int? Raw(byte? value) => value.HasValue ? value.Value : null;

    private static bool IsG2ManualSensorBoard(HpsdrBoardKind effective, OrionMkIIVariant variant) =>
        effective == HpsdrBoardKind.OrionMkII
        && variant is OrionMkIIVariant.G2 or OrionMkIIVariant.G2_1K;

    private static string G2SensorMappingStatus(bool g2Class, string? activeProtocol, bool hasP2Sample)
    {
        if (!g2Class) return "not-g2";
        if (activeProtocol != "P2") return "waiting-for-p2";
        if (!hasP2Sample) return "waiting-for-telemetry";
        return "manual-sensors-unmapped";
    }

    private static string G2SensorMappingRecommendation(
        string status,
        IReadOnlyCollection<G2MappedSensorDto> mapped,
        IReadOnlyCollection<G2UnmappedManualSensorDto> unmapped) => status switch
    {
        "not-g2" => "This diagnostic targets ANAN G2/Saturn-class manual sensor mapping; use the generic PA supply, PA thermal, and user-I/O diagnostics for this board.",
        "waiting-for-p2" => "Connect the G2 with Protocol 2 before mapping PA current, driver current, temperature, or fan telemetry.",
        "waiting-for-telemetry" => "Protocol 2 is attached but no hi-priority sample has arrived yet; wait for UDP 1025 telemetry before adding sensor mapping markers.",
        _ => $"{mapped.Count} live P2 telemetry fields are mapped, while {unmapped.Count} G2 manual sensor fields still require marker correlation. Use Settings > Hardware > Mapping Capture with one controlled action at a time before trusting any current, fan, or P2 thermal automation.",
    };

    internal static G2DigInDiagnosticsDto BuildDigInDiagnostics(
        string? activeProtocol,
        bool p2Attached,
        long p2Packets,
        DateTimeOffset? p2LastUpdatedUtc,
        HpsdrBoardKind connected,
        HpsdrBoardKind effective,
        OrionMkIIVariant variant,
        Zeus.Protocol2.P2TelemetryReading? p2Reading)
    {
        int txDisableBit = P2TxDisableBit(effective, variant);
        string txDisableLineId = $"userDigital{txDisableBit}";
        string txDisableLineName = txDisableBit == 1
            ? "User I/O IO5"
            : "User I/O IO4";
        byte? userDigital = p2Reading?.UserDigitalIn;
        bool? rawHigh = userDigital is { } value
            ? (value & (1 << txDisableBit)) != 0
            : null;
        bool? active = rawHigh is { } high ? !high : null;
        bool? cwTip = p2Reading?.DotIn;
        bool? dash = p2Reading?.DashIn;
        bool saturnMapping = UsesSaturnP2TxDisableLine(effective, variant);
        string mappingStatus = activeProtocol == "P2"
            ? saturnMapping
                ? "thetis-p2-saturn-io5"
                : "thetis-p2-io4"
            : "waiting-for-p2";

        return new G2DigInDiagnosticsDto(
            SchemaVersion: 1,
            ActiveProtocol: activeProtocol,
            ConnectedBoard: connected.ToString(),
            EffectiveBoard: effective.ToString(),
            OrionMkIIVariant: variant.ToString(),
            P2Attached: p2Attached,
            P2Packets: p2Packets,
            P2LastUpdatedUtc: p2LastUpdatedUtc,
            UserDigitalIn: userDigital,
            TxDisableLineId: txDisableLineId,
            TxDisableLineName: txDisableLineName,
            TxDisableBit: txDisableBit,
            TxDisableRawHigh: rawHigh,
            TxDisableActive: active,
            TxDisablePolarity: "active-low",
            TxDisableMappingStatus: mappingStatus,
            TxInhibitBehaviorArmed: false,
            CwKeyTipSource: "p2.dotIn",
            CwKeyTipDown: cwTip,
            CwDashInputDown: dash,
            ManualReference: "ANAN G2 manual: rear-panel Dig In stereo jack uses tip for CW key and ring for TX Disable; grounding TX Disable signals the SDR client to request transmit inhibit. Thetis maps G2-class Protocol-2 TX Inhibit to HPSP 1025 byte 59 bit 1, exposed in Zeus as p2.userDigitalIn bit 1 / userDigital1.",
            DiagnosticRecommendation: DigInRecommendation(activeProtocol, p2Reading is not null, active),
            GeneratedUtc: DateTimeOffset.UtcNow);
    }

    private static bool UsesSaturnP2TxDisableLine(HpsdrBoardKind effective, OrionMkIIVariant variant) =>
        effective == HpsdrBoardKind.OrionMkII
        && variant is OrionMkIIVariant.Anan7000DLE
            or OrionMkIIVariant.Anan8000DLE
            or OrionMkIIVariant.G2
            or OrionMkIIVariant.G2_1K
            or OrionMkIIVariant.AnvelinaPro3
            or OrionMkIIVariant.RedPitaya;

    private static int P2TxDisableBit(HpsdrBoardKind effective, OrionMkIIVariant variant) =>
        UsesSaturnP2TxDisableLine(effective, variant) ? 1 : 0;

    private static string DigInRecommendation(
        string? activeProtocol,
        bool hasP2Sample,
        bool? txDisableActive)
    {
        if (activeProtocol != "P2")
            return "Dig In TX Disable mapping is currently decoded from Protocol-2 hi-priority status; connect a P2 G2/Saturn-class radio before using this diagnostic.";

        if (!hasP2Sample)
            return "Protocol 2 is attached but no hi-priority user-digital sample has arrived yet; wait for HPSP 1025 telemetry or add a mapping marker while toggling the Dig In ring contact.";

        if (txDisableActive == true)
            return "Dig In TX Disable is active-low and currently active. Zeus reports the inhibit state but does not block MOX/TUN yet; do not rely on it as the sole transmit safety interlock until a hardware-inhibit policy is explicitly armed.";

        return "Dig In TX Disable is inactive. The mapped line is monitored read-only; future TX inhibit behavior should be opt-in and gated to verified board mappings.";
    }

    public HardwareKeyingStatusDto KeyingSnapshot(ExternalPttStatusDto externalPtt)
    {
        ArgumentNullException.ThrowIfNull(externalPtt);

        lock (_sync)
        {
            string recommendation = _activeProtocol switch
            {
                "P1" => "Protocol-1 hardware PTT and CW key echo are live; external PTT takeover status is available separately.",
                "P2" => "Protocol-2 PTT, dot, dash, and sidetone telemetry are live; host-side external PTT takeover is Protocol-1 only.",
                _ => "No hardware keying telemetry is attached; connect a radio to audit PTT, dot, dash, and sidetone inputs.",
            };

            return new(
                SchemaVersion: 1,
                ActiveProtocol: _activeProtocol,
                P1Packets: _p1Packets,
                P1LastUpdatedUtc: _p1LastUpdatedUtc,
                P1HardwarePtt: _p1HardwarePtt,
                P1CwKeyDown: _p1CwKeyDown,
                P2Packets: _p2Packets,
                P2LastUpdatedUtc: _p2LastUpdatedUtc,
                P2PttIn: _p2HadSample ? _p2Last.PttIn : (bool?)null,
                P2DotIn: _p2HadSample ? _p2Last.DotIn : (bool?)null,
                P2DashIn: _p2HadSample ? _p2Last.DashIn : (bool?)null,
                P2SidetoneActive: _p2HadSample ? _p2Last.SidetoneActive : (bool?)null,
                ExternalPtt: externalPtt,
                DiagnosticRecommendation: recommendation,
                GeneratedUtc: DateTimeOffset.UtcNow);
        }
    }

    public RadioPowerCalibrationDto PowerCalibrationSnapshot()
    {
        var connected = _radio.ConnectedBoardKind;
        var effective = _radio.EffectiveBoardKind;
        var variant = _radio.EffectiveOrionMkIIVariant;
        var cal = RadioCalibrations.For(connected, variant);
        var caps = BoardCapabilitiesTable.For(effective, variant);

        lock (_sync)
        {
            var p1 = BuildPowerReading(
                _p1Packets,
                _p1LastUpdatedUtc,
                _p1ExciterAdc,
                _p1FwdAdc,
                _p1RevAdc,
                cal);
            var p2 = BuildPowerReading(
                _p2Packets,
                _p2LastUpdatedUtc,
                _p2HadSample ? _p2Last.ExciterAdc : (ushort?)null,
                _p2HadSample ? _p2Last.FwdAdc : (ushort?)null,
                _p2HadSample ? _p2Last.RevAdc : (ushort?)null,
                cal);
            var recommendation = p1.FwdWatts is null && p2.FwdWatts is null
                ? "No PA forward/reflected telemetry has been observed yet; connect and key the radio at low drive to validate the bridge ADC path."
                : "PA telemetry is decoded with the same board calibration and watts/SWR math used by the live TX meters.";

            return new(
                SchemaVersion: 1,
                ActiveProtocol: _activeProtocol,
                ConnectedBoard: connected.ToString(),
                EffectiveBoard: effective.ToString(),
                OrionMkIIVariant: variant.ToString(),
                CalibrationBoard: connected.ToString(),
                BridgeVolt: cal.BridgeVolt,
                RefVoltage: cal.RefVoltage,
                AdcCalOffset: cal.AdcCalOffset,
                CalibrationMaxWatts: cal.MaxWatts,
                CalibrationFallbackApplied: connected == HpsdrBoardKind.Unknown,
                CapabilityMaxPowerWatts: caps.MaxPowerWatts,
                P1: p1,
                P2: p2,
                DiagnosticRecommendation: recommendation,
                GeneratedUtc: DateTimeOffset.UtcNow);
        }
    }

    public RadioSupplyAlarmsDto SupplyAlarmsSnapshot()
    {
        var effective = _radio.EffectiveBoardKind;
        var variant = _radio.EffectiveOrionMkIIVariant;
        var caps = BoardCapabilitiesTable.For(effective, variant);

        lock (_sync)
        {
            var p1 = BuildSupplyReading(
                _p1Packets,
                _p1LastUpdatedUtc,
                _p1SupplyVoltsAdc,
                caps.AdcSupplyMv,
                effective,
                variant);
            var p2 = BuildSupplyReading(
                _p2Packets,
                _p2LastUpdatedUtc,
                _p2HadSample ? _p2Last.SupplyVoltsAdc : (ushort?)null,
                caps.AdcSupplyMv,
                effective,
                variant);
            var (alarmActive, alarmStatus, recommendation) =
                EvaluateSupplyTelemetry(caps, p1, p2);

            return new(
                SchemaVersion: 1,
                ActiveProtocol: _activeProtocol,
                EffectiveBoard: effective.ToString(),
                OrionMkIIVariant: variant.ToString(),
                SupportsSupplyTelemetry: caps.HasVolts,
                AdcSupplyMv: caps.AdcSupplyMv,
                ActiveThresholdsConfigured: false,
                AlarmActive: alarmActive,
                AlarmStatus: alarmStatus,
                P1: p1,
                P2: p2,
                DiagnosticRecommendation: recommendation,
                GeneratedUtc: DateTimeOffset.UtcNow);
        }
    }

    public RadioNetworkProfileDto NetworkProfileSnapshot()
    {
        var state = _radio.Snapshot();
        var connected = _radio.ConnectedBoardKind;
        var effective = _radio.EffectiveBoardKind;
        var variant = _radio.EffectiveOrionMkIIVariant;

        lock (_sync)
        {
            var p1 = BuildNetworkCounters(_p1Client);
            var p2 = BuildNetworkCounters(_p2Client);
            var (healthStatus, recommendation) =
                EvaluateNetworkProfile(state.Status, _activeProtocol, p1, p2);

            return new(
                SchemaVersion: 1,
                ConnectionStatus: state.Status.ToString(),
                Endpoint: state.Endpoint,
                ActiveProtocol: _activeProtocol,
                SampleRateHz: state.SampleRate,
                ConnectedBoard: connected.ToString(),
                EffectiveBoard: effective.ToString(),
                OrionMkIIVariant: variant.ToString(),
                Transport: "udp",
                P1: p1,
                P2: p2,
                HealthStatus: healthStatus,
                DiagnosticRecommendation: recommendation,
                GeneratedUtc: DateTimeOffset.UtcNow);
        }
    }

    public UserIoLabelsDto UserIoLabelsSnapshot()
    {
        lock (_sync)
        {
            var lines = BuildUserIoLines(_p2HadSample ? _p2Last : null);
            var recommendation = _p2HadSample
                ? "P2 user I/O lines are decoded with default labels; add a mapping marker before treating any line as a station action."
                : "No P2 user I/O sample has arrived yet; connect a Protocol-2 radio and use mapping markers to correlate accessory lines.";

            return new(
                SchemaVersion: 1,
                ActiveProtocol: _activeProtocol,
                P2Attached: _p2Client is not null,
                P2Packets: _p2Packets,
                P2LastUpdatedUtc: _p2LastUpdatedUtc,
                Lines: lines,
                DiagnosticRecommendation: recommendation,
                GeneratedUtc: DateTimeOffset.UtcNow);
        }
    }

    public UserIoActionsDto UserIoActionsSnapshot()
    {
        lock (_sync)
        {
            var lines = BuildUserIoLines(_p2HadSample ? _p2Last : null);
            var recommendation = _p2HadSample
                ? "User I/O action bindings are intentionally unarmed; verify line identity with diagnostics markers before binding station automation."
                : "No P2 user I/O state is available yet, so action bindings remain unarmed.";

            return new(
                SchemaVersion: 1,
                ActiveProtocol: _activeProtocol,
                P2Attached: _p2Client is not null,
                P2Packets: _p2Packets,
                P2LastUpdatedUtc: _p2LastUpdatedUtc,
                ActionBindingsConfigured: false,
                Lines: lines,
                DiagnosticRecommendation: recommendation,
                GeneratedUtc: DateTimeOffset.UtcNow);
        }
    }

    public void ResetMapping()
    {
        lock (_sync)
        {
            _p1TelemetryMap.Reset();
            _p2HiPriorityMap.Reset();
            _p2Adc0MaxMagnitudeAtOverload = 0;
            _p2Adc1MaxMagnitudeAtOverload = 0;
            _mappingMarkers.Clear();
            _nextMarkerId = 1;
        }
    }

    public void AddMappingMarker(string? label, string? notes)
    {
        lock (_sync)
        {
            int id = _nextMarkerId++;
            string markerLabel = CleanMarkerText(label, $"Marker {id}", 80);
            string? markerNotes = string.IsNullOrWhiteSpace(notes)
                ? null
                : CleanMarkerText(notes, string.Empty, 220);
            var p1Capture = _p1TelemetryMap.Capture();
            var p2Capture = _p2HiPriorityMap.Capture();
            var previous = _mappingMarkers.Count == 0 ? null : _mappingMarkers[^1];
            var current = new MappingMarker(
                id,
                markerLabel,
                markerNotes,
                DateTimeOffset.UtcNow,
                _activeProtocol,
                _radio.Snapshot().Endpoint,
                _p1Packets,
                _p2Packets,
                p1Capture,
                p2Capture,
                BuildMarkerDelta(previous, p1Capture, p2Capture));
            _mappingMarkers.Add(current);
            if (_mappingMarkers.Count > 64)
                _mappingMarkers.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        _radio.Connected -= OnP1Connected;
        _radio.Disconnected -= OnP1Disconnected;
        _radio.P2Connected -= OnP2Connected;
        _radio.P2Disconnected -= OnP2Disconnected;
        OnP1Disconnected();
        OnP2Disconnected();
    }

    private void OnP1Connected(IProtocol1Client client)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_p1Client, client)) return;
            DetachP1_NoLock();
            _p1Client = client;
            _activeProtocol = "P1";
            _p1HardwarePtt = client.HardwarePtt;
            _p1CwKeyDown = client.CwKeyDown;
        }
        client.TelemetryReceived += OnP1Telemetry;
        client.AdcOverloadObserved += OnP1AdcOverload;
        client.HardwarePttChanged += OnP1HardwarePtt;
        client.CwKeyDownChanged += OnP1CwKeyDown;
    }

    private void OnP1Disconnected()
    {
        lock (_sync)
        {
            DetachP1_NoLock();
            if (_activeProtocol == "P1") _activeProtocol = null;
        }
    }

    private void DetachP1_NoLock()
    {
        if (_p1Client is not { } client) return;
        client.TelemetryReceived -= OnP1Telemetry;
        client.AdcOverloadObserved -= OnP1AdcOverload;
        client.HardwarePttChanged -= OnP1HardwarePtt;
        client.CwKeyDownChanged -= OnP1CwKeyDown;
        _p1Client = null;
    }

    private void OnP2Connected(Zeus.Protocol2.Protocol2Client client)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_p2Client, client)) return;
            DetachP2_NoLock();
            _p2Client = client;
            _activeProtocol = "P2";
        }
        client.TelemetryReceived += OnP2Telemetry;
        client.HiPriorityStatusPayloadReceived += OnP2HiPriorityPayload;
    }

    private void OnP2Disconnected()
    {
        lock (_sync)
        {
            DetachP2_NoLock();
            if (_activeProtocol == "P2") _activeProtocol = null;
        }
    }

    private void DetachP2_NoLock()
    {
        if (_p2Client is not { } client) return;
        client.TelemetryReceived -= OnP2Telemetry;
        client.HiPriorityStatusPayloadReceived -= OnP2HiPriorityPayload;
        _p2Client = null;
    }

    private static RadioPowerReadingDto BuildPowerReading(
        long packets,
        DateTimeOffset? lastUpdatedUtc,
        ushort? exciterAdc,
        ushort? fwdAdc,
        ushort? revAdc,
        RadioCalibration cal)
    {
        double? fwdWatts = null;
        double? refWatts = null;
        double? swr = null;
        if (fwdAdc is { } fwd && revAdc is { } rev)
        {
            var computed = TxMetersService.ComputeMeters(fwd, rev, cal);
            fwdWatts = Round(computed.FwdWatts, 2);
            refWatts = Round(computed.RefWatts, 2);
            swr = Round(computed.Swr, 2);
        }

        return new(
            Packets: packets,
            LastUpdatedUtc: lastUpdatedUtc,
            ExciterAdc: exciterAdc,
            FwdAdc: fwdAdc,
            RevAdc: revAdc,
            FwdWatts: fwdWatts,
            RefWatts: refWatts,
            Swr: swr);
    }

    internal static RadioSupplyReadingDto BuildSupplyReading(
        long packets,
        DateTimeOffset? lastUpdatedUtc,
        ushort? supplyVoltsAdc,
        int adcSupplyMv,
        HpsdrBoardKind effectiveBoard,
        OrionMkIIVariant variant)
    {
        var rawScaledVolts = supplyVoltsAdc is { } adc && adcSupplyMv > 0
            ? Round(adc * adcSupplyMv / 1000.0, 2)
            : (double?)null;
        var scaleStatus = SupplyScaleStatus(rawScaledVolts, effectiveBoard, variant);
        var trusted = scaleStatus == "trusted";
        return new(
            Packets: packets,
            LastUpdatedUtc: lastUpdatedUtc,
            SupplyVoltsAdc: supplyVoltsAdc,
            SupplyVolts: trusted ? rawScaledVolts : null,
            RawScaledSupplyVolts: rawScaledVolts,
            SupplyVoltsTrusted: trusted,
            ScaleStatus: scaleStatus);
    }

    internal static (bool AlarmActive, string AlarmStatus, string Recommendation)
        EvaluateSupplyTelemetry(
            BoardCapabilities caps,
            RadioSupplyReadingDto p1,
            RadioSupplyReadingDto p2)
    {
        if (!caps.HasVolts)
        {
            return (
                false,
                "unsupported",
                "The effective board does not advertise supply-voltage telemetry; PA protection should use SWR and timeout guards for this hardware.");
        }

        if (p1.SupplyVoltsAdc is null && p2.SupplyVoltsAdc is null)
        {
            return (
                false,
                "missing",
                "Supply telemetry is supported but no voltage sample has arrived yet; connect the radio and watch P1/P2 telemetry before arming supply alarms.");
        }

        if (IsInvalidSupply(p1) || IsInvalidSupply(p2))
        {
            return (
                true,
                "invalid-zero",
                "A supply telemetry sample decoded to zero volts; inspect the ADC mapping or hardware before trusting PA operation.");
        }

        if (IsUnverifiedSupplyScale(p1) || IsUnverifiedSupplyScale(p2))
        {
            return (
                false,
                "scale-unverified",
                "Supply telemetry is arriving, but the static board scale produces an implausible voltage for this radio. Treat the raw ADC as diagnostics-only and add per-radio calibration/thresholds before using it for TX inhibit.");
        }

        return (
            false,
            "telemetry-ready",
            "Live supply voltage is decoded and scaled; add per-radio high/low thresholds before using this diagnostic surface to inhibit TX.");
    }

    private static bool IsInvalidSupply(RadioSupplyReadingDto reading) =>
        reading.ScaleStatus == "invalid-zero";

    private static bool IsUnverifiedSupplyScale(RadioSupplyReadingDto reading) =>
        reading.ScaleStatus == "scale-unverified";

    private static string SupplyScaleStatus(
        double? rawScaledVolts,
        HpsdrBoardKind effectiveBoard,
        OrionMkIIVariant variant)
    {
        if (rawScaledVolts is null) return "missing";
        if (rawScaledVolts <= 0.0) return "invalid-zero";
        if (!SupplyScalePlausible(rawScaledVolts.Value, effectiveBoard, variant))
            return "scale-unverified";
        return "trusted";
    }

    private static bool SupplyScalePlausible(
        double volts,
        HpsdrBoardKind effectiveBoard,
        OrionMkIIVariant variant)
    {
        if (effectiveBoard == HpsdrBoardKind.OrionMkII && variant == OrionMkIIVariant.G2)
            return volts is >= G2SupplyPlausibleMinVolts and <= G2SupplyPlausibleMaxVolts;
        return volts <= GenericSupplyPlausibleMaxVolts;
    }

    private static double Round(double value, int digits) =>
        double.IsFinite(value) ? Math.Round(value, digits) : 0.0;

    private static RadioNetworkCountersDto BuildNetworkCounters(IProtocol1Client? client)
    {
        if (client is null)
            return EmptyNetworkCounters();

        var total = client.TotalFrames;
        var dropped = client.DroppedFrames;
        return new(
            Attached: true,
            TotalFrames: total,
            DroppedFrames: dropped,
            DropRatioPct: DropRatioPct(total, dropped),
            HiPriorityPackets: null,
            PsPairedPackets: client.PsPairedPacketCount);
    }

    private static RadioNetworkCountersDto BuildNetworkCounters(Zeus.Protocol2.Protocol2Client? client)
    {
        if (client is null)
            return EmptyNetworkCounters();

        var total = client.TotalFrames;
        var dropped = client.DroppedFrames;
        return new(
            Attached: true,
            TotalFrames: total,
            DroppedFrames: dropped,
            DropRatioPct: DropRatioPct(total, dropped),
            HiPriorityPackets: client.HiPriPacketCount,
            PsPairedPackets: null);
    }

    private static RadioNetworkCountersDto EmptyNetworkCounters() => new(
        Attached: false,
        TotalFrames: 0,
        DroppedFrames: 0,
        DropRatioPct: 0,
        HiPriorityPackets: null,
        PsPairedPackets: null);

    private static double DropRatioPct(long totalFrames, long droppedFrames)
    {
        var denominator = totalFrames + droppedFrames;
        if (denominator <= 0) return 0;
        return Round(droppedFrames * 100.0 / denominator, 3);
    }

    private static (string HealthStatus, string Recommendation) EvaluateNetworkProfile(
        ConnectionStatus status,
        string? activeProtocol,
        RadioNetworkCountersDto p1,
        RadioNetworkCountersDto p2)
    {
        if (status != ConnectionStatus.Connected)
        {
            return (
                "disconnected",
                "No radio transport is active; connect a radio before evaluating packet loss or sample-rate headroom.");
        }

        var active = activeProtocol switch
        {
            "P1" => p1,
            "P2" => p2,
            _ => null,
        };

        if (active is null || !active.Attached)
        {
            return (
                "state-only",
                "Radio state is connected but no protocol client is attached to diagnostics; reconnect to bind transport counters.");
        }

        if (active.TotalFrames <= 0)
        {
            return (
                "waiting-for-rx",
                "The transport is attached but no RX IQ frames have arrived yet; verify the radio stream has started and the selected sample rate is supported.");
        }

        if (active.DroppedFrames > 0)
        {
            return (
                "loss-detected",
                "UDP sequence gaps are present; reduce RX sample rate, check NIC power management, and prefer a direct wired LAN path before judging DSP quality.");
        }

        return (
            "clean",
            "The active transport has delivered RX frames without observed sequence gaps in this session.");
    }

    private static UserIoLineDto[] BuildUserIoLines(Zeus.Protocol2.P2TelemetryReading? reading)
    {
        ushort? userAdc0 = reading?.UserAdc0;
        ushort? userAdc1 = reading?.UserAdc1;
        ushort? userAdc2 = reading?.UserAdc2;
        ushort? userAdc3 = reading?.UserAdc3;
        byte? digital = reading?.UserDigitalIn;

        var lines = new List<UserIoLineDto>(12)
        {
            AnalogLine("userAdc0", "User ADC 0", userAdc0),
            AnalogLine("userAdc1", "User ADC 1", userAdc1),
            AnalogLine("userAdc2", "User ADC 2", userAdc2),
            AnalogLine("userAdc3", "User ADC 3", userAdc3),
        };

        for (int bit = 0; bit < 8; bit++)
        {
            bool? state = digital is { } value ? (value & (1 << bit)) != 0 : null;
            lines.Add(new(
                Id: $"userDigital{bit}",
                Kind: "digital",
                Label: $"User Digital {bit}",
                RawAdc: null,
                NormalizedPct: null,
                DigitalState: state));
        }

        return lines.ToArray();
    }

    private static UserIoLineDto AnalogLine(string id, string label, ushort? raw)
    {
        double? pct = raw is { } value
            ? Round(value / 65535.0 * 100.0, 2)
            : null;
        return new(
            Id: id,
            Kind: "analog",
            Label: label,
            RawAdc: raw,
            NormalizedPct: pct,
            DigitalState: null);
    }

    private void OnP1Telemetry(TelemetryReading reading)
    {
        lock (_sync)
        {
            _p1Packets++;
            _p1LastUpdatedUtc = DateTimeOffset.UtcNow;
            _p1LastC0Address = reading.C0Address;
            _p1LastAin0 = reading.Ain0;
            _p1LastAin1 = reading.Ain1;
            _p1TelemetryMap.Observe(reading);

            switch (reading.C0Address & 0x7E)
            {
                case 0x08:
                    _p1ExciterAdc = reading.Ain0;
                    _p1FwdAdc = reading.Ain1;
                    break;
                case 0x10:
                    _p1RevAdc = reading.Ain0;
                    _p1UserAdc0 = reading.Ain1;
                    break;
                case 0x18:
                    _p1UserAdc1 = reading.Ain0;
                    _p1SupplyVoltsAdc = reading.Ain1;
                    break;
            }
        }
    }

    private void OnP1AdcOverload(AdcOverloadStatus status)
    {
        lock (_sync)
        {
            _p1AdcOverloadBits =
                (byte)((status.Adc0 ? 0x01 : 0x00) | (status.Adc1 ? 0x02 : 0x00));
            _p1LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    private void OnP1HardwarePtt(bool on)
    {
        lock (_sync)
        {
            _p1HardwarePtt = on;
            _p1LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    private void OnP1CwKeyDown(bool on)
    {
        lock (_sync)
        {
            _p1CwKeyDown = on;
            _p1LastUpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    private void OnP2Telemetry(Zeus.Protocol2.P2TelemetryReading reading)
    {
        lock (_sync)
        {
            bool adc0Overload = (reading.AdcOverloadBits & 0x01) != 0;
            bool adc1Overload = (reading.AdcOverloadBits & 0x02) != 0;
            if (!_p2PrevAdc0Overload && adc0Overload)
                _p2Adc0MaxMagnitudeAtOverload = reading.Adc0MaxMagnitude;
            if (!_p2PrevAdc1Overload && adc1Overload)
                _p2Adc1MaxMagnitudeAtOverload = reading.Adc1MaxMagnitude;
            _p2PrevAdc0Overload = adc0Overload;
            _p2PrevAdc1Overload = adc1Overload;

            _p2Packets++;
            _p2LastUpdatedUtc = DateTimeOffset.UtcNow;
            _p2Last = reading;
            _p2HadSample = true;
        }
    }

    private void OnP2HiPriorityPayload(byte[] payload)
    {
        lock (_sync)
        {
            _p2HiPriorityMap.Observe(payload);
        }
    }

    private sealed class P1TelemetryMap
    {
        private readonly Dictionary<byte, P1TelemetryAddressMap> _addresses = new();
        private long _samples;
        private DateTimeOffset? _startedUtc;
        private DateTimeOffset? _lastUpdatedUtc;

        public void Observe(TelemetryReading reading)
        {
            var now = DateTimeOffset.UtcNow;
            _startedUtc ??= now;
            _lastUpdatedUtc = now;
            _samples++;

            byte address = (byte)(reading.C0Address & 0x7E);
            if (!_addresses.TryGetValue(address, out var map))
            {
                map = new P1TelemetryAddressMap(address);
                _addresses[address] = map;
            }
            map.Observe(reading);
        }

        public void Reset()
        {
            _addresses.Clear();
            _samples = 0;
            _startedUtc = null;
            _lastUpdatedUtc = null;
        }

        public object Snapshot() => new
        {
            stream = "p1.cAndCEcho",
            source = "Protocol-1 EP6 C&C echo slots reduced to C0 address + AIN0/AIN1",
            samples = _samples,
            startedUtc = _startedUtc,
            lastUpdatedUtc = _lastUpdatedUtc,
            addresses = _addresses.Values
                .OrderBy(a => a.Address)
                .Select(a => a.Snapshot())
                .ToArray(),
            rawGap = "P1 raw C&C bytes are not exposed yet; this map tracks the reduced TelemetryReading values. Add a raw C&C event when P1 bit-level mapping is needed.",
        };

        public P1TelemetryCapture Capture() => new(
            _samples,
            _lastUpdatedUtc,
            _addresses.Values
                .OrderBy(a => a.Address)
                .Select(a => a.Capture())
                .ToArray());
    }

    private sealed class P1TelemetryAddressMap
    {
        public byte Address { get; }
        private long _samples;
        private byte _lastRawC0;
        private ushort _firstAin0;
        private ushort _firstAin1;
        private ushort _lastAin0;
        private ushort _lastAin1;
        private ushort _minAin0;
        private ushort _minAin1;
        private ushort _maxAin0;
        private ushort _maxAin1;
        private int _ain0ChangeCount;
        private int _ain1ChangeCount;

        public P1TelemetryAddressMap(byte address)
        {
            Address = address;
        }

        public void Observe(TelemetryReading reading)
        {
            if (_samples == 0)
            {
                _firstAin0 = _minAin0 = _maxAin0 = reading.Ain0;
                _firstAin1 = _minAin1 = _maxAin1 = reading.Ain1;
            }
            else
            {
                if (reading.Ain0 != _lastAin0) _ain0ChangeCount++;
                if (reading.Ain1 != _lastAin1) _ain1ChangeCount++;
                if (reading.Ain0 < _minAin0) _minAin0 = reading.Ain0;
                if (reading.Ain1 < _minAin1) _minAin1 = reading.Ain1;
                if (reading.Ain0 > _maxAin0) _maxAin0 = reading.Ain0;
                if (reading.Ain1 > _maxAin1) _maxAin1 = reading.Ain1;
            }

            _samples++;
            _lastRawC0 = reading.C0Address;
            _lastAin0 = reading.Ain0;
            _lastAin1 = reading.Ain1;
        }

        public object Snapshot()
        {
            var known = DescribeP1Address(Address);
            return new
            {
                c0Address = Address,
                hexAddress = $"0x{Address:X2}",
                samples = _samples,
                lastRawC0 = _lastRawC0,
                lastRawC0Hex = $"0x{_lastRawC0:X2}",
                firstAin0 = _firstAin0,
                firstAin1 = _firstAin1,
                lastAin0 = _lastAin0,
                lastAin1 = _lastAin1,
                minAin0 = _minAin0,
                maxAin0 = _maxAin0,
                minAin1 = _minAin1,
                maxAin1 = _maxAin1,
                ain0ChangeCount = _ain0ChangeCount,
                ain1ChangeCount = _ain1ChangeCount,
                knownAin0 = known.ain0,
                knownAin1 = known.ain1,
                notes = known.notes,
            };
        }

        public P1AddressCapture Capture()
        {
            var known = DescribeP1Address(Address);
            return new P1AddressCapture(
                Address,
                known.ain0,
                known.ain1,
                _lastAin0,
                _lastAin1,
                _ain0ChangeCount,
                _ain1ChangeCount);
        }
    }

    private sealed class ByteStreamMap
    {
        private readonly string _stream;
        private readonly string _source;
        private readonly int _defaultLength;
        private readonly IReadOnlyDictionary<int, string> _knownBytes;
        private readonly IReadOnlyDictionary<int, string> _knownWords;
        private byte[] _first;
        private byte[] _last;
        private byte[] _min;
        private byte[] _max;
        private byte[] _changedMask;
        private int[] _changeCounts;
        private long[] _bitSetCounts;
        private long[] _bitChangeCounts;
        private ushort[] _wordFirst;
        private ushort[] _wordLast;
        private ushort[] _wordMin;
        private ushort[] _wordMax;
        private int[] _wordChangeCounts;
        private long _samples;
        private int _length;
        private DateTimeOffset? _startedUtc;
        private DateTimeOffset? _lastUpdatedUtc;

        public ByteStreamMap(
            string stream,
            string source,
            int defaultLength,
            IReadOnlyDictionary<int, string> knownBytes,
            IReadOnlyDictionary<int, string> knownWords)
        {
            _stream = stream;
            _source = source;
            _defaultLength = defaultLength;
            _knownBytes = knownBytes;
            _knownWords = knownWords;
            _first = new byte[defaultLength];
            _last = new byte[defaultLength];
            _min = new byte[defaultLength];
            _max = new byte[defaultLength];
            _changedMask = new byte[defaultLength];
            _changeCounts = new int[defaultLength];
            _bitSetCounts = new long[defaultLength * 8];
            _bitChangeCounts = new long[defaultLength * 8];
            _wordFirst = new ushort[Math.Max(0, defaultLength - 1)];
            _wordLast = new ushort[Math.Max(0, defaultLength - 1)];
            _wordMin = new ushort[Math.Max(0, defaultLength - 1)];
            _wordMax = new ushort[Math.Max(0, defaultLength - 1)];
            _wordChangeCounts = new int[Math.Max(0, defaultLength - 1)];
            Reset();
        }

        public void Reset()
        {
            Array.Clear(_first);
            Array.Clear(_last);
            for (int i = 0; i < _min.Length; i++) _min[i] = byte.MaxValue;
            Array.Clear(_max);
            Array.Clear(_changedMask);
            Array.Clear(_changeCounts);
            Array.Clear(_bitSetCounts);
            Array.Clear(_bitChangeCounts);
            Array.Clear(_wordFirst);
            Array.Clear(_wordLast);
            for (int i = 0; i < _wordMin.Length; i++) _wordMin[i] = ushort.MaxValue;
            Array.Clear(_wordMax);
            Array.Clear(_wordChangeCounts);
            _samples = 0;
            _length = 0;
            _startedUtc = null;
            _lastUpdatedUtc = null;
        }

        public void Observe(ReadOnlySpan<byte> payload)
        {
            if (payload.Length == 0) return;

            EnsureCapacity(payload.Length);
            var now = DateTimeOffset.UtcNow;
            _startedUtc ??= now;
            _lastUpdatedUtc = now;
            _length = Math.Max(_length, payload.Length);

            bool firstSample = _samples == 0;
            for (int i = 0; i < payload.Length; i++)
            {
                byte value = payload[i];
                if (firstSample)
                {
                    _first[i] = value;
                    _min[i] = value;
                    _max[i] = value;
                }
                else
                {
                    byte diff = (byte)(_last[i] ^ value);
                    if (diff != 0)
                    {
                        _changeCounts[i]++;
                        _changedMask[i] |= diff;
                        for (int bit = 0; bit < 8; bit++)
                        {
                            if ((diff & (1 << bit)) != 0)
                                _bitChangeCounts[i * 8 + bit]++;
                        }
                    }
                    if (value < _min[i]) _min[i] = value;
                    if (value > _max[i]) _max[i] = value;
                }

                for (int bit = 0; bit < 8; bit++)
                {
                    if ((value & (1 << bit)) != 0)
                        _bitSetCounts[i * 8 + bit]++;
                }
                _last[i] = value;
            }

            for (int offset = 0; offset < payload.Length - 1; offset++)
            {
                ushort value = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
                if (firstSample)
                {
                    _wordFirst[offset] = value;
                    _wordMin[offset] = value;
                    _wordMax[offset] = value;
                }
                else
                {
                    if (value != _wordLast[offset]) _wordChangeCounts[offset]++;
                    if (value < _wordMin[offset]) _wordMin[offset] = value;
                    if (value > _wordMax[offset]) _wordMax[offset] = value;
                }
                _wordLast[offset] = value;
            }

            _samples++;
        }

        public object Snapshot()
        {
            int length = _length;
            int wordLength = Math.Max(0, length - 1);
            return new
            {
                stream = _stream,
                source = _source,
                samples = _samples,
                startedUtc = _startedUtc,
                lastUpdatedUtc = _lastUpdatedUtc,
                length,
                lastHex = length == 0 ? string.Empty : Convert.ToHexString(_last.AsSpan(0, length)),
                changedByteCount = Enumerable.Range(0, length).Count(i => _changeCounts[i] > 0),
                changedWordCount = Enumerable.Range(0, wordLength).Count(i => _wordChangeCounts[i] > 0),
                bytes = Enumerable.Range(0, length)
                    .Select(i => new
                    {
                        offset = i,
                        hexOffset = $"0x{i:X2}",
                        known = KnownByteLabel(i),
                        first = _first[i],
                        last = _last[i],
                        min = _min[i],
                        max = _max[i],
                        changedMask = _changedMask[i],
                        changedMaskHex = $"0x{_changedMask[i]:X2}",
                        changeCount = _changeCounts[i],
                        bitSetCounts = Enumerable.Range(0, 8).Select(bit => _bitSetCounts[i * 8 + bit]).ToArray(),
                        bitChangeCounts = Enumerable.Range(0, 8).Select(bit => _bitChangeCounts[i * 8 + bit]).ToArray(),
                    })
                    .ToArray(),
                words = Enumerable.Range(0, wordLength)
                    .Select(i => new
                    {
                        offset = i,
                        hexOffset = $"0x{i:X2}",
                        known = _knownWords.TryGetValue(i, out var known) ? known : null,
                        first = _wordFirst[i],
                        last = _wordLast[i],
                        min = _wordMin[i],
                        max = _wordMax[i],
                        changeCount = _wordChangeCounts[i],
                    })
                    .ToArray(),
            };
        }

        public ByteStreamCapture Capture()
        {
            int length = _length;
            int wordLength = Math.Max(0, length - 1);
            var knownBytes = Enumerable.Range(0, length)
                .Select(KnownByteLabel)
                .ToArray();
            var knownWords = Enumerable.Range(0, wordLength)
                .Select(i => _knownWords.TryGetValue(i, out var known) ? known : null)
                .ToArray();
            return new ByteStreamCapture(
                _stream,
                _samples,
                _lastUpdatedUtc,
                length,
                _last.AsSpan(0, length).ToArray(),
                _changeCounts.AsSpan(0, length).ToArray(),
                _changedMask.AsSpan(0, length).ToArray(),
                _bitChangeCounts.AsSpan(0, length * 8).ToArray(),
                knownBytes,
                _wordLast.AsSpan(0, wordLength).ToArray(),
                _wordChangeCounts.AsSpan(0, wordLength).ToArray(),
                knownWords);
        }

        public G2CandidateTelemetryWordDto[] CandidateUnknownWords(int maxCount)
        {
            int wordLength = Math.Max(0, _length - 1);
            if (_samples == 0 || wordLength == 0 || maxCount <= 0) return [];

            return Enumerable.Range(0, wordLength)
                .Where(i => !_knownWords.ContainsKey(i) && _wordChangeCounts[i] > 0)
                .OrderByDescending(i => _wordChangeCounts[i])
                .ThenBy(i => i)
                .Take(maxCount)
                .Select(i => new G2CandidateTelemetryWordDto(
                    Offset: i,
                    HexOffset: $"0x{i:X2}",
                    Known: null,
                    Last: _wordLast[i],
                    Min: _wordMin[i],
                    Max: _wordMax[i],
                    ChangeCount: _wordChangeCounts[i],
                    Status: "unknown-variable",
                    MappingHint: "Create before/after markers around one controlled G2 action and require repeatable direction/range before assigning this word to a current, temperature, or fan sensor."))
                .ToArray();
        }

        private void EnsureCapacity(int length)
        {
            if (_last.Length >= length) return;
            int oldLength = _last.Length;
            int nextLength = Math.Max(length, oldLength * 2);
            Array.Resize(ref _first, nextLength);
            Array.Resize(ref _last, nextLength);
            Array.Resize(ref _min, nextLength);
            Array.Resize(ref _max, nextLength);
            Array.Resize(ref _changedMask, nextLength);
            Array.Resize(ref _changeCounts, nextLength);
            Array.Resize(ref _bitSetCounts, nextLength * 8);
            Array.Resize(ref _bitChangeCounts, nextLength * 8);

            for (int i = oldLength; i < nextLength; i++) _min[i] = byte.MaxValue;

            int oldWordLength = _wordLast.Length;
            int nextWordLength = Math.Max(0, nextLength - 1);
            Array.Resize(ref _wordFirst, nextWordLength);
            Array.Resize(ref _wordLast, nextWordLength);
            Array.Resize(ref _wordMin, nextWordLength);
            Array.Resize(ref _wordMax, nextWordLength);
            Array.Resize(ref _wordChangeCounts, nextWordLength);
            for (int i = oldWordLength; i < nextWordLength; i++) _wordMin[i] = ushort.MaxValue;
        }

        private string? KnownByteLabel(int offset)
        {
            List<string>? labels = null;
            if (_knownBytes.TryGetValue(offset, out var knownByte))
            {
                labels = new List<string> { knownByte };
            }

            foreach (var kvp in _knownWords)
            {
                if (offset == kvp.Key || offset == kvp.Key + 1)
                {
                    labels ??= new List<string>();
                    labels.Add($"{kvp.Value} {(offset == kvp.Key ? "hi" : "lo")}");
                }
            }

            return labels is null ? null : string.Join("; ", labels);
        }
    }

    private static MappingMarkerDelta BuildMarkerDelta(
        MappingMarker? previous,
        P1TelemetryCapture p1,
        ByteStreamCapture p2)
    {
        if (previous is null)
        {
            return new MappingMarkerDelta(
                null,
                null,
                true,
                p1.Samples,
                p2.Samples,
                [],
                [],
                []);
        }

        return new MappingMarkerDelta(
            previous.Id,
            previous.Label,
            false,
            Math.Max(0, p1.Samples - previous.P1.Samples),
            Math.Max(0, p2.Samples - previous.P2.Samples),
            BuildP1Deltas(previous.P1, p1),
            BuildByteDeltas(previous.P2, p2),
            BuildWordDeltas(previous.P2, p2));
    }

    private static P1AddressDelta[] BuildP1Deltas(P1TelemetryCapture previous, P1TelemetryCapture current)
    {
        var prevByAddress = previous.Addresses.ToDictionary(a => a.Address);
        var curByAddress = current.Addresses.ToDictionary(a => a.Address);
        return prevByAddress.Keys
            .Concat(curByAddress.Keys)
            .Distinct()
            .OrderBy(a => a)
            .Select(address =>
            {
                prevByAddress.TryGetValue(address, out var p);
                curByAddress.TryGetValue(address, out var c);
                int ain0Changes = Math.Max(0, (c?.Ain0ChangeCount ?? 0) - (p?.Ain0ChangeCount ?? 0));
                int ain1Changes = Math.Max(0, (c?.Ain1ChangeCount ?? 0) - (p?.Ain1ChangeCount ?? 0));
                if (p?.LastAin0 == c?.LastAin0 &&
                    p?.LastAin1 == c?.LastAin1 &&
                    ain0Changes == 0 &&
                    ain1Changes == 0)
                {
                    return null;
                }

                var known = DescribeP1Address(address);
                int? ain0Delta = p is not null && c is not null ? c.LastAin0 - p.LastAin0 : null;
                int? ain1Delta = p is not null && c is not null ? c.LastAin1 - p.LastAin1 : null;
                return new P1AddressDelta(
                    address,
                    $"0x{address:X2}",
                    c?.KnownAin0 ?? p?.KnownAin0 ?? known.ain0,
                    c?.KnownAin1 ?? p?.KnownAin1 ?? known.ain1,
                    p?.LastAin0,
                    c?.LastAin0,
                    p?.LastAin1,
                    c?.LastAin1,
                    ain0Delta,
                    ain1Delta,
                    ain0Changes,
                    ain1Changes);
            })
            .Where(d => d is not null)
            .Select(d => d!)
            .ToArray();
    }

    private static ByteDelta[] BuildByteDeltas(ByteStreamCapture previous, ByteStreamCapture current)
    {
        int length = Math.Max(previous.Length, current.Length);
        return Enumerable.Range(0, length)
            .Select(offset =>
            {
                byte? prev = offset < previous.Last.Length ? previous.Last[offset] : null;
                byte? cur = offset < current.Last.Length ? current.Last[offset] : null;
                int intervalChanges = Math.Max(0, At(current.ChangeCounts, offset) - At(previous.ChangeCounts, offset));
                var intervalBits = ChangedBitsBetween(previous.BitChangeCounts, current.BitChangeCounts, offset);
                if (prev == cur && intervalChanges == 0 && intervalBits.Length == 0)
                    return null;

                int xor = prev.HasValue && cur.HasValue ? prev.Value ^ cur.Value : 0;
                return new ByteDelta(
                    offset,
                    $"0x{offset:X2}",
                    At(current.KnownBytes, offset) ?? At(previous.KnownBytes, offset),
                    prev,
                    cur,
                    Hex(prev, 2),
                    Hex(cur, 2),
                    xor,
                    $"0x{xor:X2}",
                    intervalChanges,
                    intervalBits);
            })
            .Where(d => d is not null)
            .Select(d => d!)
            .ToArray();
    }

    private static WordDelta[] BuildWordDeltas(ByteStreamCapture previous, ByteStreamCapture current)
    {
        int length = Math.Max(previous.WordLast.Length, current.WordLast.Length);
        return Enumerable.Range(0, length)
            .Select(offset =>
            {
                ushort? prev = offset < previous.WordLast.Length ? previous.WordLast[offset] : null;
                ushort? cur = offset < current.WordLast.Length ? current.WordLast[offset] : null;
                int intervalChanges = Math.Max(0, At(current.WordChangeCounts, offset) - At(previous.WordChangeCounts, offset));
                if (prev == cur && intervalChanges == 0)
                    return null;

                return new WordDelta(
                    offset,
                    $"0x{offset:X2}",
                    At(current.KnownWords, offset) ?? At(previous.KnownWords, offset),
                    prev,
                    cur,
                    Hex(prev, 4),
                    Hex(cur, 4),
                    prev.HasValue && cur.HasValue ? cur.Value - prev.Value : null,
                    intervalChanges);
            })
            .Where(d => d is not null)
            .Select(d => d!)
            .ToArray();
    }

    private static int[] ChangedBitsBetween(long[] previous, long[] current, int byteOffset)
    {
        var bits = new List<int>(8);
        int baseIndex = byteOffset * 8;
        for (int bit = 0; bit < 8; bit++)
        {
            if (At(current, baseIndex + bit) > At(previous, baseIndex + bit))
                bits.Add(bit);
        }
        return bits.ToArray();
    }

    private static int At(int[] values, int index) =>
        index >= 0 && index < values.Length ? values[index] : 0;

    private static long At(long[] values, int index) =>
        index >= 0 && index < values.Length ? values[index] : 0;

    private static string? At(string?[] values, int index) =>
        index >= 0 && index < values.Length ? values[index] : null;

    private static string Hex(byte? value, int width) =>
        value.HasValue ? $"0x{value.Value:X2}".PadLeft(width + 2, '0') : "-";

    private static string Hex(ushort? value, int width) =>
        value.HasValue ? $"0x{value.Value.ToString($"X{width}")}" : "-";

    private static string CleanMarkerText(string? raw, string fallback, int maxLength)
    {
        string value = string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
        value = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record P1TelemetryCapture(
        long Samples,
        DateTimeOffset? LastUpdatedUtc,
        P1AddressCapture[] Addresses);

    private sealed record P1AddressCapture(
        byte Address,
        string? KnownAin0,
        string? KnownAin1,
        ushort LastAin0,
        ushort LastAin1,
        int Ain0ChangeCount,
        int Ain1ChangeCount);

    private sealed record ByteStreamCapture(
        string Stream,
        long Samples,
        DateTimeOffset? LastUpdatedUtc,
        int Length,
        byte[] Last,
        int[] ChangeCounts,
        byte[] ChangedMask,
        long[] BitChangeCounts,
        string?[] KnownBytes,
        ushort[] WordLast,
        int[] WordChangeCounts,
        string?[] KnownWords);

    private sealed record MappingMarker(
        int Id,
        string Label,
        string? Notes,
        DateTimeOffset CreatedUtc,
        string? ActiveProtocol,
        string? Endpoint,
        long P1Packets,
        long P2Packets,
        P1TelemetryCapture P1,
        ByteStreamCapture P2,
        MappingMarkerDelta SincePrevious)
    {
        public object Snapshot() => new
        {
            id = Id,
            label = Label,
            notes = Notes,
            createdUtc = CreatedUtc,
            activeProtocol = ActiveProtocol,
            endpoint = Endpoint,
            p1Packets = P1Packets,
            p2Packets = P2Packets,
            p1Samples = P1.Samples,
            p2Samples = P2.Samples,
            p1LastUpdatedUtc = P1.LastUpdatedUtc,
            p2LastUpdatedUtc = P2.LastUpdatedUtc,
            sincePrevious = SincePrevious,
        };
    }

    private sealed record MappingMarkerDelta(
        int? PreviousId,
        string? PreviousLabel,
        bool Baseline,
        long P1SampleDelta,
        long P2SampleDelta,
        P1AddressDelta[] P1ChangedAddresses,
        ByteDelta[] P2ChangedBytes,
        WordDelta[] P2ChangedWords);

    private sealed record P1AddressDelta(
        byte C0Address,
        string HexAddress,
        string? KnownAin0,
        string? KnownAin1,
        ushort? PreviousAin0,
        ushort? CurrentAin0,
        ushort? PreviousAin1,
        ushort? CurrentAin1,
        int? Ain0Delta,
        int? Ain1Delta,
        int Ain0ChangeCountDelta,
        int Ain1ChangeCountDelta);

    private sealed record ByteDelta(
        int Offset,
        string HexOffset,
        string? Known,
        byte? Previous,
        byte? Current,
        string PreviousHex,
        string CurrentHex,
        int XorMask,
        string XorMaskHex,
        int IntervalChangeCount,
        int[] IntervalChangedBits);

    private sealed record WordDelta(
        int Offset,
        string HexOffset,
        string? Known,
        ushort? Previous,
        ushort? Current,
        string PreviousHex,
        string CurrentHex,
        int? ValueDelta,
        int IntervalChangeCount);

    private static (string? ain0, string? ain1, string notes) DescribeP1Address(byte address) => address switch
    {
        0x08 => ("exciter power / board-specific sensor", "PA forward power", "Thetis networkproto1.c addr 0x08"),
        0x10 => ("PA reverse power", "user ADC0 / MKII PA volts", "Thetis networkproto1.c addr 0x10"),
        0x18 => ("user ADC1 / MKII PA amps", "supply volts / Hermes volts", "Thetis networkproto1.c addr 0x18"),
        0x20 => ("ADC overload bitfield family", "ADC overload bitfield family", "Thetis networkproto1.c addr 0x20 exposes ADC0/1/2 overload bits through C&C bytes"),
        _ => (null, null, "Unknown or firmware-specific C&C echo slot"),
    };

    private static readonly IReadOnlyDictionary<int, string> P2KnownByteFields = new Dictionary<int, string>
    {
        [0] = "flags: PTT bit0, Dot bit1, Dash bit2, PLL bit4, sidetone bit7",
        [1] = "ADC0..7 overload bitmask",
        [55] = "user digital input bitfield: P2 byte 59 after sequence prefix; Thetis maps IO4 bit0 and G2-class TX Disable/IO5 bit1",
    };

    private static readonly IReadOnlyDictionary<int, string> P2KnownWordFields = new Dictionary<int, string>
    {
        [2] = "exciter power ADC",
        [10] = "PA forward power ADC",
        [18] = "PA reverse power ADC",
        [26] = "hardware LEDs",
        [35] = "ADC0 max magnitude",
        [37] = "ADC1 max magnitude",
        [45] = "supply volts ADC",
        [47] = "user ADC3",
        [49] = "user ADC2",
        [51] = "user ADC1",
        [53] = "user ADC0",
    };

    private static readonly object[] ReferenceMap =
    [
        new
        {
            field = "P2 hi-priority status",
            source = "Thetis ChannelMaster/network.c:689-758",
            status = "decoded",
            notes = "PTT, dot, dash, PLL, ADC overloads, FWD/REV/exciter, ADC max magnitude, supply, user ADCs, user digital input, hardware LEDs",
        },
        new
        {
            field = "G2 Dig In TX Disable",
            source = "ANAN G2 manual Dig In connector + Thetis Console.PollTXInhibit/netInterface.c getUserI05_p2",
            status = "decoded-read-only",
            notes = "The G2 manual assigns Dig In tip to CW key and ring to TX Disable. Thetis reads Protocol-2 HPSP 1025 byte 59 bit 1 (Zeus p2.userDigitalIn bit 1 / userDigital1 / IO5) for G2-class TX Inhibit and treats the grounded input as active-low.",
        },
        new
        {
            field = "P1 C&C echo telemetry",
            source = "Thetis ChannelMaster/networkproto1.c:329-355",
            status = "decoded",
            notes = "PTT, ADC overloads, AIN power slots, user ADC0/1, supply volts where firmware provides them",
        },
        new
        {
            field = "Static board capabilities",
            source = "Thetis clsHardwareSpecific.cs + docs/references/protocol-1/thetis-board-matrix.md",
            status = "mapped",
            notes = "RX ADC count, MKII BPF, ADC supply, L/R swap, telemetry presence, audio amp, RX2 attenuation mode, rated watts, and the G2-class 1.536 MHz DDC ceiling",
        },
        new
        {
            field = "G2 manual receiver topology",
            source = "ANAN G2 user manual: block diagram, feature list, and specifications",
            status = "audited",
            notes = "Manual confirms dual phase-synchronous ADCs, independent ADC filter-bank paths, RX2/ADC2 routing, 10 independent DDC receivers, and ADC2 ground-on-TX hardware behavior; Zeus exposes the safe topology facts and live ADC telemetry while leaving unmapped routing controls read-only.",
        },
        new
        {
            field = "PA thermal telemetry",
            source = "ANAN G2 user manual V1.4 thermal sensor/fan guidance + HL2 P1 C&C temperature slot",
            status = "partially-decoded",
            notes = "HL2/P1 PA temperature is decoded from C&C echo slot 0x08 AIN0. The G2 manual confirms temperature sensors and cooling hardware, but the G2/P2 PA-temperature word is still exposed as an explicit unmapped diagnostics gap until captured and verified.",
        },
        new
        {
            field = "G2 current, bias, thermal, and fan sensors",
            source = "ANAN G2 manual V1.4 PA/supply feature list + biascheck utility",
            status = "mapping-required",
            notes = "Manual evidence confirms hardware current/voltage/temperature sensors, thermally compensated bias, internal/external fan support, and biascheck Driver Current / PA Current readings. Zeus exposes mapped P2 power/supply/user I/O fields plus candidate unknown telemetry words, but PA current, driver current, P2 temperature, and fan state remain unassigned until live marker captures prove the offsets.",
        },
        new
        {
            field = "DSP runtime readiness",
            source = "DspPipelineService + WDSP wisdom bootstrap",
            status = "decoded",
            notes = "Active DSP engine, channel/rate, RX sink ownership, TX block geometry, TX monitor state, RX DSP chain state, RXA stage meters, WDSP wisdom/model readiness, and NR native-export capability are exposed in diagnostics.dsp",
        },
    ];

    private static readonly object[] CandidateSettings =
    [
        new
        {
            field = "P2 network/discovery options",
            status = "candidate",
            notes = "Thetis exposes specific-radio/IP, NIC selection, discovery speed, subnet behavior, protocol match, and P2 port notification. Zeus currently auto-discovers broadly; these belong in Settings > Hardware/Network.",
        },
        new
        {
            field = "Board-specific hardware options",
            status = "candidate",
            notes = "Thetis gates Alex/Apollo/MKII/Orion options by model. Zeus should surface only controls backed by BoardCapabilities and persisted per radio; G2 ADC2 ground-on-TX and 10-DDC assignment stay read-only until their protocol/UI mapping is verified. G2 Dig In TX Disable is decoded as read-only diagnostics first; TX blocking must remain opt-in and board-gated.",
        },
        new
        {
            field = "P2 ADC overload/max-magnitude driven auto-attenuation",
            status = "candidate",
            notes = "Thetis records max magnitude at overload. Zeus now exposes the raw values; the next step is a user-configurable P2 auto-ATT strategy.",
        },
        new
        {
            field = "G2 PA sensor mapping workflow",
            status = "candidate",
            notes = "Settings > Hardware now surfaces the mapped/unmapped G2 sensor inventory and candidate variable P2 words. Operator controls for PA current, driver current, P2 thermal inhibit, or fan automation must stay disabled until mapping markers correlate one controlled hardware action at a time.",
        },
    ];

    private static readonly object[] FeatureSurfaces =
    [
        new
        {
            id = "hardware.inventory.discovery",
            title = "Network inventory and radio identity",
            category = "network",
            implementationStatus = "telemetry-ready",
            userConfigurable = true,
            source = "P1/P2 discovery replies",
            telemetryPaths = new[]
            {
                "connectionStatus",
                "endpoint",
                "connectedBoard",
                "effectiveBoard",
                "orionMkIIVariant",
                "capabilities",
            },
            candidateControls = new[]
            {
                "/api/radios",
                "/api/radio/variant",
                "/api/radio/network-profile",
            },
            safetyClass = "rx-safe",
            notes = "Stable board identity, firmware, raw discovery reply, Thetis-derived capability gates, and live transport counters now give clients a direct network profile before DSP quality is judged.",
        },
        new
        {
            id = "hardware.mapping.correlation",
            title = "Wire-field correlation markers",
            category = "diagnostics",
            implementationStatus = "api-ready",
            userConfigurable = false,
            source = "diagnostics accumulator",
            telemetryPaths = new[]
            {
                "mapping.p2HiPriority.bytes",
                "mapping.p2HiPriority.words",
                "mapping.p1.addresses",
                "mapping.markers",
            },
            candidateControls = new[]
            {
                "/api/radio/diagnostics",
                "/api/radio/diagnostics/map/reset",
                "/api/radio/diagnostics/map/marker",
            },
            safetyClass = "rx-safe",
            notes = "Machine-readable before/after deltas let new hardware actions be mapped by labeling one controlled action at a time.",
        },
        new
        {
            id = "rx.g2.receiver-topology",
            title = "G2 receiver ADC topology and wide DDC capacity",
            category = "rx-hardware",
            implementationStatus = "telemetry-ready",
            userConfigurable = true,
            source = "ANAN G2 manual block diagram/specifications + BoardCapabilities + P2 hi-priority ADC telemetry",
            telemetryPaths = new[]
            {
                "connectedBoard",
                "effectiveBoard",
                "orionMkIIVariant",
                "capabilities.rxAdcCount",
                "capabilities.mkiiBpf",
                "capabilities.hasSteppedAttenuationRx2",
                "capabilities.maxRxSampleRateHz",
                "p2.adcOverloadBits",
                "p2.adc0MaxMagnitude",
                "p2.adc1MaxMagnitude",
                "p2.adc0MaxMagnitudeAtOverload",
                "p2.adc1MaxMagnitudeAtOverload",
            },
            candidateControls = new[]
            {
                "/api/radio/diagnostics",
                "/api/radio/capabilities",
                "/api/radio/network-profile",
                "Settings > Hardware > Receiver Topology",
            },
            safetyClass = "rx-safe",
            notes = "The G2/Saturn manual topology is now visible as safe diagnostics: dual phase-synchronous ADCs, independent MKII/preselector filter-bank paths, RX2 stepped attenuation, and the 48 kHz..1.536 MHz P2 DDC ceiling. The manual's 10 independent DDC receivers and ADC2 ground-on-TX behavior remain explicitly marked as not exposed operator controls until protocol mapping and live marker captures prove the safe control surface.",
        },
        new
        {
            id = "rx.auto-attenuation.adc-overload",
            title = "ADC overload auto-attenuation",
            category = "rx-protection",
            implementationStatus = "control-ready",
            userConfigurable = true,
            source = "Thetis P2 hi-priority status byte 1 + max-magnitude words",
            telemetryPaths = new[]
            {
                "p2.adcOverloadBits",
                "p2.adc0MaxMagnitude",
                "p2.adc1MaxMagnitude",
                "p2.adc0MaxMagnitudeAtOverload",
                "p2.adc1MaxMagnitudeAtOverload",
            },
            candidateControls = new[]
            {
                "/api/auto-att",
                "/api/attenuator",
                "/api/rx/adc-protection",
            },
            safetyClass = "rx-safe",
            notes = "Orion/G2 overload and magnitude telemetry now drive the configurable ADC protection endpoint with fast attack, slow release, offset caps, and P2 magnitude soft limits.",
        },
        new
        {
            id = "rx.signal-intelligence.weak-signal",
            title = "RX weak-signal display intelligence",
            category = "rx-dsp",
            implementationStatus = "control-ready",
            userConfigurable = true,
            source = "Panadapter CFAR floor, temporal confidence, Signal Pop, Snap-to, and auto scene profiles",
            telemetryPaths = new[]
            {
                "dsp.engineKind",
                "dsp.sampleRateHz",
                "dsp.rxDsp.status",
                "dsp.rxDsp.activeFeatures",
                "dsp.rxDsp.qualityReasons",
                "dsp.rxMeters.status",
                "dsp.rxMeters.signalPkDbm",
                "dsp.rxMeters.adcPkDbfs",
                "dsp.rxMeters.agcGainDb",
                "dsp.display.status",
                "dsp.display.panSource",
                "dsp.display.waterfallSource",
                "dsp.display.pan.maxDb",
                "dsp.display.waterfall.dynamicRangeDb",
                "frontendDspScene.status",
                "frontendDspScene.signalProfile",
                "frontendDspScene.signalReason",
                "frontendDspScene.coherentMaxSnrDb",
                "frontend.signalEnhance.sceneStatus",
                "frontend.signalEstimator.noiseFloor",
                "frontend.signalEstimator.signalConfidence",
            },
            candidateControls = new[]
            {
                "Settings > DSP > Signal Intelligence",
                "/api/dsp/display-intelligence",
                "/api/radio/diagnostics/dsp-scene",
            },
            safetyClass = "rx-safe",
            notes = "Existing RX display logic now has floor-normalized Signal Pop, snap-to-carrier, temporal confidence, and coherent auto-profile classification; the display-intelligence API persists the active weak-signal display policy while diagnostics mirror live frontend scene evidence.",
        },
        new
        {
            id = "rx.smart-nr.adaptive",
            title = "Smart NR adaptive condition logic",
            category = "rx-dsp",
            implementationStatus = "control-ready",
            userConfigurable = true,
            source = "Frontend condition analyzer plus WDSP NR/NB/ANF/SNB controls",
            telemetryPaths = new[]
            {
                "dsp.engineKind",
                "frontendDspScene.status",
                "frontendDspScene.smartNrProfile",
                "frontendDspScene.smartNrRecommendation",
                "frontendDspScene.smartNrHeldByRxChain",
                "frontendDspScene.coherentSubthresholdSignal",
                "frontend.smartNr.status",
                "frontend.signalEstimator.noiseFloor",
                "frontend.signalEstimator.signalConfidence",
                "dsp.requestedNrMode",
                "dsp.effectiveNrMode",
                "dsp.rxDsp.status",
                "dsp.rxDsp.anfEnabled",
                "dsp.rxDsp.snbEnabled",
                "dsp.rxDsp.nbMode",
                "dsp.rxDsp.effectiveNbpNotchesRun",
                "dsp.rxDsp.activeManualNotchCount",
                "dsp.rxDsp.appliedNrMatchesRequested",
                "dsp.rxMeters.status",
                "dsp.rxMeters.adcHeadroomDb",
                "dsp.rxMeters.agcGainDb",
                "dsp.rxMeters.signalUsable",
                "dsp.audio.status",
                "dsp.audio.source",
                "dsp.audio.rmsDbfs",
                "dsp.audio.peakDbfs",
                "dsp.audio.squelchOpen",
                "dsp.audio.txMonitorRequested",
                "dsp.audio.monitorBacklogSamples",
                "dsp.wdspWisdomPhase",
                "dsp.wdspEmnrPost2Available",
                "dsp.wdspNr4SbnrAvailable",
                "dsp.nr4Readiness",
                "/api/dsp/nr-condition.rxChain.agcTopDb",
                "/api/dsp/nr-condition.rxChain.agcOffsetDb",
                "/api/dsp/nr-condition.rxChain.effectiveAgcTopDb",
                "/api/dsp/nr-condition.rxChain.effectiveAttenDb",
                "/api/dsp/nr-condition.rxChain.adcOverloadWarning",
                "/api/dsp/nr-condition.rxChain.squelchEnabled",
            },
            candidateControls = new[]
            {
                "Settings > DSP > Smart NR Automation",
                "/api/nr-ui-prefs",
                "/api/radio/diagnostics",
                "/api/radio/diagnostics/dsp-scene",
                "/api/dsp/nr-condition",
            },
            safetyClass = "rx-safe",
            notes = "Smart NR already separates weak sparse signals, tonal interference, dense noise, and impulsive artifacts; the direct NR-condition API and backend diagnostics feed preserve the active profile, recommendation, RX-chain hold reason, requested/effective NR mode, ANF/SNB/NB/manual-notch runtime state, WDSP NR2/NR4 native capability, backend AGC/ATT/ADC/squelch operating point, and final RX/TX-monitor audio-frame freshness/RMS/peak evidence for remote clients and recordings.",
        },
        new
        {
            id = "pa.telemetry.power-supply",
            title = "PA and supply telemetry",
            category = "power",
            implementationStatus = "telemetry-ready",
            userConfigurable = true,
            source = "Thetis P1 C&C slots and P2 hi-priority words",
            telemetryPaths = new[]
            {
                "p1.exciterAdc",
                "p1.fwdAdc",
                "p1.revAdc",
                "p1.supplyVoltsAdc",
                "p2.exciterAdc",
                "p2.fwdAdc",
                "p2.revAdc",
                "p2.supplyVoltsAdc",
                "/api/radio/pa-thermal.status",
                "/api/radio/pa-thermal.tempC",
                "/api/radio/pa-thermal.ageMs",
                "g2Sensors.status",
                "g2Sensors.unmappedManualSensors",
            },
            candidateControls = new[]
            {
                "/api/radio/power-calibration",
                "/api/radio/supply-alarms",
                "/api/radio/pa-thermal",
                "/api/radio/g2-sensors",
                "/api/pa-settings",
            },
            safetyClass = "tx-monitoring-only",
            notes = "Raw P1/P2 power ADC telemetry now has a direct calibrated snapshot that reuses the live TX meter math; supply-voltage ADCs are scaled from board capabilities and exposed as advisory alarm evidence until per-radio high/low thresholds are configured. PA thermal diagnostics decode the existing HL2/P1 temperature path and explicitly flag the G2/P2 temperature word as unmapped, matching the G2 manual's sensor/fan hardware without inventing an unverified value.",
        },
        new
        {
            id = "pa.g2.sensor-mapping",
            title = "G2 current, thermal, and fan sensor mapping",
            category = "power",
            implementationStatus = "mapping-ready",
            userConfigurable = true,
            source = "ANAN G2 manual V1.4 + P2 hi-priority marker correlation",
            telemetryPaths = new[]
            {
                "g2Sensors.status",
                "g2Sensors.mappedSensors",
                "g2Sensors.unmappedManualSensors",
                "g2Sensors.candidateWords",
                "mapping.p2HiPriority.words",
                "mapping.markers",
            },
            candidateControls = new[]
            {
                "/api/radio/g2-sensors",
                "/api/radio/diagnostics",
                "/api/radio/diagnostics/map/reset",
                "/api/radio/diagnostics/map/marker",
                "Settings > Hardware > G2 Sensor Mapping",
            },
            safetyClass = "tx-monitoring-only",
            notes = "The G2 manual documents PA current, driver current, temperature sensors, thermally compensated bias, and fan support. Zeus now lists mapped P2 power/supply/user-I/O fields and the remaining unmapped manual sensors, with live unknown-word candidates from the P2 hi-priority map so controlled marker captures can prove the offsets before any protection automation is armed.",
        },
        new
        {
            id = "tx.fidelity.spectral-density",
            title = "TX fidelity and spectral-density advisor",
            category = "tx-audio",
            implementationStatus = "control-ready",
            userConfigurable = true,
            source = "WDSP TX stage meters, TX monitor, PureSignal feedback, Audio Suite chain meters, and IMD tools",
            telemetryPaths = new[]
            {
                "dsp.txBlockSamples",
                "dsp.txOutputSamples",
                "dsp.txMonitorRequested",
                "tx.audioPath.status",
                "tx.audioPath.ringFillPct",
                "tx.audioPath.ringDropRatioPct",
                "tx.audioPath.p2DucLive",
                "tx.audioPath.p2InputComplexSamples",
                "tx.audioPath.p2PacketsSent",
                "tx.egress.qualityReasons",
                "pureSignal.feedbackSource",
                "pureSignal.healthStatus",
                "pureSignal.feedbackLevelRaw",
                "pureSignal.txFeedbackAttenuationDb",
                "pureSignal.rfBypassSelected",
                "/api/tx/diag",
                "/api/audio-suite/chain/meters",
            },
            candidateControls = new[]
            {
                "/api/audio-suite/processing-mode",
                "/api/audio-suite/chain/meters",
                "/api/tx/fidelity-policy",
                "/api/tx/station-profiles",
                "/api/tx/ps/feedback-source",
                "/api/tx/ps/feedback-attenuation",
                "/api/tx/ps/monitor",
            },
            safetyClass = "tx-monitoring-only",
            notes = "The existing TX advisor can score mic/leveler/ALC/CFC/output/PureSignal health; /api/tx/diag now separates standby P1 compatibility-ring pressure from active P2 DUC egress evidence before judging station-quality audio.",
        },
        new
        {
            id = "hardware.user-io",
            title = "User analog and digital IO",
            category = "accessory-io",
            implementationStatus = "telemetry-ready",
            userConfigurable = true,
            source = "Thetis P2 hi-priority status bytes 47..55",
            telemetryPaths = new[]
            {
                "p2.userAdc0",
                "p2.userAdc1",
                "p2.userAdc2",
                "p2.userAdc3",
                "p2.userDigitalIn",
                "digIn.txDisableLineId",
                "digIn.txDisableActive",
                "digIn.txDisableMappingStatus",
                "digIn.cwKeyTipDown",
            },
            candidateControls = new[]
            {
                "/api/radio/user-io/labels",
                "/api/radio/user-io/actions",
                "/api/radio/dig-in",
            },
            safetyClass = "rx-safe",
            notes = "P2 user ADC and digital input lines now have direct read-only labels, action-readiness snapshots, and a G2 Dig In TX Disable view. The TX Disable ring contact is decoded from the Thetis P2 IO5 mapping but station automation and TX blocking remain unarmed until a hardware-inhibit policy is explicitly enabled.",
        },
        new
        {
            id = "hardware.front-panel.leds",
            title = "Hardware LED/front-panel state",
            category = "front-panel",
            implementationStatus = "telemetry-ready",
            userConfigurable = false,
            source = "Thetis P2 hi-priority status word 26",
            telemetryPaths = new[]
            {
                "p2.hardwareLeds",
                "mapping.p2HiPriority.words[0x1A]",
            },
            candidateControls = Array.Empty<string>(),
            safetyClass = "rx-safe",
            notes = "Read-only mirror of hardware/front-panel state; useful for remote diagnostics and UI state reconciliation.",
        },
        new
        {
            id = "cw.hardware-keying",
            title = "Hardware PTT, dot, dash, and sidetone",
            category = "keying",
            implementationStatus = "telemetry-ready",
            userConfigurable = true,
            source = "Thetis P1 key/PTT events and P2 hi-priority byte 0",
            telemetryPaths = new[]
            {
                "p1.hardwarePtt",
                "p1.cwKeyDown",
                "p2.pttIn",
                "p2.dotIn",
                "p2.dashIn",
                "p2.sidetoneActive",
            },
            candidateControls = new[]
            {
                "/api/cw/hardware-keying",
                "/api/tx/external-ptt",
            },
            safetyClass = "tx-capable-requires-confirmation",
            notes = "The telemetry is decoded and exposed through read-only keying/PTT status APIs; any future write controls must remain explicitly armed because they can key TX.",
        },
    ];
}
