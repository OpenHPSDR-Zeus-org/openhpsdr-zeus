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

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  setPs,
  setPsAdvanced,
  setPsFeedbackSource,
  setPsFeedbackAttenuation,
  setPsMonitor,
  resetPs,
  setTwoTone,
  setTxPreKeyDelay,
} from '../api/client';
import { useRadioStore } from '../state/radio-store';
import { useTxStore } from '../state/tx-store';

// HermesLite2 has no PS-Monitor display source path (no internal feedback
// loopback) but DOES have an internal coupler — so the Internal/External
// feedback-source selector is shown on every board, while PS-Monitor is
// hidden on HL2. See issues #121 and #172.
const HL2_BOARD_ID = 'HermesLite2';

const CAL_STATE_NAMES = [
  'RESET',
  'WAIT',
  'MOXDELAY',
  'SETUP',
  'COLLECT',
  'MOXCHECK',
  'CALC',
  'DELAY',
  'STAYON',
  'TURNON',
];

// Dial geometry (must match the SVG circle below).
const DIAL_R = 78;
const DIAL_C = 2 * Math.PI * DIAL_R;

const SPARK_LEN = 60;

/**
 * PureSignal calibration dashboard. Lives inside the Settings modal
 * (SettingsMenu) and as a standalone dockable panel (PsFlexPanel).
 *
 * Layout: hero strip with a convergence dial, a TX → PA → coupler →
 * feedback signal-flow diagram, and live peak meters. Mode + actions
 * row below, then a two-column timing / hardware grid, then the
 * standard PS-Monitor and two-tone test sections.
 */
