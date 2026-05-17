// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

/// <summary>
/// Consumes raw FWD/REF ADC readings from Protocol1, smooths them with an
/// exponential low-pass, converts to watts + SWR per the HermesLite2
/// calibration, and broadcasts a <see cref="TxMetersV2Frame"/> over the
/// StreamingHub at 10 Hz while MOX is on / 2 Hz when idle.
///
/// PRD FR-6: If SWR > 2.5 sustained for ≥500 ms while MOX or TUN is on,
/// auto-drop MOX/TUN and emit an AlertFrame. Protects the HL2 finals if
/// the antenna goes out of match mid-transmission.
///
/// Math provenance: Thetis <c>console.cs:25008-25072</c> (watts) and
/// <c>console.cs:25972-25978</c> (SWR). Smoothing α matches
/// <c>console.cs:25011,25931</c>.
/// </summary>
public sealed class TxMetersService : BackgroundService
{
    // Thetis uses a 90/10 split on the raw ADC (console.cs:25011).
    private const double SmoothAlpha = 0.90;

    /// <summary>
    /// Raised when TX meter values are updated (approximately 10 Hz during MOX).
    /// Arguments: (fwdWatts, refWatts, swr, alcPk, alcGr)
    /// </summary>
    public event Action<float, float, float, float, float>? TxMetersUpdated;

    // Wire FWD ≤ 2 W as a floor for SWR; below the bridge noise dominates
    // and the ratio is meaningless (Thetis does the same in console.cs:25974).
    private const double SwrMinFwdWatts = 2.0;
    private const double SwrMax = 9.0;

    // Per-mode SWR trip thresholds and sustain windows. MOX keeps the
    // conservative PRD FR-6 default (2.5:1 sustained 500 ms). TUN is
    // relaxed to 6:1 because TUN's whole purpose is to drive a carrier
    // into a not-yet-matched load so an ATU / operator can find a
    // match — tripping at 2.5:1 on TUN makes TUN useless. Values follow
    // Thetis / piHPSDR convention. See issue #362.
    private const double SwrTripThresholdMox = 2.5;
    private const double SwrTripThresholdTun = 6.0;
    private static readonly TimeSpan SwrTripDurationMox = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SwrTripDurationTun = TimeSpan.FromMilliseconds(500);

    // Startup grace after the keying edge: suppress SWR trip evaluation
    // during the PA-bias / LPF-relay settle window where the bridge can
    // transiently read above threshold before the actual antenna match
    // is engaged. Per-mode so MOX still trips quickly on a genuine bad
    // match while TUN gives the ATU time to do its work.
    private static readonly TimeSpan SwrStartupGraceMox = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SwrStartupGraceTun = TimeSpan.FromMilliseconds(500);

    // PRD FR-6: a single MOX/TUN transmission may not exceed 120 s. Catches
    // stuck spacebars, jammed buttons, or a client that forgot to unkey. The
    // constant is internal-settable so a test can shorten the window without
    // driving a 2-minute wall-clock delay.
    internal static readonly TimeSpan DefaultTxTimeout = TimeSpan.FromSeconds(120);
    internal TimeSpan TxTimeout { get; set; } = DefaultTxTimeout;

    private static readonly TimeSpan MoxTick = TimeSpan.FromMilliseconds(100); // 10 Hz
    private static readonly TimeSpan IdleTick = TimeSpan.FromMilliseconds(500); // 2 Hz

    // PA temperature broadcast cadence: 2 Hz regardless of MOX. Temperature
    // is a protection signal (HL2 auto-disables TX at 55 °C) and moves on
    // a seconds timescale, so piggybacking on the 10 Hz MOX tick would be
    // wasted wire. When MOX is off the outer loop already ticks at 500 ms;
    // when MOX is on the outer loop ticks at 100 ms and we throttle the
    // PA broadcast with a last-sent timestamp.
    private static readonly TimeSpan PaTempTick = TimeSpan.FromMilliseconds(500);

