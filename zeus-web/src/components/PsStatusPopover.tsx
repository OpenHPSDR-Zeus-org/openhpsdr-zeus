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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useTxStore } from '../state/tx-store';

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

// Dial geometry — must match the SVG circle below.
const DIAL_R = 36;
const DIAL_C = 2 * Math.PI * DIAL_R;

/**
 * Live PureSignal status popover. Anchored above the transport-bar PS
 * button, surfaces the same telemetry as the Settings → PURESIGNAL hero
 * (convergence dial, cal state, observed/HW peaks, correction dB) but
 * compact enough to scan in a glance during MOX without leaving the
 * panadapter view.
 */
export function PsStatusPopover() {
  const psEnabled = useTxStore((s) => s.psEnabled);
  const psFeedbackLevel = useTxStore((s) => s.psFeedbackLevel);
  const psCalState = useTxStore((s) => s.psCalState);
  const psCorrecting = useTxStore((s) => s.psCorrecting);
  const psCorrectionDb = useTxStore((s) => s.psCorrectionDb);
  const psMaxTxEnvelope = useTxStore((s) => s.psMaxTxEnvelope);
  const psHwPeak = useTxStore((s) => s.psHwPeak);
  const psAuto = useTxStore((s) => s.psAuto);
  const psSingle = useTxStore((s) => s.psSingle);
  // Gate the "correcting" badge on actual TX being keyed. WDSP info[14]
  // (psCorrecting) reports "iqc curve loaded" — it stays high across
  // MOX-down once cal succeeds, because the curve persists between keying
  // cycles. Same gate as PsSettingsPanel.tsx so the popover and the panel
  // agree.
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const twoToneOn = useTxStore((s) => s.twoToneOn);

  const calStateLabel = CAL_STATE_NAMES[psCalState] ?? `state ${psCalState}`;
  const feedbackPct = Math.max(0, Math.min(1, psFeedbackLevel / 256));
  const dialFillLen = feedbackPct * DIAL_C;
  const feedbackRound = Math.round(psFeedbackLevel);

  const keyed = moxOn || tunOn || twoToneOn;
  const isCorrecting = psEnabled && psCorrecting && keyed;
  const isReady = psEnabled && psCorrecting && !keyed;
  const isRunning = psEnabled && !psCorrecting && psCalState > 0;
  const dialClass = isCorrecting || isReady ? 'is-converged' : '';
  const armedLabel = !psEnabled
    ? 'IDLE'
    : isCorrecting
      ? 'CORRECTING'
      : isReady
        ? 'READY'
        : isRunning
          ? 'RUNNING'
          : 'ARMED';

  // Peak meter scale — same logic as the Settings hero so values map 1:1
  // between the two views. calcc needs observed ∈ [0.80·hw_peak, hw_peak]
  // for scOK (top bins filled, no clip); outside that the fit stalls.
  const meterScale = Math.max(psHwPeak * 1.4, psMaxTxEnvelope * 1.05, 0.001);
  const obsPct = Math.min(100, (psMaxTxEnvelope / meterScale) * 100);
  const refPct = Math.min(100, (psHwPeak / meterScale) * 100);
  const obsClipping = psMaxTxEnvelope > psHwPeak;
  const obsStarved = psMaxTxEnvelope > 0 && psMaxTxEnvelope < psHwPeak * 0.80;
  const obsClass = obsClipping ? 'bad' : obsStarved ? 'warn' : '';
  const obsHint = obsClipping
    ? 'HW peak too low — Observed clipping.'
    : obsStarved
      ? 'HW peak too high — Observed needs ≥ 90 %.'
      : null;

  const modeLabel = psSingle ? 'Single' : psAuto ? 'Auto' : 'Manual';

  return (
    <div className="ps-popover" role="status" aria-live="polite">
      <div className="ps-popover-head">
        <span className="ps-popover-title">PureSignal</span>
        <span className={`ps-popover-state ${psEnabled ? 'on' : 'off'}`}>
          {armedLabel}
        </span>
      </div>

      <div className="ps-popover-body">
        <div className={`ps-popover-dial ${dialClass}`}>
          <svg viewBox="0 0 88 88">
            <circle
              className="ps-dial-track"
              cx="44"
              cy="44"
              r={DIAL_R}
              transform="rotate(-90 44 44)"
            />
            <circle
              className="ps-dial-fill"
              cx="44"
              cy="44"
              r={DIAL_R}
              transform="rotate(-90 44 44)"
              style={{
                strokeDasharray: `${dialFillLen} ${DIAL_C - dialFillLen}`,
              }}
            />
          </svg>
          <div className="ps-popover-dial-center">
            <div className="ps-popover-dial-num">{feedbackRound}</div>
            <div className="ps-popover-dial-sub">/ 256</div>
          </div>
        </div>

        <dl className="ps-popover-rows">
          <div className="ps-popover-row">
            <dt>State</dt>
            <dd className="mono">{calStateLabel}</dd>
          </div>
          <div className="ps-popover-row">
            <dt>Mode</dt>
            <dd>{modeLabel}</dd>
          </div>
          <div className="ps-popover-row">
            <dt>Correction</dt>
            <dd className="mono">
              {psEnabled && psCorrecting ? `+${psCorrectionDb.toFixed(2)} dB` : '—'}
            </dd>
          </div>
        </dl>
      </div>

      <div className={`ps-popover-peak ${obsClass}`}>
        <div className="ps-popover-peak-row">
          <span>Observed</span>
          <span className="mono">{psMaxTxEnvelope.toFixed(4)}</span>
        </div>
        <div className="ps-popover-meter">
          <div className="ps-popover-mfill" style={{ width: `${obsPct}%` }} />
          <div className="ps-popover-ref" style={{ left: `${refPct}%` }} />
        </div>
        <div className="ps-popover-peak-row sub">
          <span>HW peak</span>
          <span className="mono">{psHwPeak.toFixed(4)}</span>
        </div>
        {obsHint ? <div className="ps-popover-peak-hint">{obsHint}</div> : null}
      </div>

      <div className="ps-popover-foot">
        Click PS to {psEnabled ? 'disarm' : 'arm'} · open Settings → PURESIGNAL
        for full controls
      </div>
    </div>
  );
}
