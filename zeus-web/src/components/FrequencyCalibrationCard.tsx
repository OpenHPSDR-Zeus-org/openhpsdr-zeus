// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Per-radio frequency calibration card (issue #325). One-button auto-cal
// modelled on Thetis's WWV procedure (console.cs:9779-9854):
//   - Operator clicks "Calibrate"
//   - Backend snapshots state, tunes to WWV 10 MHz, finds the spectral
//     peak, computes the correction factor, and restores operator state
//   - UI displays the resulting offset / ppm
//
// No manual entry — matches the Thetis / piHPSDR / deskHPSDR model
// where the user never types a calibration number, the radio measures
// the drift against a known reference.
//
// Visual idiom borrowed from the surrounding RadioOptionsPanel `.ps-card`
// so this reads as the same surface family.

import { useEffect } from 'react';
import { useFrequencyCalibrationStore } from '../state/frequency-calibration-store';
import { useConnectionStore } from '../state/connection-store';

function formatPpm(ppm: number): string {
  if (Math.abs(ppm) < 0.001) return '0.000 ppm';
  return `${ppm >= 0 ? '+' : ''}${ppm.toFixed(3)} ppm`;
}

function formatOffsetHz(hz: number): string {
  if (Math.abs(hz) < 0.05) return '0.0 Hz';
  return `${hz >= 0 ? '+' : ''}${hz.toFixed(1)} Hz`;
}

export function FrequencyCalibrationCard() {
  const state = useFrequencyCalibrationStore((s) => s.state);
  const loaded = useFrequencyCalibrationStore((s) => s.loaded);
  const inflight = useFrequencyCalibrationStore((s) => s.inflight);
  const lastResult = useFrequencyCalibrationStore((s) => s.lastResult);
  const error = useFrequencyCalibrationStore((s) => s.error);
  const load = useFrequencyCalibrationStore((s) => s.load);
  const calibrate = useFrequencyCalibrationStore((s) => s.calibrate);
  const reset = useFrequencyCalibrationStore((s) => s.reset);

  const connected = useConnectionStore((s) => s.status === 'Connected');

  useEffect(() => {
    load();
  }, [load]);

  const isCalibrated = Math.abs(state.factor - 1.0) > 1e-9;
  const buttonLabel = inflight
    ? 'CALIBRATING…'
    : isCalibrated
      ? 'RE-CALIBRATE'
      : 'CALIBRATE';

  const resultTone =
    lastResult?.outcome === 'Success'
      ? 'success'
      : lastResult
        ? 'warn'
        : null;

  return (
    <div className="ps-card">
      <h4>
        <svg className="ps-ic-sm" viewBox="0 0 12 12">
          <circle cx="6" cy="6" r="4" fill="none" />
          <path d="M6 2v1M6 9v1M2 6h1M9 6h1" />
          <circle cx="6" cy="6" r="1" />
        </svg>
        Frequency Calibration
        <span className="ps-card-hint">
          per-radio crystal-drift correction
        </span>
      </h4>

      <div className="ps-field" style={{ display: 'block' }}>
        <div className="ps-name" style={{ marginBottom: 8 }}>
          Auto-calibrate against WWV 10 MHz
          <em>
            Click <strong>Calibrate</strong>. Zeus tunes the radio to WWV on
            10.000 MHz (US standard frequency, broadcast 24/7 from Fort
            Collins, Colorado), measures any offset on the panadapter, and
            applies a correction factor so the dial matches the actual
            transmitted frequency. Your VFO, mode, filter, and zoom are
            restored automatically when the procedure finishes.
          </em>
        </div>

        <div
          className="freqcal-current"
          style={{
            display: 'flex',
            alignItems: 'baseline',
            justifyContent: 'space-between',
            padding: '8px 10px',
            background: 'var(--panel-bot)',
            border: '1px solid var(--bevel-dark)',
            borderRadius: 4,
            marginBottom: 10,
            fontFamily: '"Archivo Narrow", system-ui, sans-serif',
          }}
        >
          <span style={{ opacity: 0.75 }}>Current correction</span>
          <span style={{ color: isCalibrated ? 'var(--accent)' : 'inherit' }}>
            {!loaded
              ? '…'
              : isCalibrated
                ? `${formatOffsetHz(state.offsetHzAt10MHz)} @ 10 MHz  (${formatPpm(state.ppm)})`
                : 'Uncalibrated'}
          </span>
        </div>

        <div style={{ display: 'flex', gap: 8 }}>
          <button
            type="button"
            className="btn sm active"
            onClick={() => calibrate()}
            disabled={inflight || !connected}
            style={{ flex: 1 }}
            title={
              connected
                ? 'Tune to WWV 10 MHz, measure offset, apply correction'
                : 'Connect a radio first'
            }
          >
            {buttonLabel}
          </button>
          {isCalibrated && (
            <button
              type="button"
              className="btn sm"
              onClick={() => reset()}
              disabled={inflight}
              title="Clear the stored calibration (back to factory)"
            >
              RESET
            </button>
          )}
        </div>

        {!connected && (
          <div
            style={{
              marginTop: 8,
              fontSize: '0.85em',
              opacity: 0.7,
            }}
          >
            Connect a radio to enable calibration.
          </div>
        )}

        {lastResult && (
          <div
            className={`freqcal-result freqcal-result-${resultTone}`}
            style={{
              marginTop: 10,
              padding: '8px 10px',
              borderLeft: `3px solid ${resultTone === 'success' ? 'var(--accent)' : 'var(--tx)'}`,
              background: 'var(--panel-bot)',
              borderRadius: 2,
              fontSize: '0.9em',
            }}
          >
            {lastResult.message}
          </div>
        )}

        {error && (
          <div
            style={{
              marginTop: 8,
              color: 'var(--tx)',
              fontSize: '0.85em',
            }}
          >
            Error: {error}
          </div>
        )}
      </div>
    </div>
  );
}