    // MCP9700 / TMP36-style sensor on the HL2 Q6 position. Datasheet:
    // V_out = 500 mV + 10 mV/°C * T; ADC is 12-bit against a 3.26 V ref.
    // Derived: tempC = (3.26 * raw / 4096 - 0.5) * 100. See Steve Haynal's
    // hermes-lite wiki for the board-level mapping and reference voltage.
    private const double PaTempAdcRefVolts = 3.26;
    private const int PaTempAdcFullScale = 4096;
    private const double PaTempSensorOffsetVolts = 0.5;
    private const double PaTempSensorVoltsPerDegC = 0.01;

    // Clamp range for the conversion output. Below this the sensor is
    // either unplugged or reading noise floor; above this the reading is
    // well beyond the HL2 gateware's 55 °C shutdown. Broadcasting a
    // clamped value keeps the UI from flashing red during boot while a
    // floating ADC settles.
    private const float PaTempMinC = -40f;
    private const float PaTempMaxC = 125f;

    private readonly StreamingHub _hub;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipe;
    private readonly ILogger<TxMetersService> _log;
    // Per-board calibration is resolved at sample time via
    // RadioCalibrations.For(_radio.ConnectedBoardKind). Mirrors the
    // PaDefaults.GetPaGainDb dispatch seam (per CLAUDE.md, do not
    // special-case inside ComputeMeters). See RadioCalibrations for the
    // dispatch table and the OrionMkII / ANAN-8000D caveat.

    private readonly object _sync = new();
    private double _fwdAdc;
    private double _refAdc;
    // Peak-hold ADC tracking between publish ticks. The radio sends
    // hi-priority status at hundreds of Hz on P2 (G2 MkII observed at ~820
    // pkts/s) but we publish to the UI at only 10 Hz; voice peaks lasting
    // 100-200 ms fall between publish ticks and the bar reads the
    // inter-syllable average (~3× low vs an analog peak-reading wattmeter
    // like an LP-100A). Tracking max ADC seen since last publish gives the
    // UI a meter that catches transients the way a hardware peak-reading
    // wattmeter does. Reset to 0 every publish tick.
    private ushort _fwdAdcPeak;
    private ushort _refAdcPeak;
    // PA temperature smoothed ADC and first-sample flag — separate from
    // _seenSample because temperature arrives on a different slot and may
    // show up before / after the FWD-REF pair on any given packet.
    private double _paTempAdc;
    private bool _seenPaTempSample;
    private bool _seenSample;
    // SWR trip state: timestamp when SWR first exceeded threshold, or null if
    // SWR is currently below threshold. Checked on every meter tick (100 ms).
    private DateTime? _swrAboveThresholdSince;
    // Last time a PaTempFrame was broadcast, so the 10 Hz MOX loop can
    // throttle itself down to the 2 Hz PA cadence without a separate timer.
    private DateTime _lastPaTempBroadcastAtUtc = DateTime.MinValue;

    // Diagnostic counters for the 1 Hz tx.meters.diag log emitted while
    // MOX/TUN is on. Bumped under _sync from OnTelemetry / OnTelemetryRaw /
    // ApplyPaTempSmoothed; reset under _sync after each emit. The "last"
    // values capture the most recent raw sample on each axis so a quiet
    // tick (count=0) still shows the last value the radio sent.
    private uint _diagFwdSlotCount;
    private uint _diagRefSlotCount;
    private uint _diagPaTempSlotCount;
    private ushort _diagLastFwdAin1;
    private ushort _diagLastFwdAin0;
    private ushort _diagLastRefAin0;
    private DateTime _lastDiagLogAtUtc = DateTime.MinValue;

    public TxMetersService(StreamingHub hub, RadioService radio, TxService tx, DspPipelineService pipe, ILogger<TxMetersService> log)
    {
        _hub = hub;
        _radio = radio;
        _tx = tx;
        _pipe = pipe;
        _log = log;
        // Bind to the radio's connection lifecycle so we subscribe to telemetry
        // on every fresh Protocol1Client instance. The event is one-way
        // (Protocol1 → Server) and carries only AIN readings.
        radio.Connected += OnConnected;
        radio.Disconnected += OnDisconnected;
        // Mirror the same subscribe/detach dance for Protocol-2 so
        // hi-priority status (UDP 1025) feeds the same FWD/REF smoothing
        // path as P1 alex telemetry. Issue #174 — without this hook, a
        // G2 / ANAN-class radio's TX power meter sits at zero.
        radio.P2Connected += OnP2Connected;
        radio.P2Disconnected += OnP2Disconnected;
    }

