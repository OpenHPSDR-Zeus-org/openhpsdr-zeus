// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Wall-clock peak hold for the immersive meter widgets. Mirrors the
// prototype's peak/peakHold logic: a peak that bumps up immediately, holds
// for a window (default 1.5 s), then decays at 0.5 fraction-units / sec
// — measured in fractional [0..1] axis units, not dB, so the visual decay
// rate is the same on every meter regardless of its native dB range.

import { useRef } from 'react';
import { isSilent } from './dbScale';

const HOLD_MS_DEFAULT = 1500;
const DECAY_FRAC_PER_SEC_DEFAULT = 0.5;

interface PeakState {
  peak: number;     // last held fraction
  ts: number;       // ms (performance.now)
  holdUntil: number;
}

/** Pure-function variant — useful for SSR / tests. */
export function advancePeakHold(
  state: PeakState,
  currentFrac: number,
  nowMs: number,
  holdMs: number = HOLD_MS_DEFAULT,
  decayFracPerSec: number = DECAY_FRAC_PER_SEC_DEFAULT,
): PeakState {
  if (currentFrac > state.peak) {
    return { peak: currentFrac, ts: nowMs, holdUntil: nowMs + holdMs };
  }
  if (nowMs < state.holdUntil) {
    return { ...state, ts: nowMs };
  }
  const dtSec = state.ts === 0 ? 0 : Math.max(0, (nowMs - state.ts) / 1000);
  const decayed = Math.max(currentFrac, state.peak - decayFracPerSec * dtSec);
  return { peak: decayed, ts: nowMs, holdUntil: state.holdUntil };
}

/**
 * React hook returning the peak-hold value (in axis fractions [0..1]) for a
 * live `currentDb` feed. Resets to zero on the silence sentinel so a
 * post-MOX idle period doesn't leave a stale hold tick floating.
 */
export function usePeakHoldFrac(
  currentDb: number,
  dbToFracFn: (db: number) => number,
  holdMs: number = HOLD_MS_DEFAULT,
  decayFracPerSec: number = DECAY_FRAC_PER_SEC_DEFAULT,
): number {
  const ref = useRef<PeakState>({ peak: 0, ts: 0, holdUntil: 0 });
  if (isSilent(currentDb)) {
    ref.current = { peak: 0, ts: 0, holdUntil: 0 };
    return 0;
  }
  const now = typeof performance !== 'undefined' ? performance.now() : Date.now();
  ref.current = advancePeakHold(
    ref.current,
    dbToFracFn(currentDb),
    now,
    holdMs,
    decayFracPerSec,
  );
  return ref.current.peak;
}
