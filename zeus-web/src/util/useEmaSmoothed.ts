// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Exponential moving average smoother for meter readings. The wire frames
// arrive at ~10 Hz and the render loop ticks at ~30 Hz, so a raw value
// driven straight into a needle / bar visibly steps. A short-time-constant
// EMA (default 90 ms) makes the visual motion fluid without lagging
// noticeably behind voice dynamics on SSB.
//
// Pure ballistics: alpha = 1 - exp(-dt/tau), so a step input reaches
//   ~63 % in 1 × tau, ~95 % in 3 × tau, ~99 % in ~4.6 × tau.
//   tau =  90 ms  → ~270 ms to settle within 5 %.
//   tau = 150 ms  → ~450 ms.
// 90 ms is the sweet spot for SSB/CW: kills the 10 Hz steppiness without
// turning the meter into a slow integrator.
//
// Sentinel handling — WDSP / the meter pipeline emits ≤ -200 dBFS for
// "channel idle / bypassed". We pass that through verbatim and reset the
// smoother so the next live sample doesn't lerp out of the sentinel; the
// downstream widget renders an em-dash and we don't fight it.

import { useRef } from 'react';

const SILENT_SENTINEL = -200;

interface EmaState {
  smoothed: number;
  ts: number; // performance.now() of last update
}

/**
 * Smooth a scalar reading with an exponential moving average. Re-renders
 * every time the input changes (callers control the cadence — typically
 * a 30 Hz raf tick from useMeterRefresh).
 *
 * @param value  Latest sample from the store.
 * @param tauMs  Time constant in ms. 90 ms is the meter default; set 0 to
 *               bypass smoothing entirely.
 */
export function useEmaSmoothed(value: number, tauMs = 90): number {
  const state = useRef<EmaState>({ smoothed: NaN, ts: 0 });

  // Sentinel / non-finite — pass through and reset so the next live sample
  // seeds the smoother fresh instead of lerping out of -∞.
  if (!isFinite(value) || value <= SILENT_SENTINEL) {
    state.current = { smoothed: value, ts: 0 };
    return value;
  }

  // Bypass — caller asked for raw.
  if (tauMs <= 0) {
    state.current = { smoothed: value, ts: 0 };
    return value;
  }

  const now = typeof performance !== 'undefined' ? performance.now() : Date.now();
  const prev = state.current;

  // First valid sample — seed the smoother so the meter doesn't visibly
  // ramp from 0 on the first reading.
  if (!isFinite(prev.smoothed) || prev.ts === 0) {
    state.current = { smoothed: value, ts: now };
    return value;
  }

  const dt = Math.max(0, now - prev.ts);
  const alpha = 1 - Math.exp(-dt / tauMs);
  const smoothed = prev.smoothed + (value - prev.smoothed) * alpha;
  state.current = { smoothed, ts: now };
  return smoothed;
}