    // Holds the last subscribed client so OnDisconnected can detach the
    // TelemetryReceived handler once the Protocol1 surface lands.
    private Zeus.Protocol1.IProtocol1Client? _subscribedClient;
    // Same idea, P2 side. Tracked separately so a P1 disconnect doesn't
    // accidentally detach a P2 handler (the two protocols can't be live at
    // the same time, but the events are independent and this keeps the
    // coupling clean).
    private Zeus.Protocol2.Protocol2Client? _subscribedP2Client;

    // HL2 C&C-echo addresses that carry the alex FWD/REF ADCs and the PA
    // temperature (see TelemetryReading docs in Zeus.Protocol1).
    // addr=1 (C0=0x08): Ain0 = HL2 PA temperature;   Ain1 = alex_forward_power
    // addr=2 (C0=0x10): Ain0 = alex_reverse_power;   Ain1 = ADC0 bias
    // Match on bits 4:1 only — C0[0] is the PTT/MOX echo (so a live TX packet
    // arrives as 0x09/0x11), and C0[7] is the HL2 IOB ACK
    // marker which PacketParser already filters out via the addr==1|2|3 gate.
    private const byte C0AddrMask = 0x7E;
    private const byte C0AddrAlexFwd = 0x08;
    private const byte C0AddrAlexRef = 0x10;

    /// <summary>
    /// Entry point for telemetry consumers. FWD+temperature share the 0x08
    /// slot (Ain1 / Ain0 respectively); REF arrives on 0x10 (Ain0). One
    /// <see cref="Zeus.Protocol1.TelemetryReading"/> may update multiple axes
    /// on the 0x08 slot (FWD + temperature) but never more than one slot's
    /// worth per call — packets carry the echo-slot map, not a combined blob.
    /// </summary>
    public void OnTelemetry(Zeus.Protocol1.TelemetryReading reading)
    {
        switch (reading.C0Address & C0AddrMask)
        {
            case C0AddrAlexFwd:
                ApplySmoothed(ref _fwdAdc, reading.Ain1);
                TrackPeak(ref _fwdAdcPeak, reading.Ain1);
                // Ain0 on this slot is the HL2 Q6 temperature ADC. Smooth
                // with the same α as FWD/REF so the UI sees a stable reading
                // instead of ADC jitter.
                ApplyPaTempSmoothed(reading.Ain0);
                lock (_sync)
                {
                    _diagFwdSlotCount++;
                    _diagPaTempSlotCount++;
                    _diagLastFwdAin1 = reading.Ain1;
                    _diagLastFwdAin0 = reading.Ain0;
                }
                break;
            case C0AddrAlexRef:
                ApplySmoothed(ref _refAdc, reading.Ain0);
                TrackPeak(ref _refAdcPeak, reading.Ain0);
                lock (_sync)
                {
                    _diagRefSlotCount++;
                    _diagLastRefAin0 = reading.Ain0;
                }
                break;
            default:
                // Other echo slots (ADC bias, exciter/temp) aren't part of the
                // FWD/REF meter pair. Silently ignored — protection/alerts
                // are a later slice.
                break;
        }
    }

    // Overload kept for unit tests that want to drive both axes simultaneously
    // without constructing two TelemetryReading structs. Also the P2 ingress
    // point — both axes arrive in the same hi-priority status packet.
    internal void OnTelemetryRaw(ushort fwdAdc, ushort refAdc)
    {
        ApplySmoothed(ref _fwdAdc, fwdAdc);
        ApplySmoothed(ref _refAdc, refAdc);
        TrackPeak(ref _fwdAdcPeak, fwdAdc);
        TrackPeak(ref _refAdcPeak, refAdc);
        lock (_sync)
        {
            _diagFwdSlotCount++;
            _diagRefSlotCount++;
            _diagLastFwdAin1 = fwdAdc;
            _diagLastRefAin0 = refAdc;
        }
    }

