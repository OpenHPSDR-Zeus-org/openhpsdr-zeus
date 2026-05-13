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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

/// <summary>
/// PureSignal AutoAttenuate loop. Polls the calcc feedback level (info[4]) at
/// 10 Hz while PS is armed and the operator has AutoAttenuate on; if the level
/// lands outside the [128, 181] ideal window calcc rejects every fit
/// (binfo[6] != 0 → scOK=0 → bs_count==2 → LRESET → loop), so PS never
/// converges. The loop adjusts the radio's TX step attenuator to bring
/// feedback into the window.
///
/// Mirrors Thetis <c>PSForm.cs:728-784</c> timer2code and the
/// <c>PSForm.cs:1109-1112</c> NeedToRecalibrate threshold:
///   • feedback &gt; 181  → too hot → attenuate more (delta &gt; 0).
///   • feedback ≤ 128 AND current att &gt; 0 → too quiet → attenuate less.
/// Step size is <c>20 * log10(feedback / 152.293)</c> dB clamped to ±1 per
/// tick (1 dB/100 ms — matches Thetis feel; converges within a couple of
/// seconds without overshooting). After every attenuator change we issue a
/// SetPSControl(reset=1) so calcc retries with the new feedback level.
///
/// Two wire paths are supported. Protocol 2 (G2/Saturn etc.) uses the simple
/// step-then-reset pattern via <see cref="Zeus.Protocol2.Protocol2Client.SetTxAttenuationDb"/>.
/// HL2 (Protocol 1) uses the mi0bot timer2code 3-state dance — disable PS at
/// the engine, write the new ATTOnTX wire byte, then re-enable PS — because
/// changing C4 (AD9866 TX PGA) mid-fit otherwise wedges calcc into binfo[6]
/// permanent-fault. mi0bot ref: PSForm.cs:728-815 timer2code.
/// </summary>
public sealed class PsAutoAttenuateService : BackgroundService
{
    // Thetis ideal feedback target: 152.293 (PSForm.cs:745). Window 128..181
    // matches the lblPSInfoFB green-LED thresholds (PSForm.cs:1123-1138).
    private const double IdealFeedback = 152.293;
    private const int FeedbackLowThreshold = 128;
    private const int FeedbackHighThreshold = 181;

    // 10 Hz tick. Same cadence Thetis runs timer2code at when PS is armed and
    // the form has focus (PSForm.cs:204-209, m_bQuckAttenuate=false default).
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    // perf3 iter3: idle cadence used when PS is disarmed or the radio isn't
    // keyed. At 1 Hz we still notice an arm-edge or MOX-on within a second
    // (which is plenty — the operator's own click latency is more than that),
    // while shedding 90 % of the steady-state TP wake-ups. The Tick1 body
    // early-returns in ≤2 lock-free checks on the idle gates, so the wake
    // cost is small per-tick; the win is the 10× lower wake rate.
    private static readonly TimeSpan IdleTick = TimeSpan.FromSeconds(1);

    // Hardware bounds for the TX step attenuator (Thetis network.c:1238-1242
    // writes a single byte 0..31 dB per ADC tap).
    private const int TxAttnMinDb = 0;
    private const int TxAttnMaxDb = 31;

    // HL2 TX-side step attenuator range (mi0bot console.cs:2084 udTXStepAttData
    // Minimum=-28, Maximum=+31). Wider than the bare-HPSDR 0..31 because HL2's
    // AD9866 TX PGA can reduce PA drive below the nominal 0 dB reference.
    private const int Hl2TxAttnMinDb = -28;
    private const int Hl2TxAttnMaxDb = 31;

    // Settle time after a step change: give the radio one wire-cycle to pick
    // up the new attenuator, then issue the reset so calcc starts fresh.
    private static readonly TimeSpan PostStepSettle = TimeSpan.FromMilliseconds(100);

    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipe;
    private readonly ILogger<PsAutoAttenuateService> _log;

    // Mirrored attenuator value — server-of-truth for what we last asked the
    // radio to apply. Reset to 0 on every fresh PS arm (PsEnabled false→true)
    // so a new operator session starts from the radio's untouched baseline.
    private int _currentAttnDb;
    private bool _psWasEnabled;

