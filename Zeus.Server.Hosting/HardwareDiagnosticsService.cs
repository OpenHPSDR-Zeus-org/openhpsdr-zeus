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

/// <summary>
/// Read-only hardware diagnostic accumulator. Mirrors the live fields Thetis
/// keeps in ChannelMaster network.c/networkproto1.c, but exposes them as a
/// safe REST snapshot instead of turning them directly into operator controls.
/// </summary>
public sealed class HardwareDiagnosticsService : IHostedService, IDisposable
{
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
                caps.AdcSupplyMv);
            var p2 = BuildSupplyReading(
                _p2Packets,
                _p2LastUpdatedUtc,
                _p2HadSample ? _p2Last.SupplyVoltsAdc : (ushort?)null,
                caps.AdcSupplyMv);
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

    private static RadioSupplyReadingDto BuildSupplyReading(
        long packets,
        DateTimeOffset? lastUpdatedUtc,
        ushort? supplyVoltsAdc,
        int adcSupplyMv)
    {
        var volts = supplyVoltsAdc is { } adc && adcSupplyMv > 0
            ? Round(adc * adcSupplyMv / 1000.0, 2)
            : (double?)null;
        return new(
            Packets: packets,
            LastUpdatedUtc: lastUpdatedUtc,
            SupplyVoltsAdc: supplyVoltsAdc,
            SupplyVolts: volts);
    }

    private static (bool AlarmActive, string AlarmStatus, string Recommendation)
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

        if (p1.SupplyVolts is null && p2.SupplyVolts is null)
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

        return (
            false,
            "telemetry-ready",
            "Live supply voltage is decoded and scaled; add per-radio high/low thresholds before using this diagnostic surface to inhibit TX.");
    }

    private static bool IsInvalidSupply(RadioSupplyReadingDto reading) =>
        reading.SupplyVolts is { } volts && volts <= 0.0;

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
        [55] = "user digital input bitfield",
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
            notes = "RX ADC count, MKII BPF, ADC supply, L/R swap, telemetry presence, audio amp, RX2 attenuation mode, rated watts",
        },
        new
        {
            field = "DSP runtime readiness",
            source = "DspPipelineService + WDSP wisdom bootstrap",
            status = "decoded",
            notes = "Active DSP engine, channel/rate, RX sink ownership, TX block geometry, TX monitor state, WDSP wisdom/model readiness, and NR native-export capability are exposed in diagnostics.dsp",
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
            notes = "Thetis gates Alex/Apollo/MKII/Orion options by model. Zeus should surface only controls backed by BoardCapabilities and persisted per radio.",
        },
        new
        {
            field = "P2 ADC overload/max-magnitude driven auto-attenuation",
            status = "candidate",
            notes = "Thetis records max magnitude at overload. Zeus now exposes the raw values; the next step is a user-configurable P2 auto-ATT strategy.",
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
                "dsp.wdspWisdomPhase",
                "dsp.wdspEmnrPost2Available",
                "dsp.wdspNr4SbnrAvailable",
                "dsp.nr4Readiness",
            },
            candidateControls = new[]
            {
                "Settings > DSP > Smart NR Automation",
                "/api/nr-ui-prefs",
                "/api/dsp/nr-condition",
            },
            safetyClass = "rx-safe",
            notes = "Smart NR already separates weak sparse signals, tonal interference, dense noise, and impulsive artifacts; the direct NR-condition API and backend diagnostics feed preserve the active profile, recommendation, RX-chain hold reason, requested/effective NR mode, and WDSP NR2/NR4 native capability for remote clients and recordings.",
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
            },
            candidateControls = new[]
            {
                "/api/radio/power-calibration",
                "/api/radio/supply-alarms",
                "/api/pa-settings",
            },
            safetyClass = "tx-monitoring-only",
            notes = "Raw P1/P2 power ADC telemetry now has a direct calibrated snapshot that reuses the live TX meter math; supply-voltage ADCs are scaled from board capabilities and exposed as advisory alarm evidence until per-radio high/low thresholds are configured.",
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
                "/api/tx/diag",
                "/api/audio-suite/chain/meters",
            },
            candidateControls = new[]
            {
                "/api/audio-suite/processing-mode",
                "/api/audio-suite/chain/meters",
                "/api/tx/fidelity-policy",
                "/api/tx/station-profiles",
            },
            safetyClass = "tx-monitoring-only",
            notes = "The existing TX advisor can score mic/leveler/ALC/CFC/output/PureSignal health; the TX fidelity policy now persists the active station target while diagnostics prove the high-rate TX path before judging station-quality audio.",
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
            },
            candidateControls = new[]
            {
                "planned:/api/radio/user-io/labels",
                "planned:/api/radio/user-io/actions",
            },
            safetyClass = "rx-safe",
            notes = "Expose configurable labels, thresholds, and action bindings for Orion/G2 external IO once physical lines are correlated.",
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