    // Hold the highest raw ADC seen since the last publish-tick reset. Called
    // on every incoming telemetry sample. Lock-free against the publish reader
    // because the writer side runs only on the radio RX thread; cross-thread
    // visibility is taken care of by the _sync lock used at publish time.
    private void TrackPeak(ref ushort state, ushort raw)
    {
        if (raw > state) state = raw;
    }

    private void ApplySmoothed(ref double state, ushort raw)
    {
        lock (_sync)
        {
            if (!_seenSample)
            {
                // First-sample fast path matches Thetis console.cs:25011 — seed
                // both axes so the UI doesn't ramp up from zero across the
                // ~2 s alpha=0.90 settling time.
                _fwdAdc = raw;
                _refAdc = raw;
                state = raw;
                _seenSample = true;
                return;
            }
            state = SmoothAlpha * state + (1.0 - SmoothAlpha) * raw;
        }
    }

    // Same α as FWD/REF (0.90 / 0.10) so the temperature reading settles at
    // the same timescale the operator is already used to for protection
    // signals. Tracked separately from _seenSample because the temperature
    // arrives on the same slot as FWD but via a different AIN pair; seeding
    // it on the first sample avoids a ~2 s ramp from zero.
    internal void ApplyPaTempSmoothed(ushort raw)
    {
        lock (_sync)
        {
            if (!_seenPaTempSample)
            {
                _paTempAdc = raw;
                _seenPaTempSample = true;
                return;
            }
            _paTempAdc = SmoothAlpha * _paTempAdc + (1.0 - SmoothAlpha) * raw;
        }
    }

