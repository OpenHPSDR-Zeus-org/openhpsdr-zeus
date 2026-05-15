// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Per-radio frequency-calibration store (issue #325). Mirrors the
// /api/radio/frequency-calibration surface:
//
//   GET                                 → current persisted factor + ppm
//   POST /calibrate                     → run Thetis-style WWV auto-cal
//   POST /reset                         → set factor back to 1.0
//
// The auto-cal procedure on the backend snapshots state, tunes to
// 10 MHz USB, finds the spectral peak, computes the correction factor,
// and restores state — operator types nothing.

import { create } from 'zustand';

export type CalibrationOutcome =
  | 'Success'
  | 'Busy'
  | 'NotConnected'
  | 'CaptureFailed'
  | 'NoSignal'
  | 'OffsetOutOfRange';

export interface CalibrationState {
  factor: number;
  ppm: number;
  offsetHzAt10MHz: number;
}

export interface CalibrationResult {
  outcome: CalibrationOutcome;
  offsetHz: number | null;
  peakDb: number | null;
  appliedFactor: number | null;
  message: string;
}

const DEFAULT_STATE: CalibrationState = {
  factor: 1.0,
  ppm: 0,
  offsetHzAt10MHz: 0,
};

function parseState(raw: unknown): CalibrationState {
  if (!raw || typeof raw !== 'object') return DEFAULT_STATE;
  const r = raw as Record<string, unknown>;
  return {
    factor: typeof r.factor === 'number' ? r.factor : 1.0,
    ppm: typeof r.ppm === 'number' ? r.ppm : 0,
    offsetHzAt10MHz:
      typeof r.offsetHzAt10MHz === 'number' ? r.offsetHzAt10MHz : 0,
  };
}

function parseResult(raw: unknown): CalibrationResult {
  const fallback: CalibrationResult = {
    outcome: 'CaptureFailed',
    offsetHz: null,
    peakDb: null,
    appliedFactor: null,
    message: 'Unexpected response from server.',
  };
  if (!raw || typeof raw !== 'object') return fallback;
  const r = raw as Record<string, unknown>;
  return {
    outcome: (r.outcome as CalibrationOutcome) ?? fallback.outcome,
    offsetHz: typeof r.offsetHz === 'number' ? r.offsetHz : null,
    peakDb: typeof r.peakDb === 'number' ? r.peakDb : null,
    appliedFactor:
      typeof r.appliedFactor === 'number' ? r.appliedFactor : null,
    message: typeof r.message === 'string' ? r.message : fallback.message,
  };
}

type FrequencyCalibrationStore = {
  state: CalibrationState;
  loaded: boolean;
  inflight: boolean;
  lastResult: CalibrationResult | null;
  error: string | null;
  load: () => Promise<void>;
  calibrate: () => Promise<void>;
  reset: () => Promise<void>;
};

export const useFrequencyCalibrationStore = create<FrequencyCalibrationStore>(
  (set) => ({
    state: DEFAULT_STATE,
    loaded: false,
    inflight: false,
    lastResult: null,
    error: null,

    load: async () => {
      set({ inflight: true, error: null });
      try {
        const res = await fetch('/api/radio/frequency-calibration');
        if (!res.ok) throw new Error(`GET → ${res.status}`);
        set({
          state: parseState(await res.json()),
          loaded: true,
          inflight: false,
        });
      } catch (err) {
        set({
          error: err instanceof Error ? err.message : String(err),
          inflight: false,
        });
      }
    },

    calibrate: async () => {
      set({ inflight: true, error: null, lastResult: null });
      try {
        const res = await fetch(
          '/api/radio/frequency-calibration/calibrate',
          { method: 'POST' },
        );
        if (!res.ok) throw new Error(`POST /calibrate → ${res.status}`);
        const result = parseResult(await res.json());
        set({ lastResult: result, inflight: false });
        // Re-load the current factor regardless of outcome — Success
        // moved it, the failure paths left it alone, but either way the
        // UI displays the canonical persisted value next.
        const stateRes = await fetch('/api/radio/frequency-calibration');
        if (stateRes.ok) set({ state: parseState(await stateRes.json()) });
      } catch (err) {
        set({
          error: err instanceof Error ? err.message : String(err),
          inflight: false,
        });
      }
    },

    reset: async () => {
      set({ inflight: true, error: null });
      try {
        const res = await fetch('/api/radio/frequency-calibration/reset', {
          method: 'POST',
        });
        if (!res.ok) throw new Error(`POST /reset → ${res.status}`);
        set({
          state: parseState(await res.json()),
          lastResult: null,
          inflight: false,
        });
      } catch (err) {
        set({
          error: err instanceof Error ? err.message : String(err),
          inflight: false,
        });
      }
    },
  }),
);