export function PsSettingsPanel() {
  const connectedBoard = useRadioStore((s) => s.selection.connected);
  const psMonitorSupported = connectedBoard !== HL2_BOARD_ID;

  const psEnabled = useTxStore((s) => s.psEnabled);
  const psMonitorEnabled = useTxStore((s) => s.psMonitorEnabled);
  const setPsMonitorLocal = useTxStore((s) => s.setPsMonitorEnabled);
  const psAuto = useTxStore((s) => s.psAuto);
  const psSingle = useTxStore((s) => s.psSingle);
  const psPtol = useTxStore((s) => s.psPtol);
  const psAutoAttenuate = useTxStore((s) => s.psAutoAttenuate);
  const txMoxPreKeyDelayMs = useTxStore((s) => s.txMoxPreKeyDelayMs);
  const setTxMoxPreKeyDelayMsLocal = useTxStore((s) => s.setTxMoxPreKeyDelayMs);
  const psMoxDelaySec = useTxStore((s) => s.psMoxDelaySec);
  const psLoopDelaySec = useTxStore((s) => s.psLoopDelaySec);
  const psAmpDelayNs = useTxStore((s) => s.psAmpDelayNs);
  const psHwPeak = useTxStore((s) => s.psHwPeak);
  const psHwPeakDefault = useTxStore((s) => s.psHwPeakDefault);
  const psIntsSpiPreset = useTxStore((s) => s.psIntsSpiPreset);
  const psFeedbackSourceState = useTxStore((s) => s.psFeedbackSource);
  const psFeedbackLevel = useTxStore((s) => s.psFeedbackLevel);
  const psCalState = useTxStore((s) => s.psCalState);
  const psCorrecting = useTxStore((s) => s.psCorrecting);
  const psCorrectionDb = useTxStore((s) => s.psCorrectionDb);
  const psMaxTxEnvelope = useTxStore((s) => s.psMaxTxEnvelope);
  const psCalibrationStalled = useTxStore((s) => s.psCalibrationStalled);
  // Used to gate the "correcting" indicator — WDSP's info[14] stays high
  // across MOX-down once calcc has a curve loaded, so without this gate the
  // panel claims "correcting" with no RF being emitted. moxOn+tunOn+twoToneOn
  // cover every TX-active path. (twoToneOn is also read further down for the
  // 2-tone control row.)
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const setPsAuto = useTxStore((s) => s.setPsAuto);
  const setPsSingle = useTxStore((s) => s.setPsSingle);
  const setPsPtol = useTxStore((s) => s.setPsPtol);
  const setPsAutoAttenuate = useTxStore((s) => s.setPsAutoAttenuate);
  const setPsMoxDelaySec = useTxStore((s) => s.setPsMoxDelaySec);
  const setPsLoopDelaySec = useTxStore((s) => s.setPsLoopDelaySec);
  const setPsAmpDelayNs = useTxStore((s) => s.setPsAmpDelayNs);
  const setPsHwPeak = useTxStore((s) => s.setPsHwPeak);
  const psTxFeedbackAttenuationDb = useTxStore((s) => s.psTxFeedbackAttenuationDb);
  const psTxFeedbackAttenuationDbMin = useTxStore((s) => s.psTxFeedbackAttenuationDbMin);
  const setPsTxFeedbackAttenuationDb = useTxStore((s) => s.setPsTxFeedbackAttenuationDb);
  const setPsIntsSpiPreset = useTxStore((s) => s.setPsIntsSpiPreset);
  const setPsFeedbackSourceLocal = useTxStore((s) => s.setPsFeedbackSource);

  const twoToneOn = useTxStore((s) => s.twoToneOn);
  const twoToneFreq1 = useTxStore((s) => s.twoToneFreq1);
  const twoToneFreq2 = useTxStore((s) => s.twoToneFreq2);
  const twoToneMag = useTxStore((s) => s.twoToneMag);
  const setTwoToneOn = useTxStore((s) => s.setTwoToneOn);
  const setTwoToneFreq1 = useTxStore((s) => s.setTwoToneFreq1);
  const setTwoToneFreq2 = useTxStore((s) => s.setTwoToneFreq2);
  const setTwoToneMag = useTxStore((s) => s.setTwoToneMag);

  const setMode = useCallback(
    (auto: boolean, single: boolean) => {
      setPsAuto(auto);
      setPsSingle(single);
      setPs({ enabled: psEnabled, auto, single }).catch(() => {});
    },
    [psEnabled, setPsAuto, setPsSingle],
  );

  const pushAdvanced = useCallback(
    (overrides: Partial<{
      ptol: boolean;
      autoAttenuate: boolean;
      moxDelaySec: number;
      loopDelaySec: number;
      ampDelayNs: number;
      hwPeak: number;
      intsSpiPreset: string;
    }>) => {
      setPsAdvanced(overrides).catch(() => {});
    },
    [],
  );

  const onReset = useCallback(() => {
    resetPs().catch(() => {});
  }, []);

  // "Run now" — re-arm a single calibration pass. Maps to the existing
  // single-shot path; safe to invoke regardless of current Auto/Single
  // selection (mi0bot PSForm.cs treats Single as a one-shot trigger).
  const onRunNow = useCallback(() => {
    setMode(false, true);
  }, [setMode]);

  const onFeedbackSourceChange = useCallback(
    (next: 'internal' | 'external') => {
      const prev = psFeedbackSourceState;
      setPsFeedbackSourceLocal(next);
      setPsFeedbackSource(next).catch(() => setPsFeedbackSourceLocal(prev));
    },
    [psFeedbackSourceState, setPsFeedbackSourceLocal],
  );

  const onPsMonitorToggle = useCallback(
    (next: boolean) => {
      const prev = psMonitorEnabled;
      setPsMonitorLocal(next);
      setPsMonitor(next).catch(() => setPsMonitorLocal(prev));
    },
    [psMonitorEnabled, setPsMonitorLocal],
  );

  const onTwoToneToggle = useCallback(() => {
    const next = !twoToneOn;
    setTwoToneOn(next);
    setTwoTone({
      enabled: next,
      freq1: twoToneFreq1,
      freq2: twoToneFreq2,
      mag: twoToneMag,
    }).catch(() => setTwoToneOn(!next));
  }, [twoToneOn, twoToneFreq1, twoToneFreq2, twoToneMag, setTwoToneOn]);

  const onTwoToneFreq1Change = useCallback(
    (hz: number) => {
      const v = Math.max(50, Math.min(5000, Math.round(hz)));
      setTwoToneFreq1(v);
      setTwoTone({ enabled: twoToneOn, freq1: v }).catch(() => {});
    },
    [twoToneOn, setTwoToneFreq1],
  );

  const onTwoToneFreq2Change = useCallback(
    (hz: number) => {
      const v = Math.max(50, Math.min(5000, Math.round(hz)));
      setTwoToneFreq2(v);
      setTwoTone({ enabled: twoToneOn, freq2: v }).catch(() => {});
    },
    [twoToneOn, setTwoToneFreq2],
  );

  const onTwoToneMagChange = useCallback(
    (mag: number) => {
      const v = Math.max(0, Math.min(1, mag));
      setTwoToneMag(v);
      setTwoTone({ enabled: twoToneOn, mag: v }).catch(() => {});
    },
    [twoToneOn, setTwoToneMag],
  );

  const calStateLabel = CAL_STATE_NAMES[psCalState] ?? `state ${psCalState}`;
  // Feedback level is 0..256 raw; UI shows 0..1 normalized for the dial.
  const feedbackPct = Math.max(0, Math.min(1, psFeedbackLevel / 256));
  const feedbackLen = feedbackPct * DIAL_C;
  const feedbackRound = Math.round(psFeedbackLevel);

  // Dial state visualization. WDSP info[14] (psCorrecting) reports "iqc
  // curve loaded + engaged" — true even when MOX is off, because the curve
  // persists between keying cycles. It also stays high after the operator
  // disables PS until the curve is explicitly cleared, so gate on psEnabled
  // too — otherwise the panel claims "correcting" with PS off.
  //   - "correcting" only when actively transmitting (curve applied to live RF)
  //   - "ready"      when curve is loaded but radio is idle
  const keyed = moxOn || tunOn || twoToneOn;
  const isCorrecting = psEnabled && psCorrecting && keyed;
  const isReady = psEnabled && psCorrecting && !keyed;
  const isRunning = psEnabled && !psCorrecting && psCalState > 0;
  const dialClass = isCorrecting || isReady ? 'is-converged' : '';
  const pillClass = isCorrecting || isReady ? 'ok' : isRunning ? 'run' : '';
  const pillText = isCorrecting
    ? 'CAL · correcting'
    : isReady
      ? 'CAL · ready'
      : isRunning
        ? 'CAL · running'
        : 'CAL · idle';

  const feedbackSubText = isCorrecting
    ? `${Math.round(feedbackPct * 100)} % · locked`
    : isReady
      ? `${Math.round(feedbackPct * 100)} % · curve loaded`
      : `${Math.round(feedbackPct * 100)} % · ${calStateLabel.toLowerCase()}`;

  // Elapsed timer. Mounts when the panel mounts; resets when the operator
  // clicks Run-now or Reset. Display is purely informational.
  const t0Ref = useRef<number>(Date.now());
  const [, setElapsedTick] = useState(0);
  useEffect(() => {
    const id = window.setInterval(() => setElapsedTick((n) => n + 1), 1000);
    return () => window.clearInterval(id);
  }, []);
  const elapsedMs = Date.now() - t0Ref.current;
  const elapsedLabel = formatElapsed(elapsedMs);

  // Correction sparkline — keep a short rolling history so the operator
  // can see whether the inverse-model dB number is jittering or stable.
  const sparkRef = useRef<number[]>([]);
  useEffect(() => {
    // Only record while actively predistorting RF — info[14] persists across
    // MOX cycles, so without the keyed gate the sparkline would fill with
    // stale post-key samples.
    if (!isCorrecting) return;
    sparkRef.current.push(psCorrectionDb);
    if (sparkRef.current.length > SPARK_LEN) sparkRef.current.shift();
  }, [psCorrectionDb, isCorrecting]);

  const sparkPoints = buildSparkline(sparkRef.current);

  // Peak meters share the same x-scale: 0..max(HW peak * 1.4, observed).
  // calcc only sets scOK (= curve loaded) when bins 0..3 of the envelope
  // histogram are full, which requires observed ∈ [0.80·hw_peak, hw_peak].
  // Below that the top bins starve → PS sits in COLLECT forever;
  // above hw_peak the envelope clips into bin 15.
  const meterScale = Math.max(psHwPeak * 1.4, psMaxTxEnvelope * 1.05, 0.001);
  const obsPct = Math.min(100, (psMaxTxEnvelope / meterScale) * 100);
  const hwRefPct = Math.min(100, (psHwPeak / meterScale) * 100);
  const hwSelfPct = Math.min(100, (psHwPeak / meterScale) * 100);
  const obsClipping = psMaxTxEnvelope > psHwPeak;
  const obsStarved = psMaxTxEnvelope > 0 && psMaxTxEnvelope < psHwPeak * 0.80;
  const obsClass = obsClipping ? 'bad' : obsStarved ? 'warn' : '';
  const obsHint = obsClipping
    ? 'HW peak too low — Observed is clipping into the ADC ceiling.'
    : obsStarved
      ? 'HW peak too high — Observed needs to reach ≥ 90 % of it for the fit to converge.'
      : 'Live feedback amplitude · should sit in the 90–100 % band of HW peak.';

  // Signal-flow node states: TX is always "on" when psEnabled; remaining
  // nodes light up when calibration is actually running so the operator
  // sees the path go live during MOX.
  const flowActive = psEnabled && (isRunning || isCorrecting || isReady);

  return (
    <div className="ps-shell">

      {/* ── Calibration hero ─────────────────────────────── */}
      <section>
        <div className="ps-section-head">
          <h3>Calibration</h3>
          <p>
            Live linearization adapts the predistorter to your PA. Key down
            (MOX/TUN) to begin sampling.
          </p>
          <span className="ps-elapsed">{elapsedLabel}</span>
        </div>

        <div className="ps-hero">

          {/* Dial */}
          <div className="ps-dial-col">
            <div className={`ps-dial ${dialClass}`}>
              <svg viewBox="0 0 200 200">
                <circle
                  className="ps-dial-track"
                  cx="100"
                  cy="100"
                  r={DIAL_R}
                />
                <circle
                  className="ps-dial-fill"
                  cx="100"
                  cy="100"
                  r={DIAL_R}
                  style={{
                    strokeDasharray: `${feedbackLen} ${DIAL_C - feedbackLen}`,
                  }}
                />
              </svg>
              <div className="ps-dial-center">
                <div className="ps-dial-state">{calStateLabel}</div>
                <div className="ps-dial-num">
                  {feedbackRound}
                  <small>/256</small>
                </div>
                <div className="ps-dial-sub">{feedbackSubText}</div>
              </div>
            </div>
            <span className={`ps-pill ${pillClass}`}>
              <span className="ps-pdot" />
              <span>{pillText}</span>
            </span>
          </div>

          {/* Signal flow */}
          <div className="ps-flow-col">
            <div className="ps-flow-title">Signal Flow</div>
            <div className="ps-flow">
              <FlowNode
                label="TX"
                sub={
                  psMaxTxEnvelope > 0
                    ? `${(20 * Math.log10(psMaxTxEnvelope / Math.max(psHwPeak, 0.0001))).toFixed(1)} dBFS`
                    : 'idle'
                }
                active={psEnabled}
              >
                <svg className="ps-ic" viewBox="0 0 16 16">
                  <path d="M3 12l3-3M6 9l3-3M9 6l3-3" />
                  <circle cx="3" cy="13" r="1.4" />
                  <circle cx="13" cy="3" r="1.4" />
                </svg>
              </FlowNode>
              <div className={`ps-arr ${flowActive ? 'live' : ''}`} />
              <FlowNode label="PA" sub="amplifier" active={flowActive}>
                <svg className="ps-ic" viewBox="0 0 16 16">
                  <rect x="2.5" y="5" width="11" height="6" rx="1.2" />
                  <path d="M5 5V3M11 5V3M5 13v-2M11 13v-2" />
                </svg>
              </FlowNode>
              <div className={`ps-arr ${flowActive ? 'live' : ''}`} />
              <FlowNode
                label={
                  psFeedbackSourceState === 'external' ? 'EXT COUPLER' : 'COUPLER'
                }
                sub={psFeedbackSourceState === 'external' ? 'bypass' : 'internal'}
                active={flowActive}
              >
                <svg className="ps-ic" viewBox="0 0 16 16">
                  <path d="M2 10l4-6 2 4 2-2 4 4" />
                  <path d="M2 13h12" />
                </svg>
              </FlowNode>
              <div className={`ps-arr ${flowActive ? 'live' : ''}`} />
              <FlowNode label="FEEDBACK" sub="RX path" active={flowActive}>
                <svg className="ps-ic" viewBox="0 0 16 16">
                  <path d="M2 8h3l1.5-3 3 6 1.5-3H14" />
                </svg>
              </FlowNode>
            </div>
            <div className="ps-source-tabs">
              <button
                type="button"
                className={`ps-source-tab ${psFeedbackSourceState === 'internal' ? 'is-active' : ''}`}
                onClick={() => onFeedbackSourceChange('internal')}
              >
                <span className="ps-rd" />
                <span className="ps-rd-label">
                  <strong>Internal coupler</strong>
                  <em>Use the radio's built-in directional coupler</em>
                </span>
              </button>
              <button
                type="button"
                className={`ps-source-tab ${psFeedbackSourceState === 'external' ? 'is-active' : ''}`}
                onClick={() => onFeedbackSourceChange('external')}
              >
                <span className="ps-rd" />
                <span className="ps-rd-label">
                  <strong>External (bypass)</strong>
                  <em>External coupler on RX2 antenna jack</em>
                </span>
              </button>
            </div>
          </div>

          {/* Peaks */}
          <div className="ps-peaks-col">
            <div className={`ps-peak ${obsClass}`}>
              <div className="ps-peak-row">
                <span className="ps-peak-nm">Observed peak</span>
                <span className="ps-peak-val">{psMaxTxEnvelope.toFixed(4)}</span>
              </div>
              <div className="ps-meter">
                <div className="ps-mfill" style={{ width: `${obsPct}%` }} />
                <div className="ps-ref" style={{ left: `${hwRefPct}%` }} />
              </div>
              <div className="ps-help">{obsHint}</div>
            </div>

            <div className="ps-peak">
              <div className="ps-peak-row">
                <span className="ps-peak-nm">HW peak</span>
                <span className="ps-peak-val">{psHwPeak.toFixed(4)}</span>
              </div>
              <div className="ps-meter">
                <div className="ps-mfill" style={{ width: `${hwSelfPct}%` }} />
              </div>
              <div className="ps-help">
                ADC clip threshold for the feedback path.
              </div>
            </div>

            <div className="ps-peak">
              <div className="ps-peak-row">
                <span className="ps-peak-nm">Correction</span>
                <span className="ps-peak-val">
                  {isCorrecting ? `+${psCorrectionDb.toFixed(2)}` : '—'}
                  {isCorrecting ? <small>dB</small> : null}
                </span>
              </div>
              <div className="ps-spark">
                <svg viewBox="0 0 200 14" preserveAspectRatio="none">
                  <polyline
                    fill="none"
                    stroke="var(--accent)"
                    strokeWidth="1.4"
                    points={sparkPoints}
                  />
                </svg>
              </div>
              <div className="ps-help">
                Last samples · stability of the inverse model.
              </div>
            </div>
          </div>
        </div>

        {/* Mode + actions */}
        <div className="ps-mode-row">
          <button
            type="button"
            className={`ps-mode-card ${psAuto && !psSingle ? 'is-active' : ''}`}
            onClick={() => setMode(true, false)}
          >
            <span className="ps-rd" />
            <div className="ps-mode-info">
              <h4>Auto</h4>
              <p>Continuously refine while transmitting. Recommended.</p>
            </div>
          </button>
          <button
            type="button"
            className={`ps-mode-card ${psSingle ? 'is-active' : ''}`}
            onClick={() => setMode(false, true)}
          >
            <span className="ps-rd" />
            <div className="ps-mode-info">
              <h4>Single</h4>
              <p>Run one calibration pass and lock the result.</p>
            </div>
          </button>
          <div className="ps-action-stack">
            <button
              type="button"
              className="ps-btn primary"
              onClick={onRunNow}
              title="Trigger a single calibration pass"
            >
              <svg className="ps-ic-sm" viewBox="0 0 12 12">
                <path d="M3 2l7 4-7 4z" fill="currentColor" stroke="none" />
              </svg>
              Run now
            </button>
            <button
              type="button"
              className="ps-btn danger"
              onClick={() => {
                t0Ref.current = Date.now();
                sparkRef.current = [];
                onReset();
              }}
              title="Reset PS state"
            >
              <svg className="ps-ic-sm" viewBox="0 0 12 12">
                <path d="M3 2v3h3M3 5a4 4 0 1 1-1 3" />
              </svg>
              Reset
            </button>
          </div>
        </div>
      </section>

      {/* ── Lower grid: Timing + Hardware ─────────────────── */}
      <div className="ps-grid2">

        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <circle cx="6" cy="6" r="4.5" />
              <path d="M6 3.5V6l1.5 1" />
            </svg>
            Timing
            <span className="ps-card-hint">defaults assume your radio</span>
          </h4>

          <FieldRow
            label="TX delay"
            help="Wait after key-down before RF (external amp T/R relay). 0 for CW/digital. Issue #630."
          >
            <NumberInput
              value={txMoxPreKeyDelayMs}
              min={0}
              max={500}
              step={5}
              unit="ms"
              onChange={(v) => {
                // Optimistic local update; reconcile with the server-applied
                // value, which may be clamped below the PS MOX hold-off below.
                setTxMoxPreKeyDelayMsLocal(v);
                setTxPreKeyDelay(v)
                  .then((r) => setTxMoxPreKeyDelayMsLocal(r.txMoxPreKeyDelayMs))
                  .catch(() => {});
              }}
            />
          </FieldRow>

          <FieldRow
            label="MOX delay"
            help="Hold-off after key-down before sampling"
          >
            <NumberInput
              value={psMoxDelaySec}
              min={0.0}
              max={10.0}
              step={0.1}
              unit="s"
              onChange={(v) => {
                setPsMoxDelaySec(v);
                pushAdvanced({ moxDelaySec: v });
              }}
            />
          </FieldRow>

          <FieldRow
            label="Cal delay"
            help="Time between calibration iterations"
          >
            <NumberInput
              value={psLoopDelaySec}
              min={0.0}
              max={100.0}
              step={0.5}
              unit="s"
              onChange={(v) => {
                setPsLoopDelaySec(v);
                pushAdvanced({ loopDelaySec: v });
              }}
            />
          </FieldRow>

          <FieldRow
            label="Amp delay"
            help="RF propagation through the PA chain"
          >
            <NumberInput
              value={psAmpDelayNs}
              min={0}
              max={25_000_000}
              step={50}
              unit="ns"
              onChange={(v) => {
                setPsAmpDelayNs(v);
                pushAdvanced({ ampDelayNs: v });
              }}
            />
          </FieldRow>
        </div>

        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <rect x="2" y="4" width="8" height="4" rx="1" />
              <path d="M4 4V2M8 4V2M4 10V8M8 10V8" />
            </svg>
            Hardware
            <span className="ps-card-hint">advanced — most users won't change</span>
          </h4>

          {psCalibrationStalled ? (
            <div
              className="ps-stall-banner"
              role="status"
              aria-live="polite"
              title="PureSignal has been keyed for more than 5 seconds without completing a calibration fit. This almost always means HW peak is set higher than the actual TX envelope peak — calcc bin 15 never fills."
            >
              ⚠ PS not converging — set HW peak just above your observed TX peak (~5%).
            </div>
          ) : null}

          <FieldRow
            label="HW peak"
            help="Override coupler ADC ceiling"
          >
            <span className="ps-hwpeak-cell">
              <NumberInput
                value={psHwPeak}
                min={0.01}
                max={2.0}
                step={0.001}
                onChange={(v) => {
                  setPsHwPeak(v);
                  pushAdvanced({ hwPeak: v });
                }}
                onCommit={(v) => {
                  setPsHwPeak(v);
                  pushAdvanced({ hwPeak: v });
                }}
              />
              {psHwPeak !== psHwPeakDefault ? (
                <span
                  className="ps-diff-mark"
                  aria-label="HW peak differs from per-board default"
                  title={`Differs from default ${psHwPeakDefault.toFixed(4)}`}
                >
                  *
                </span>
              ) : null}
              <button
                type="button"
                className="ps-default-btn"
                disabled={psHwPeak === psHwPeakDefault}
                title={`Reset HW peak to per-board default ${psHwPeakDefault.toFixed(4)}`}
                onClick={() => {
                  setPsHwPeak(psHwPeakDefault);
                  pushAdvanced({ hwPeak: psHwPeakDefault });
                }}
              >
                Default
              </button>
            </span>
          </FieldRow>

          <FieldRow
            label="Feedback atten"
            help="Feedback attenuation (dB). Manual alternative to Auto-attenuate — dial it so Observed sits mid-scale, then it persists per band."
          >
            <NumberInput
              value={psTxFeedbackAttenuationDb}
              min={psTxFeedbackAttenuationDbMin}
              max={31}
              step={1}
              disabled={psAutoAttenuate}
              onChange={(v) => {
                setPsTxFeedbackAttenuationDb(v);
                setPsFeedbackAttenuation(v).catch(() => {});
              }}
              onCommit={(v) => {
                setPsTxFeedbackAttenuationDb(v);
                setPsFeedbackAttenuation(v).catch(() => {});
              }}
            />
          </FieldRow>

          <FieldRow
            label="Ints / Spi"
            help="Iterations per spectral pass"
          >
            <select
              className="ps-select-mini"
              value={psIntsSpiPreset}
              onChange={(e) => {
                const v = e.target.value;
                setPsIntsSpiPreset(v);
                pushAdvanced({ intsSpiPreset: v });
              }}
            >
              <option value="16/256">16 / 256</option>
              <option value="8/512">8 / 512</option>
              <option value="4/1024">4 / 1024</option>
            </select>
          </FieldRow>

          <FieldRow
            label="Auto-attenuate"
            help="Drop drive when feedback clips"
          >
            <Checkbox
              checked={psAutoAttenuate}
              onChange={(v) => {
                setPsAutoAttenuate(v);
                pushAdvanced({ autoAttenuate: v });
              }}
              label={psAutoAttenuate ? 'Enabled' : 'Disabled'}
            />
          </FieldRow>

          <FieldRow
            label="Relax phase tolerance"
            help="Allow looser convergence on weak PAs"
          >
            <Checkbox
              checked={psPtol}
              onChange={(v) => {
                setPsPtol(v);
                pushAdvanced({ ptol: v });
              }}
              label={psPtol ? 'Enabled' : 'Disabled'}
            />
          </FieldRow>
        </div>
      </div>

      {/* ── Status (last cal) ─────────────────────────────── */}
      <div className="ps-status-row">
        <div className="ps-status-left">
          <span>Calibration</span>
          {isCorrecting ? (
            <span className="saved">
              {`converged · ${psCorrectionDb.toFixed(2)} dB`}
            </span>
          ) : isReady ? (
            <span className="saved">curve loaded · idle</span>
          ) : (
            <span>{calStateLabel}</span>
          )}
        </div>
      </div>

      {/* ── Display: PS Monitor (hidden on HL2) ──────────── */}
      {psMonitorSupported ? (
        <div className="ps-card">
          <h4>
            <svg className="ps-ic-sm" viewBox="0 0 12 12">
              <rect x="1.5" y="2.5" width="9" height="6" rx="0.8" />
              <path d="M4 10h4M6 8.5v1.5" />
            </svg>
            Display
          </h4>
          <FieldRow
            label="Monitor PA output"
            help="Show post-correction signal in TX panadapter"
          >
            <Checkbox
              checked={psMonitorEnabled}
              onChange={onPsMonitorToggle}
              label={psMonitorEnabled ? 'Enabled' : 'Disabled'}
            />
          </FieldRow>
        </div>
      ) : null}

      {/* ── Two-tone test signal ─────────────────────────── */}
      <div className="ps-card">
        <h4>
          <svg className="ps-ic-sm" viewBox="0 0 12 12">
            <path d="M1.5 6c1-3 3-3 3 0s2 3 3 0 2-3 3 0" />
          </svg>
          Two-tone test signal
        </h4>

        <FieldRow
          label="Generator"
          help="Standard PureSignal calibration excitation"
        >
          <button
            type="button"
            className={`ps-btn ${twoToneOn ? 'primary' : ''}`}
            onClick={onTwoToneToggle}
          >
            {twoToneOn ? '2-Tone ON' : '2-Tone OFF'}
          </button>
        </FieldRow>

        <FieldRow label="Freq 1" help="Lower test tone">
          <NumberInput
            value={twoToneFreq1}
            min={50}
            max={5000}
            step={10}
            unit="Hz"
            onChange={onTwoToneFreq1Change}
          />
        </FieldRow>

        <FieldRow label="Freq 2" help="Upper test tone">
          <NumberInput
            value={twoToneFreq2}
            min={50}
            max={5000}
            step={10}
            unit="Hz"
            onChange={onTwoToneFreq2Change}
          />
        </FieldRow>

        <FieldRow label="Magnitude" help="Per-tone amplitude (0..1)">
          <NumberInput
            value={twoToneMag}
            min={0}
            max={1}
            step={0.01}
            onChange={onTwoToneMagChange}
          />
        </FieldRow>
      </div>
    </div>
  );
}