    /// <summary>
    /// Convert a raw HL2 Q6-sensor ADC reading to °C, clamped into the
    /// plausible physical range so a floating ADC or disconnected sensor
    /// can't trip the UI's 55 °C red zone at boot. Pure function — exposed
    /// <c>internal</c> for unit tests. Formula derivation:
    /// MCP9700-class sensor, V_out = 500 mV + 10 mV/°C · T, 12-bit ADC
    /// against a 3.26 V reference; see the hermes-lite wiki / Steve
    /// Haynal's HL2 docs for the board-level mapping.
    /// </summary>
    internal static float ConvertPaTempAdcToCelsius(double rawAdc)
    {
        double volts = rawAdc * PaTempAdcRefVolts / PaTempAdcFullScale;
        double tempC = (volts - PaTempSensorOffsetVolts) / PaTempSensorVoltsPerDegC;
        if (tempC < PaTempMinC) tempC = PaTempMinC;
        if (tempC > PaTempMaxC) tempC = PaTempMaxC;
        return (float)tempC;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // TX timeout guard: PRD FR-6 caps a single TX at 120 s to catch
                // stuck spacebar / jammed PTT. TxService hands off the trip via
                // the same AlertFrame path as SWR so the client only needs one
                // protection-event listener.
                if (EvaluateTimeoutTrip(DateTime.UtcNow) is { } timeoutReason)
                {
                    _tx.TryTripForAlert(AlertKind.TxTimeout, timeoutReason);
                }

                // Meter during MOX *or* TUN — both drive the PA, both need live
                // FWD/SWR readouts. Idle frame is only for fully-unkeyed RX.
                bool mox = _tx.IsMoxOn || _tx.IsTunOn;
                TxMetersV2Frame frame;
                double swr = 1.0;
                if (mox)
                {
                    double fwdAdc, refAdc, fwdAdcSmoothed, refAdcSmoothed;
                    ushort fwdAdcPeak, refAdcPeak;
                    lock (_sync)
                    {
                        // Use the peak ADC seen since the previous publish
                        // tick rather than the smoothed value — voice peaks
                        // are 100-200 ms but our publish cadence is 100 ms,
                        // and a hardware peak-reading wattmeter (LP-100A) is
                        // what the operator compares against. Fall back to
                        // the smoothed value if no new sample arrived in
                        // this tick (radio quiescence) so the bar doesn't
                        // collapse to zero between hi-pri packets.
                        fwdAdcPeak = _fwdAdcPeak;
                        refAdcPeak = _refAdcPeak;
                        fwdAdcSmoothed = _fwdAdc;
                        refAdcSmoothed = _refAdc;
                        fwdAdc = fwdAdcPeak > 0 ? fwdAdcPeak : _fwdAdc;
                        refAdc = refAdcPeak > 0 ? refAdcPeak : _refAdc;
                        _fwdAdcPeak = 0;
                        _refAdcPeak = 0;
                    }
                    var cal = RadioCalibrations.For(_radio.ConnectedBoardKind, _radio.EffectiveOrionMkIIVariant);
                    // Watts use peak-hold (matches LP-100A peak-reading
                    // wattmeter). SWR uses the smoothed ADC pair instead:
                    // peak(FWD) vs peak(REF) are two uncorrelated transient
                    // maxima and their ratio is not a physical standing-wave
                    // ratio — on HL2 both rails can saturate together during
                    // PA-on / LPF-relay transients, with REF clamping a few
                    // counts above FWD, which the trip logic reads as 9:1.
                    var (fwdW, refW, _) = ComputeMeters(fwdAdc, refAdc, cal);
                    var (_, _, swrVal) = ComputeMeters(fwdAdcSmoothed, refAdcSmoothed, cal);
                    swr = swrVal;
                    // Stage meters are published by WdspDspEngine.ProcessTxBlock;
                    // may lag the first TX block by a few ticks at MOX-on, which
                    // reads as "Silent" (−∞ level / 0 GR) — UI treats as empty.
                    var stage = _pipe.CurrentEngine?.GetTxStageMeters() ?? TxStageMeters.Silent;
                    frame = BuildFrame((float)fwdW, (float)refW, (float)swr, stage);

                    EmitTxMetersDiag(
                        DateTime.UtcNow,
                        fwdAdc, refAdc,
                        fwdAdcSmoothed, refAdcSmoothed,
                        fwdAdcPeak, refAdcPeak,
                        fwdW, refW, swr,
                        cal);

                    bool isTun = _tx.IsTunOn;
                    DateTime keyedAt = (isTun ? _tx.TunStartedAt : _tx.MoxStartedAt) ?? DateTime.UtcNow;
                    if (EvaluateSwrTrip(swr, DateTime.UtcNow, isTun, keyedAt) is { } tripReason)
                    {
                        // TryTripForAlert is idempotent — a second caller on the
                        // same tick (e.g. timeout firing concurrently) finds MOX
                        // already off and no-ops.
                        _tx.TryTripForAlert(AlertKind.SwrTrip, tripReason);
                    }
                }
                else
                {
                    // Zero the TX fields while idle so the UI doesn't latch a
                    // stale pre-unkey reading. Stage meters go to Silent (−∞)
                    // so the diagnostic strip renders empty instead of latching
                    // last-during-TX values.
                    frame = BuildFrame(0f, 0f, 1.0f, TxStageMeters.Silent);
                    // Clear the trip timer when not keyed so a brief spike doesn't
                    // carry over into the next TX.
                    lock (_sync) { _swrAboveThresholdSince = null; }
                }

                _hub.Broadcast(frame);

                // Raise TCI meter event (FWD, REF, SWR, ALC peak, ALC GR)
                TxMetersUpdated?.Invoke(frame.FwdWatts, frame.RefWatts, frame.Swr, frame.AlcPk, frame.AlcGr);

                // PureSignal stage meters — broadcast only while PsEnabled is
                // armed so idle wire stays quiet. The engine returns
                // `PsStageMeters.Silent` when PS is off, so we double-gate on
                // both the StateDto bit and the engine view to avoid emitting
                // a frame between the operator arming PS and the engine
                // applying it.
                var snap = _radio.Snapshot();
                if (snap.PsEnabled && _pipe.CurrentEngine is IDspEngine ps)
                {
                    var psm = ps.GetPsStageMeters();
                    var psFrame = new PsMetersFrame(
                        FeedbackLevel: psm.FeedbackLevel,
                        CorrectionDb: psm.CorrectionDb,
                        CalState: psm.CalState,
                        Correcting: psm.Correcting,
                        MaxTxEnvelope: psm.MaxTxEnvelope);
                    _hub.Broadcast(psFrame);
                    // Mirror the live read-out into the StateDto so REST/state
                    // pollers see it too — same pattern PA/Mic meters use.
                    _radio.UpdatePsLiveReadout(psm.FeedbackLevel, psm.CalState, psm.Correcting);
                }

                // PA temperature broadcast — 2 Hz always, throttled against
                // wall-clock so the 10 Hz MOX loop emits it every 5th tick
                // and the 2 Hz idle loop emits it every tick. Suppressed
                // until at least one telemetry sample has landed; a fresh
                // client would otherwise see a garbage ADC of 0 mapped to
                // the -40 °C clamp floor.
                var nowUtc = DateTime.UtcNow;
                bool paSeen;
                double paAdc;
                lock (_sync) { paSeen = _seenPaTempSample; paAdc = _paTempAdc; }
                if (paSeen && nowUtc - _lastPaTempBroadcastAtUtc >= PaTempTick)
                {
                    _lastPaTempBroadcastAtUtc = nowUtc;
                    _hub.Broadcast(new PaTempFrame(ConvertPaTempAdcToCelsius(paAdc)));
                }

                try { await Task.Delay(mox ? MoxTick : IdleTick, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "tx.meters broadcast loop exited with error");
        }
    }