    // Last-observed info[5] (calcc CalibrationAttempts counter). We only
    // step the attenuator after calcc finishes a NEW fit — matches Thetis
    // PSForm.cs:1097-1099 timer2code which gates on
    // `CalibrationAttemptsChanged`. Stepping on every 100 ms tick instead
    // makes cm jump mid-fit, scheck flags 0x40, scOK=0, bs_count==2 forces
    // LRESET, and calcc never converges. Initialize to -1 so the first
    // observed counter (0 or any value) registers as "new".
    private int _lastCalibrationAttempts = -1;

    // Rate-limit bucket for diagnostic gate-skip logging. Tick1 runs at 10 Hz;
    // without rate-limiting a stuck gate would emit 10 lines/sec. 1 s bucket
    // gives one line per gate-state per second — enough to localise the
    // failing gate during a 5 s rack key without flooding the log.
    private long _lastGateLogTickMs;
    private const long GateLogIntervalMs = 1000;

    // HL2 P1 path — mi0bot timer2code 3-state dance. mi0bot PSForm.cs:728-815.
    //   Monitor:          detect new fit + threshold breach → SetPSControl
    //                     reset=1 (disable PS in WDSP) → SetNewValues
    //   SetNewValues:     write ATTOnTX wire byte → 100 ms settle →
    //                     RestoreOperation
    //   RestoreOperation: SetPSControl(0, save_single, save_auto, 0)
    //                     (re-enable PS with the operator's prior cal-mode)
    //                     → Monitor
    // Cycling at the 10 Hz tick gives ~200 ms between disable and re-enable —
    // enough for the C4 frame change to land on the radio without leaving
    // calcc fitting against a moving envelope.
    private enum Hl2AutoAttState { Monitor, SetNewValues, RestoreOperation }
    private Hl2AutoAttState _hl2State = Hl2AutoAttState.Monitor;

    // P2 path — same 3-state dance as HL2 (mi0bot PSForm.cs:728-815 is
    // protocol-agnostic; non-HL2 boards run the identical structure with
    // their own attenuator range + wire write). Previously the P2 branch
    // used a ±1 dB clamp + per-step engine.ResetPs() pattern which made
    // first-arm convergence take 5-10 s vs. Thetis's 2-3 s and truncated
    // the polynomial fit on each step (driving steady-state IMD).
    private enum P2AutoAttState { Monitor, SetNewValues, RestoreOperation }
    private P2AutoAttState _p2State = P2AutoAttState.Monitor;
    private int _p2DeltaDb;
    private bool _p2SavedAuto;
    private bool _p2SavedSingle;
    private int _hl2DeltaDb;
    private bool _hl2SavedAuto;
    private bool _hl2SavedSingle;

    // *** DEVIATION FROM mi0bot ***
    // Silent server-side auto-cal of WDSP hw_peak from observed TX envelope.
    // mi0bot exposes PSForm.cs txtPSpeak as a hand-dialed operator value
    // defaulting to clsHardwareSpecific.cs:303-328 PSDefaultPeak. We deviate
    // per Brian (EI6LF) "I want it automatic" instruction: WDSP calcc bins
    // env*hw_scale into 16 bins where hw_scale = 1/hw_peak; samples > hw_peak
    // are dropped; bin 15 covers env*hw_scale in 0.9375..1.0 → bin 15 fills
    // only when hw_peak < observed * 1.067. Sweet spot: hw_peak = observed *
    // 1.02 (all samples bin AND bin 15 fills with 4.5% jitter headroom).
    // Operator can still override via the HW peak input — auto-cal will
    // re-target on the next stable TX cycle.
    private const double HwPeakSafetyMargin = 1.02;        // 2% above observed
    private const double HwPeakDeadbandRatio = 0.05;       // ≥5% off-target → push
    private const double EnvelopeMinForAutoCal = 0.01;     // skip silent TX
    private const double HwPeakMin = 0.05;                 // server clamps (0,2]
    private const double HwPeakMax = 2.0;
    private const long AutoCalMinIntervalMs = 1000;        // ≤ 1 push per second
    private long _lastAutoCalTickMs;