function FlowNode({
  label,
  sub,
  active,
  children,
}: {
  label: string;
  sub: string;
  active: boolean;
  children: React.ReactNode;
}) {
  return (
    <div className={`ps-node ${active ? 'active' : 'dim'}`}>
      <div className="ps-ico">{children}</div>
      <div className="ps-nm">{label}</div>
      <div className="ps-sub">{sub}</div>
    </div>
  );
}

function FieldRow({
  label,
  help,
  children,
}: {
  label: string;
  help?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="ps-field">
      <div className="ps-name">
        {label}
        {help ? <em>{help}</em> : null}
      </div>
      <div>{children}</div>
    </div>
  );
}

function NumberInput({
  value,
  min,
  max,
  step,
  unit,
  onChange,
  onCommit,
  disabled,
}: {
  value: number;
  min: number;
  max: number;
  step: number;
  unit?: string;
  onChange: (v: number) => void;
  // mi0bot ref: PSForm.cs PSpeak_TextChanged — onCommit fires on blur and
  // Enter unconditionally so re-entering the same value still re-pushes,
  // mirroring WinForms TextChanged-on-every-keystroke semantics that React's
  // controlled-input dedup otherwise hides on focus/blur with no edit.
  onCommit?: (v: number) => void;
  disabled?: boolean;
}) {
  return (
    <span className="ps-ninput">
      <input
        type="number"
        className={unit ? '' : 'no-unit'}
        value={value}
        min={min}
        max={max}
        step={step}
        disabled={disabled}
        onChange={(e) => {
          const v = Number(e.target.value);
          if (Number.isFinite(v)) onChange(v);
        }}
        onBlur={(e) => {
          if (!onCommit) return;
          const v = Number(e.target.value);
          if (!Number.isFinite(v)) return;
          const normalized = String(v);
          if (normalized !== e.target.value) e.target.value = normalized;
          onCommit(v);
        }}
        onKeyDown={(e) => {
          if (!onCommit || e.key !== 'Enter') return;
          const v = Number((e.target as HTMLInputElement).value);
          if (Number.isFinite(v)) onCommit(v);
        }}
      />
      {unit ? <span className="ps-unit">{unit}</span> : null}
    </span>
  );
}

function Checkbox({
  checked,
  onChange,
  label,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  label: string;
}) {
  return (
    <label className="ps-check">
      <input
        type="checkbox"
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="ps-check-box" />
      <span>{label}</span>
    </label>
  );
}

function formatElapsed(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  const m = Math.floor(s / 60);
  const r = s % 60;
  return `Elapsed ${m}:${String(r).padStart(2, '0')}`;
}

function buildSparkline(history: number[]): string {
  if (history.length === 0) return '';
  const n = SPARK_LEN;
  // Auto-scale so the trace stays visible regardless of dB magnitude.
  const min = Math.min(...history, 0);
  const max = Math.max(...history, min + 0.5);
  const range = Math.max(0.1, max - min);
  return history
    .map((v, i) => {
      const x = (i / (n - 1)) * 200;
      const y = 13 - ((v - min) / range) * 11 - 1;
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');
}