    // Once-per-second diagnostic line emitted while MOX/TUN is on. Captures
    // raw + smoothed + peak ADC for both axes, the per-second telemetry slot
    // counts, and the watts/SWR the meter just published. Purpose: tell us
    // whether the radio is sending FWD/REF samples at all, what raw ADC
    // values they carry, and whether the calibration math is what's
    // collapsing the reading to ~0 W. INFO level so operators can capture it
    // without enabling debug logging; silent during RX.
    private void EmitTxMetersDiag(
        DateTime nowUtc,
        double fwdAdc, double refAdc,
        double fwdAdcSmoothed, double refAdcSmoothed,
        ushort fwdAdcPeak, ushort refAdcPeak,
        double fwdW, double refW, double swr,
        RadioCalibration cal)
    {
        if (nowUtc - _lastDiagLogAtUtc < TimeSpan.FromSeconds(1)) return;
        uint fwdCount, refCount, paTempCount;
        ushort lastFwdAin1, lastFwdAin0, lastRefAin0;
        bool seenFwdRef, seenPaTemp;
        double paTempAdcLast;
        lock (_sync)
        {
            fwdCount = _diagFwdSlotCount;
            refCount = _diagRefSlotCount;
            paTempCount = _diagPaTempSlotCount;
            lastFwdAin1 = _diagLastFwdAin1;
            lastFwdAin0 = _diagLastFwdAin0;
            lastRefAin0 = _diagLastRefAin0;
            seenFwdRef = _seenSample;
            seenPaTemp = _seenPaTempSample;
            paTempAdcLast = _paTempAdc;
            _diagFwdSlotCount = 0;
            _diagRefSlotCount = 0;
            _diagPaTempSlotCount = 0;
        }
        _lastDiagLogAtUtc = nowUtc;
        _log.LogInformation(
            "tx.meters.diag board={Board} cal=bridge{Bridge:F2}/ref{Ref:F1}/off{Off} " +
            "fwdSlot/s={FwdCount} refSlot/s={RefCount} paTempSlot/s={PaTempCount} " +
            "lastFwdAin1={LastFwdAin1} lastFwdAin0={LastFwdAin0} lastRefAin0={LastRefAin0} " +
            "fwdAdcSm={FwdSm:F0} refAdcSm={RefSm:F0} fwdAdcPk={FwdPk} refAdcPk={RefPk} " +
            "fwdAdcUsed={FwdUsed:F0} refAdcUsed={RefUsed:F0} " +
            "fwdW={FwdW:F2} refW={RefW:F2} swr={Swr:F2} " +
            "seenFwdRef={SeenFR} seenPaTemp={SeenPT} paTempAdc={PaTempAdc:F0}",
            _radio.ConnectedBoardKind, cal.BridgeVolt, cal.RefVoltage, cal.AdcCalOffset,
            fwdCount, refCount, paTempCount,
            lastFwdAin1, lastFwdAin0, lastRefAin0,
            fwdAdcSmoothed, refAdcSmoothed, fwdAdcPeak, refAdcPeak,
            fwdAdc, refAdc,
            fwdW, refW, swr,
            seenFwdRef, seenPaTemp, paTempAdcLast);
    }