    public PsAutoAttenuateService(
        RadioService radio,
        TxService tx,
        DspPipelineService pipe,
        ILogger<PsAutoAttenuateService> log)
    {
        _radio = radio;
        _tx = tx;
        _pipe = pipe;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("psAutoAttn.start");
        try
        {
            // perf3 iter3: adaptive tick cadence. Start in the idle cadence
            // (1 Hz) and switch to the active cadence (10 Hz) the moment PS
            // arms AND the radio is keyed. Brings TP wake-ups down by ~90 %
            // for the steady-state RX-only operator scenario where this
            // service used to spin at 10 Hz purely to evaluate two booleans
            // and return. PeriodicTimer.Period (settable since .NET 8) lets
            // us reuse the same timer instance — no dispose/recreate churn.
            using var timer = new PeriodicTimer(IdleTick);
            var currentPeriod = IdleTick;
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Tick1();
                }
                catch (Exception ex)
                {
                    // Swallow — the loop must keep running so a transient
                    // engine race doesn't permanently disable auto-attn.
                    _log.LogWarning(ex, "psAutoAttn.tick failed");
                }
                // Decide the cadence for the NEXT tick. Active when PS is
                // armed and the radio is keyed (MOX or TwoTone) — the only
                // window where Tick1 does work other than early-returning.
                // Snapshot is a struct copy on RadioService's cached state;
                // ~free.
                var s = _radio.Snapshot();
                var wantActive = s.PsEnabled && (_tx.IsMoxOn || _tx.IsTwoToneOn);
                var wantPeriod = wantActive ? Tick : IdleTick;
                if (wantPeriod != currentPeriod)
                {
                    timer.Period = wantPeriod;
                    currentPeriod = wantPeriod;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    // Diagnostic — emits one line per second tagging which gate short-
    // circuited Tick1. Without this the loop is invisible when it returns
    // early (the only visible signals were `psAutoAttn.armed` and
    // `psAutoAttn.step`, neither of which fire when a gate fails). Used to
    // localise the silent-gate symptom on the G2 MkII rack test.
    private void LogGate(string outcome)
    {
        long now = Environment.TickCount64;
        if (now - _lastGateLogTickMs < GateLogIntervalMs) return;
        _lastGateLogTickMs = now;
        _log.LogInformation("psAutoAttn.gate {Outcome}", outcome);
    }

    private void Tick1()
    {
        var s = _radio.Snapshot();

        // PS-arm edge: re-baseline _currentAttnDb on every false→true so a
        // fresh arm starts at the radio's untouched 0 dB. The actual radio
        // state may differ if the operator manually changed step-att between
        // sessions; assume the radio holds 0 between arms (matches pihpsdr).
        // Also reset the HL2 state machine so a fresh arm always starts in
        // Monitor — if the prior session was disarmed mid-dance, we don't
        // want to fire RestoreOperation against a stale saved cal-mode.
        if (s.PsEnabled && !_psWasEnabled)
        {
            _currentAttnDb = 0;
            _lastCalibrationAttempts = -1;
            _hl2State = Hl2AutoAttState.Monitor;
            _p2State = P2AutoAttState.Monitor;
            _lastAutoCalTickMs = 0;
            _log.LogInformation("psAutoAttn.armed reset attn={Db}", _currentAttnDb);
        }
        _psWasEnabled = s.PsEnabled;

        // Hard idle conditions — also force both state machines back to
        // Monitor so a mid-dance disarm/unkey doesn't strand PS in the
        // disabled state when the operator re-keys.
        if (!s.PsEnabled)
        {
            _hl2State = Hl2AutoAttState.Monitor;
            _p2State = P2AutoAttState.Monitor;
            LogGate("skip=PsEnabled-off");
            return;
        }
        if (!_tx.IsMoxOn && !_tx.IsTwoToneOn)
        {
            _hl2State = Hl2AutoAttState.Monitor;
            _p2State = P2AutoAttState.Monitor;
            LogGate("skip=not-keyed");
            return;
        }

        var engine = _pipe.CurrentEngine;
        if (engine is null)
        {
            _hl2State = Hl2AutoAttState.Monitor;
            _p2State = P2AutoAttState.Monitor;
            LogGate("skip=engine-null");
            return;
        }

        // Auto-cal hw_peak from observed envelope. Independent of HL2/P2 path
        // and runs every keyed tick — same gates as the rest of the loop
        // (PsEnabled + TX active + engine present, all checked above).
        TickAutoCalHwPeak(s, engine);

        // HL2 P1 branch — mi0bot timer2code 3-state dance (PSForm.cs:728-815).
        // Run before the P2 path because HL2 has its own client + wire
        // semantics (ATTOnTX writes C4 of register 0x0a during MOX).
        var p1 = _radio.ActiveClient;
        if (_radio.ConnectedBoardKind == HpsdrBoardKind.HermesLite2 && p1 is not null)
        {
            Tick1Hl2(s, engine, p1);
            return;
        }

        // P2 branch — mi0bot timer2code 3-state dance (PSForm.cs:728-815).
        // Same structure as Tick1Hl2 but with P2 attenuator range (0..31)
        // and the Protocol2Client wire write.
        var p2 = _pipe.CurrentP2Client;
        if (p2 is null) { LogGate("skip=p2-null"); return; }
        Tick1P2(s, engine, p2);
    }

    // *** DEVIATION FROM mi0bot ***
    // Silent server-side auto-cal of WDSP hw_peak from observed TX envelope
    // (GetPSMaxTX). mi0bot leaves hw_peak as the operator-tuned PSForm.cs
    // txtPSpeak. Per Brian (EI6LF) "I want it automatic" instruction we
    // retarget to observed*1.02 whenever the current hw_peak is ≥5% off,
    // throttled to ≤1 push/sec and skipped while the HL2 auto-att dance is
    // mid-flight (we don't want to fight a SetPSControl(reset=1) sequence).
    // Operator can still override via the HW peak input — auto-cal will
    // re-target on the next eligible tick.
    private void TickAutoCalHwPeak(StateDto s, IDspEngine engine)
    {
        // 2026-05-03: disabled. mi0bot has no hw_peak auto-cal — operator-tuned
        // only (PSForm.cs:815-831 txtPSpeak.TextChanged → SetPSHWPeak). Auto-cal
        // pinned env/hw_peak ≈ 0.98 and starved calcc.c LCOLLECT bins 0..13, so
        // info[5] CalibrationAttempts never incremented and PS stuck in COLLECT.
        // See docs/puresignal.hl2.md (D1) for the full diagnosis. The original
        // body is preserved below so this is a one-line revert if the diagnosis
        // is wrong; flagged for maintainer review per CLAUDE.md (defaults +
        // UX behaviour are red-light).
        return;
        // (rest of method preserved below for easy revert)
#pragma warning disable CS0162 // Unreachable code detected
        // Don't fight the HL2 timer2code dance — SetNewValues / RestoreOperation
        // are mid-disable; firing SetPsAdvanced now would race the in-flight
        // SetPSControl(reset=1)/(re-arm) sequence.
        if (_hl2State != Hl2AutoAttState.Monitor) return;

        // 1 push / sec ceiling. Even a sustained drift only writes once per
        // 10 ticks, so we never flood the engine with same-direction nudges.
        long now = Environment.TickCount64;
        if (now - _lastAutoCalTickMs < AutoCalMinIntervalMs) return;

        double env = engine.GetPsStageMeters().MaxTxEnvelope;
        if (env < EnvelopeMinForAutoCal) return;   // no real TX content

        // Calcc bin-fill math: hw_scale = 1/hw_peak; samples bin when
        // env*hw_scale ≤ 1.0; bin 15 covers env*hw_scale ∈ [0.9375, 1.0] →
        // bin 15 fills only when hw_peak < observed*1.067. 1.02× gives all
        // samples bin AND bin 15 catches the peak with 4.5% jitter headroom.
        double target = Math.Clamp(env * HwPeakSafetyMargin, HwPeakMin, HwPeakMax);
        double current = s.PsHwPeak;
        if (Math.Abs(current - target) / Math.Max(target, 1e-3) <= HwPeakDeadbandRatio)
        {
            return;   // 5% deadband — avoid constant tiny adjustments
        }

        target = Math.Round(target, 4);
        _log.LogInformation(
            "psAutoAttn.autoCal env={Env:F4} oldHw={Old:F4} newHw={New:F4}",
            env, current, target);
        _radio.SetPsAdvanced(new PsAdvancedSetRequest(HwPeak: target));
        _lastAutoCalTickMs = now;
#pragma warning restore CS0162
    }

    // mi0bot timer2code HL2 path (PSForm.cs:728-815). Three states cycle at
    // the 10 Hz tick: Monitor → SetNewValues → RestoreOperation → Monitor.
    // The disable/re-enable bracket around the C4 wire change is what
    // prevents the calcc binfo[6] wedge that bench-driver hit earlier when
    // we changed C4 mid-MOX without the dance. Once we're past Monitor we
    // MUST complete the cycle so PS gets re-enabled — the early gates at
    // the top of Tick1 honour that by only running while in Monitor.
    private void Tick1Hl2(StateDto s, IDspEngine engine, IProtocol1Client p1)
    {
        switch (_hl2State)
        {
            case Hl2AutoAttState.Monitor:
            {
                // Operator can disable AutoAttenuate without losing PS — the
                // gate sits inside Monitor so the state machine never starts
                // a dance the operator didn't ask for.
                if (!s.PsAutoAttenuate)
                {
                    LogGate("hl2.skip=AutoAttenuate-off");
                    return;
                }

                var psm = engine.GetPsStageMeters();
                int feedback = (int)Math.Round(psm.FeedbackLevel);

                // mi0bot PSForm.cs:1097-1099 CalibrationAttemptsChanged:
                // gate every step on a freshly-completed calcc fit.
                if (_lastCalibrationAttempts >= 0
                    && psm.CalibrationAttempts == _lastCalibrationAttempts)
                {
                    LogGate($"hl2.skip=no-new-calc info5={psm.CalibrationAttempts} fb={feedback}");
                    return;
                }
                _lastCalibrationAttempts = psm.CalibrationAttempts;

                // info[4] == 0 → calcc hasn't completed a fit yet.
                if (feedback <= 0)
                {
                    LogGate($"hl2.skip=fb-zero psm.fb={psm.FeedbackLevel:F2}");
                    return;
                }

                // mi0bot NeedToRecalibrate_HL2 (PSForm.cs:1109-1112):
                //   FB > 181  OR  (FB <= 128 AND ATTOnTX > -28)
                bool tooHot = feedback > FeedbackHighThreshold;
                bool tooQuiet = feedback <= FeedbackLowThreshold && _currentAttnDb > Hl2TxAttnMinDb;
                if (!tooHot && !tooQuiet)
                {
                    LogGate($"hl2.skip=in-window fb={feedback} attn={_currentAttnDb}");
                    return;
                }

                // mi0bot PSForm.cs:745-761 — full ddB step (no ±1 clamp on HL2)
                // so a single dance can pull a hot envelope back into window
                // in one cycle. NaN guard + ±100 dB rails per mi0bot.
                double ddB = 20.0 * Math.Log10(feedback / IdealFeedback);
                if (double.IsNaN(ddB)) ddB = 10.0;
                else if (ddB < -100.0) ddB = -10.0;
                else if (ddB > 100.0) ddB = 10.0;
                _hl2DeltaDb = (int)Math.Round(ddB, MidpointRounding.AwayFromZero);

                // Save the operator's current cal-mode so RestoreOperation
                // brings it back exactly. mi0bot uses _save_singlecalON /
                // _save_autoON, captured at the start of the dance.
                _hl2SavedAuto = s.PsAuto;
                _hl2SavedSingle = s.PsSingle;

                // mi0bot PSForm.cs:763 — disable PS BEFORE writing the new
                // ATTOnTX. Engine.SetPsControl(false, false) maps internally
                // to NativeMethods.SetPSControl(id, reset=1, mancal=0,
                // automode=0, turnon=0) — exactly mi0bot's call.
                engine.SetPsControl(autoCal: false, singleCal: false);

                _log.LogInformation(
                    "psAutoAttn.hl2.monitor fb={Fb} info5={Cal} ddB={DDb:F1} delta={Delta} attn={Db}",
                    feedback, psm.CalibrationAttempts, ddB, _hl2DeltaDb, _currentAttnDb);
                _hl2State = Hl2AutoAttState.SetNewValues;
                return;
            }

            case Hl2AutoAttState.SetNewValues:
            {
                // mi0bot PSForm.cs:769-788. State advances first so a no-op
                // delta still reaches RestoreOperation and re-arms PS — same
                // safety the WinForms version has.
                _hl2State = Hl2AutoAttState.RestoreOperation;
                int newAttn = Math.Clamp(
                    _currentAttnDb + _hl2DeltaDb,
                    Hl2TxAttnMinDb,
                    Hl2TxAttnMaxDb);
                if (newAttn != _currentAttnDb)
                {
                    _log.LogInformation(
                        "psAutoAttn.hl2.setNewValues attn {Old}->{New} dB",
                        _currentAttnDb, newAttn);
                    _currentAttnDb = newAttn;
                    p1.SetHl2TxStepAttenuationDb(newAttn);
                    // mi0bot PSForm.cs:783 Thread.Sleep(100) — give the
                    // C4 frame time to land on the wire before the next
                    // tick re-enables PS in calcc.
                    try { Task.Delay(PostStepSettle).Wait(); } catch { /* ignore */ }
                }
                return;
            }

            case Hl2AutoAttState.RestoreOperation:
            {
                // mi0bot PSForm.cs:790-815 SetPSControl(0, save_single,
                // save_auto, 0). Engine.SetPsControl translates the saved
                // (auto, single) pair into the same wire call.
                engine.SetPsControl(autoCal: _hl2SavedAuto, singleCal: _hl2SavedSingle);
                _log.LogInformation(
                    "psAutoAttn.hl2.restoreOperation auto={Auto} single={Single}",
                    _hl2SavedAuto, _hl2SavedSingle);
                _hl2State = Hl2AutoAttState.Monitor;
                return;
            }
        }
    }

    // P2 timer2code dance — same shape as HL2 (mi0bot PSForm.cs:728-815 is
    // protocol-agnostic in its outer structure; the differences are the wire
    // write and the attenuator range). Replaces the prior ±1 dB clamp +
    // engine.ResetPs()-per-step pattern which truncated calcc's polynomial
    // fit on every step, driving first-arm convergence to 5-10 s and feeding
    // mid-fit cm coefficient jumps that the operator heard as splatter
    // bursts. Thetis applies the full ddB delta in one shot bracketed by a
    // disable/restore SetPSControl pair so calcc only resets once per dance.
    //
    // Once we're past Monitor we MUST complete the cycle so PS gets re-enabled —
    // the early gates at the top of Tick1 honour that by only running while
    // in Monitor.
    private void Tick1P2(StateDto s, IDspEngine engine, Zeus.Protocol2.Protocol2Client p2)
    {
        switch (_p2State)
        {
            case P2AutoAttState.Monitor:
            {
                // Operator can disable AutoAttenuate without losing PS — the
                // gate sits inside Monitor so the state machine never starts
                // a dance the operator didn't ask for.
                if (!s.PsAutoAttenuate)
                {
                    LogGate("p2.skip=AutoAttenuate-off");
                    return;
                }

                var psm = engine.GetPsStageMeters();
                int feedback = (int)Math.Round(psm.FeedbackLevel);

                // mi0bot PSForm.cs:1097-1099 CalibrationAttemptsChanged:
                // gate every step on a freshly-completed calcc fit so we
                // never write a new attenuator mid-fit.
                if (_lastCalibrationAttempts >= 0
                    && psm.CalibrationAttempts == _lastCalibrationAttempts)
                {
                    LogGate($"p2.skip=no-new-calc info5={psm.CalibrationAttempts} fb={feedback}");
                    return;
                }
                _lastCalibrationAttempts = psm.CalibrationAttempts;

                // info[4] == 0 → calcc hasn't completed a fit yet.
                if (feedback <= 0)
                {
                    LogGate($"p2.skip=fb-zero psm.fb={psm.FeedbackLevel:F2}");
                    return;
                }

                // mi0bot NeedToRecalibrate non-HL2 path:
                //   FB > 181  OR  (FB <= 128 AND ATTOnTX > 0)
                // The in-window range 128..181 falls into the tooQuiet branch's
                // "feedback >= FeedbackLowThreshold" complement and is filtered
                // by the tooHot check first, so this matches PSForm.cs:1109-1112.
                bool tooHot = feedback > FeedbackHighThreshold;
                bool tooQuiet = feedback < FeedbackLowThreshold && _currentAttnDb > TxAttnMinDb;
                if (!tooHot && !tooQuiet)
                {
                    LogGate($"p2.skip=in-window fb={feedback} attn={_currentAttnDb}");
                    return;
                }

                // mi0bot PSForm.cs:745-761 — full ddB step (no ±1 clamp)
                // so a single dance pulls a hot envelope back into window
                // in one cycle. NaN guard + ±100 dB rails per mi0bot.
                double ddB = 20.0 * Math.Log10(feedback / IdealFeedback);
                if (double.IsNaN(ddB)) ddB = 10.0;
                else if (ddB < -100.0) ddB = -10.0;
                else if (ddB > 100.0) ddB = 10.0;
                _p2DeltaDb = (int)Math.Round(ddB, MidpointRounding.AwayFromZero);

                // Save the operator's current cal-mode so RestoreOperation
                // brings it back exactly. mi0bot uses _save_singlecalON /
                // _save_autoON, captured at the start of the dance.
                _p2SavedAuto = s.PsAuto;
                _p2SavedSingle = s.PsSingle;

                // mi0bot PSForm.cs:763 — disable PS BEFORE writing the new
                // ATTOnTX. Engine.SetPsControl(false, false) maps internally
                // to NativeMethods.SetPSControl(id, reset=1, mancal=0,
                // automode=0, turnon=0) — exactly mi0bot's call. This is the
                // ONE reset per dance, replacing the previous per-step
                // engine.ResetPs() storm.
                engine.SetPsControl(autoCal: false, singleCal: false);

                _log.LogInformation(
                    "psAutoAttn.p2.monitor fb={Fb} info5={Cal} ddB={DDb:F1} delta={Delta} attn={Db}",
                    feedback, psm.CalibrationAttempts, ddB, _p2DeltaDb, _currentAttnDb);
                _p2State = P2AutoAttState.SetNewValues;
                return;
            }

            case P2AutoAttState.SetNewValues:
            {
                // mi0bot PSForm.cs:769-788. State advances first so a no-op
                // delta still reaches RestoreOperation and re-arms PS — same
                // safety the WinForms version has.
                _p2State = P2AutoAttState.RestoreOperation;
                int newAttn = Math.Clamp(
                    _currentAttnDb + _p2DeltaDb,
                    TxAttnMinDb,
                    TxAttnMaxDb);
                if (newAttn != _currentAttnDb)
                {
                    _log.LogInformation(
                        "psAutoAttn.p2.setNewValues attn {Old}->{New} dB",
                        _currentAttnDb, newAttn);
                    _currentAttnDb = newAttn;
                    p2.SetTxAttenuationDb((byte)newAttn);
                    // mi0bot PSForm.cs:783 Thread.Sleep(100) — give the
                    // wire byte time to land before the next tick re-enables
                    // PS in calcc.
                    try { Task.Delay(PostStepSettle).Wait(); } catch { /* ignore */ }
                }
                return;
            }

            case P2AutoAttState.RestoreOperation:
            {
                // mi0bot PSForm.cs:790-815 SetPSControl(0, save_single,
                // save_auto, 0). Engine.SetPsControl translates the saved
                // (auto, single) pair into the same wire call. This re-arms
                // calcc on the new envelope — single LRESET → LCOLLECT pass.
                engine.SetPsControl(autoCal: _p2SavedAuto, singleCal: _p2SavedSingle);
                _log.LogInformation(
                    "psAutoAttn.p2.restoreOperation auto={Auto} single={Single}",
                    _p2SavedAuto, _p2SavedSingle);
                _p2State = P2AutoAttState.Monitor;
                return;
            }
        }
    }
}
