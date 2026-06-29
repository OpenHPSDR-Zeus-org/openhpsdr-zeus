// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

export const SMETER_PEAK_HOLD_MS = 2000;
export const SMETER_PEAK_DECAY_MS = 400;
export const SMETER_PEAK_EPSILON = 0.0005;

export interface SMeterPeakHoldState {
  /** Peak marker position on the rendered 0..1 meter axis. */
  peak: number;
  /** Previous live meter fraction. Used to detect the edge leaving a peak. */
  lastFraction: number;
  /** Timestamp until which the current peak should remain latched. */
  holdUntilMs: number;
  /** Timestamp of the last state step, for post-hold decay. */
  lastTickMs: number;
}

function clampFraction(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(1, value));
}

export function initialSMeterPeakHoldState(
  fraction: number,
  nowMs = 0,
): SMeterPeakHoldState {
  const value = clampFraction(fraction);
  return {
    peak: value,
    lastFraction: value,
    holdUntilMs: nowMs + SMETER_PEAK_HOLD_MS,
    lastTickMs: nowMs,
  };
}

export function stepSMeterPeakHold(
  state: SMeterPeakHoldState,
  fraction: number,
  nowMs: number,
): SMeterPeakHoldState {
  const value = clampFraction(fraction);
  const currentPeak = clampFraction(state.peak);

  if (value >= currentPeak - SMETER_PEAK_EPSILON) {
    return {
      peak: value,
      lastFraction: value,
      holdUntilMs: nowMs + SMETER_PEAK_HOLD_MS,
      lastTickMs: nowMs,
    };
  }

  const holdUntilMs =
    state.lastFraction >= currentPeak - SMETER_PEAK_EPSILON
      ? nowMs + SMETER_PEAK_HOLD_MS
      : state.holdUntilMs;

  if (nowMs < holdUntilMs) {
    return {
      peak: currentPeak,
      lastFraction: value,
      holdUntilMs,
      lastTickMs: nowMs,
    };
  }

  const decayStartMs = Math.max(state.lastTickMs, holdUntilMs);
  const dtMs = Math.max(0, nowMs - decayStartMs);
  const nextPeak = Math.max(value, currentPeak - dtMs / SMETER_PEAK_DECAY_MS);

  return {
    peak: nextPeak,
    lastFraction: value,
    holdUntilMs,
    lastTickMs: nowMs,
  };
}