    /// <summary>
    /// Evaluate the per-mode SWR sustain window and return the operator-facing
    /// trip message when the active-mode threshold has been exceeded for the
    /// active-mode sustain duration, or null if not yet. Honours the per-mode
    /// startup-grace window after the keying edge so a bridge-settle transient
    /// at MOX/TUN-on does not arm the trip before the load has stabilised.
    /// Exposed as <c>internal</c> so unit tests can drive synthetic timestamps
    /// without a <see cref="DateTime"/> abstraction — the production caller
    /// passes <see cref="DateTime.UtcNow"/>. Firing resets the timer so the
    /// caller gets exactly one trip per sustained excursion.
    /// </summary>
    internal string? EvaluateSwrTrip(double swr, DateTime now, bool isTun, DateTime keyedAt)
    {
        var threshold = isTun ? SwrTripThresholdTun : SwrTripThresholdMox;
        var sustain   = isTun ? SwrTripDurationTun  : SwrTripDurationMox;
        var grace     = isTun ? SwrStartupGraceTun  : SwrStartupGraceMox;

        lock (_sync)
        {
            if (now - keyedAt < grace)
            {
                _swrAboveThresholdSince = null;
                return null;
            }

            if (swr > threshold)
            {
                if (_swrAboveThresholdSince is null)
                {
                    _swrAboveThresholdSince = now;
                    return null;
                }
                if (now - _swrAboveThresholdSince.Value >= sustain)
                {
                    _swrAboveThresholdSince = null;
                    return $"SWR {swr:F1}:1 sustained >{(int)sustain.TotalMilliseconds} ms — dropped TX to protect PA";
                }
                return null;
            }
            _swrAboveThresholdSince = null;
            return null;
        }
    }

    /// <summary>
    /// PRD FR-6 TX timeout: returns a trip reason if MOX or TUN has been
    /// continuously on for ≥ <see cref="TxTimeout"/>, else null. Reads the
    /// keyed-at timestamps from <see cref="TxService"/> (which records them
    /// on state transitions) so the check is stateless in this class.
    /// </summary>
    internal string? EvaluateTimeoutTrip(DateTime now)
    {
        var moxStart = _tx.MoxStartedAt;
        if (moxStart is not null && now - moxStart.Value >= TxTimeout)
            return $"TX timeout: MOX keyed >{(int)TxTimeout.TotalSeconds} s — dropped to protect PA";
        var tunStart = _tx.TunStartedAt;
        if (tunStart is not null && now - tunStart.Value >= TxTimeout)
            return $"TX timeout: TUN keyed >{(int)TxTimeout.TotalSeconds} s — dropped to protect PA";
        return null;
    }

    /// <summary>
    /// Compose a <see cref="TxMetersV2Frame"/> from the protection readings
    /// (FWD/REF/SWR) and the latest stage-meter snapshot. Kept as a small
    /// helper so the MOX and idle branches in <see cref="ExecuteAsync"/>
    /// stay symmetric and a future v3 frame is a one-line change. Pure
    /// function — no instance state.
    /// </summary>
    internal static TxMetersV2Frame BuildFrame(float fwdW, float refW, float swr, TxStageMeters stage)
        => new(
            FwdWatts: fwdW,
            RefWatts: refW,
            Swr: swr,
            MicPk: stage.MicPk,
            MicAv: stage.MicAv,
            EqPk: stage.EqPk,
            EqAv: stage.EqAv,
            LvlrPk: stage.LvlrPk,
            LvlrAv: stage.LvlrAv,
            LvlrGr: stage.LvlrGr,
            CfcPk: stage.CfcPk,
            CfcAv: stage.CfcAv,
            CfcGr: stage.CfcGr,
            CompPk: stage.CompPk,
            CompAv: stage.CompAv,
            AlcPk: stage.AlcPk,
            AlcAv: stage.AlcAv,
            AlcGr: stage.AlcGr,
            OutPk: stage.OutPk,
            OutAv: stage.OutAv);

    /// <summary>
    /// Port of Thetis <c>console.cs:25008-25072</c> watts math plus the
    /// <c>console.cs:25972-25978</c> SWR ratio. Exposed for unit tests —
    /// pure function, no state.
    /// </summary>
    public static (double FwdWatts, double RefWatts, double Swr) ComputeMeters(
        double fwdAdc, double refAdc, RadioCalibration cal)
    {
        double fwdV = (fwdAdc - cal.AdcCalOffset) / 4095.0 * cal.RefVoltage;
        double refV = (refAdc - cal.AdcCalOffset) / 4095.0 * cal.RefVoltage;
        double fwdW = fwdV * fwdV / cal.BridgeVolt;
        double refW = refV * refV / cal.BridgeVolt;
        if (fwdW < 0 || double.IsNaN(fwdW)) fwdW = 0;
        if (refW < 0 || double.IsNaN(refW)) refW = 0;

        double swr;
        if (fwdW <= SwrMinFwdWatts)
        {
            swr = 1.0;
        }
        else
        {
            double ratio = refW / fwdW;
            if (ratio < 0) ratio = 0;
            if (ratio >= 1.0)
            {
                swr = SwrMax;
            }
            else
            {
                double rho = Math.Sqrt(ratio);
                double s = (1.0 + rho) / (1.0 - rho);
                if (double.IsNaN(s) || double.IsInfinity(s)) swr = SwrMax;
                else swr = Math.Min(s, SwrMax);
            }
        }

        return (fwdW, refW, swr);
    }

    private void OnConnected(Zeus.Protocol1.IProtocol1Client client)
    {
        _subscribedClient = client;
        client.TelemetryReceived += OnTelemetry;
    }

    private void OnDisconnected()
    {
        var client = _subscribedClient;
        _subscribedClient = null;
        if (client is not null) client.TelemetryReceived -= OnTelemetry;
        lock (_sync)
        {
            _fwdAdc = 0;
            _refAdc = 0;
            _fwdAdcPeak = 0;
            _refAdcPeak = 0;
            _paTempAdc = 0;
            _seenPaTempSample = false;
            _seenSample = false;
            _swrAboveThresholdSince = null;
            _diagFwdSlotCount = 0;
            _diagRefSlotCount = 0;
            _diagPaTempSlotCount = 0;
            _diagLastFwdAin1 = 0;
            _diagLastFwdAin0 = 0;
            _diagLastRefAin0 = 0;
        }
        // Reset the broadcast throttle so the next connection's first
        // sample fires a PaTempFrame immediately instead of waiting out
        // the previous session's 500 ms window.
        _lastPaTempBroadcastAtUtc = DateTime.MinValue;
        _lastDiagLogAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Hi-priority status (UDP 1025) handler for Protocol 2. The packet
    /// already carries FWD/REF as 16-bit ADC values matched to the same
    /// per-board RadioCalibration tables the P1 path uses, so we route both
    /// axes through OnTelemetryRaw. PA temperature on G2 lives on a
    /// different ADC slot and isn't decoded yet — separate task.
    ///
    /// Runs on the Protocol2Client RX thread; OnTelemetryRaw takes _sync.
    /// </summary>
    public void OnP2Telemetry(Zeus.Protocol2.P2TelemetryReading reading)
    {
        OnTelemetryRaw(reading.FwdAdc, reading.RevAdc);
    }

    private void OnP2Connected(Zeus.Protocol2.Protocol2Client client)
    {
        _subscribedP2Client = client;
        client.TelemetryReceived += OnP2Telemetry;
        _log.LogInformation("tx.meters subscribed to p2 hi-priority telemetry");
    }

    private void OnP2Disconnected()
    {
        var client = _subscribedP2Client;
        _subscribedP2Client = null;
        if (client is not null) client.TelemetryReceived -= OnP2Telemetry;
        lock (_sync)
        {
            _fwdAdc = 0;
            _refAdc = 0;
            _fwdAdcPeak = 0;
            _refAdcPeak = 0;
            _seenSample = false;
            _swrAboveThresholdSince = null;
            _diagFwdSlotCount = 0;
            _diagRefSlotCount = 0;
            _diagLastFwdAin1 = 0;
            _diagLastRefAin0 = 0;
        }
        _lastDiagLogAtUtc = DateTime.MinValue;
    }
}
