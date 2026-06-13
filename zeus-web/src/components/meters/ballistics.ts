// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Pure meter ballistics primitives shared across every Zeus meter widget.
// Lives here (and not in analog-meter/) because both the analog S-meter dial
// and the meter-group / TX-stage / mic-bar widgets feed through the same
// pipeline — one implementation, no drift.
//
// Pipeline (applied per rAF tick by useBallisticReading):
//   raw sample → moving-average prefilter → asymmetric attack/decay RC
//   → peak-hold step (rises with value, decays slowly)
//
// Defaults match the analog S-meter dial verbatim. Values past the
// SILENT_SENTINEL (≤ -200 dBFS) mean "bypassed / idle" and bypass the
// smoother entirely — callers reset state and pass through.

export const METER_BALLISTICS_DEFAULTS = {
  /** Rise time constant (seconds). The analog S-meter dial uses 0.05 s
   *  (peak-grabby) because a wide moving-coil sweep reads that motion as
   *  expressive. On the tighter TX-stage / mic / meter-group bars and arcs
   *  the same rise rate looks jittery on SSB-voice power — every syllable
   *  peak jumps the bar visibly. 0.25 s is fast enough to feel live but
   *  smooths over per-syllable spikes; the slow decay below keeps the
   *  classic "needle falls back slowly" feel. */
  attackSec: 0.35,
  /** Fall time constant (seconds). Slow — like a moving-coil meter. */
  decaySec: 0.6,
  /** Pre-ballistic ring-buffer averager length (samples). The hook ticks
   *  at rAF rate, so 18 samples ≈ 300 ms at 60 Hz. The wire pushes meter
   *  frames at ~10 Hz (one every ~100 ms), so a 300 ms window blends
   *  ~3 wire frames before the ballistic sees them — enough to take the
   *  syllable-spike edge off HL2 fwdWatts without adding visible lag. */
  avgSamples: 18,
  /** Peak-hold decay rate, expressed as a fraction of the meter's axis
   *  span per second. 0.05 = the peak ghost loses 5 % of full scale each
   *  second — same rate the analog dial uses. */
  peakDecayFracPerSec: 0.05,
  /** ≤ this counts as "bypassed / idle" (WDSP convention) and skips all
   *  smoothing. */
  silentSentinel: -200,
} as const;

export function isSilentSample(value: number): boolean {
  return !Number.isFinite(value) || value <= METER_BALLISTICS_DEFAULTS.silentSentinel;
}

/** Asymmetric RC ballistic — different time constant for rising vs falling
 *  samples. Returns the new filtered value. */
export function ballisticsStep(
  prev: number,
  target: number,
  dt: number,
  attackSec: number = METER_BALLISTICS_DEFAULTS.attackSec,
  decaySec: number = METER_BALLISTICS_DEFAULTS.decaySec,
): number {
  const tau = target > prev ? attackSec : decaySec;
  if (tau <= 0.001) return target;
  const alpha = 1 - Math.exp(-dt / tau);
  return prev + (target - prev) * alpha;
}

/** Peak-hold step in raw-value space. Rises instantly with `value`, then
 *  decays at `decayFracPerSec × (axisMax - axisMin)` per second so the
 *  visual decay rate is the same regardless of unit. */
export function peakHoldStep(
  prevPeak: number,
  value: number,
  dt: number,
  axisMin: number,
  axisMax: number,
  decayFracPerSec: number = METER_BALLISTICS_DEFAULTS.peakDecayFracPerSec,
): number {
  if (value > prevPeak) return value;
  const span = Math.max(0, axisMax - axisMin);
  const decayPerSec = decayFracPerSec * span;
  return Math.max(value, prevPeak - decayPerSec * dt);
}

export interface Averager {
  /** Push a sample and return the current mean. */
  push(x: number): number;
  /** Resize the ring buffer, seeding the new buffer with the current mean
   *  so a UI control that changes the avg size doesn't pop. */
  resize(m: number): void;
  /** Reset to empty — call when a sentinel arrives so the next live sample
   *  doesn't lerp out of the last mean. */
  reset(): void;
}

/** Ring-buffer moving average. O(1) push and resize. */
export function makeAverager(n: number): Averager {
  let buf = new Array<number>(Math.max(1, n)).fill(0);
  let i = 0;
  let sum = 0;
  let filled = 0;
  return {
    push(x: number): number {
      sum -= buf[i] ?? 0;
      buf[i] = x;
      sum += x;
      i = (i + 1) % buf.length;
      if (filled < buf.length) filled++;
      return sum / filled;
    },
    resize(m: number): void {
      const seed = filled > 0 ? sum / filled : 0;
      buf = new Array<number>(Math.max(1, m)).fill(seed);
      i = 0;
      filled = buf.length;
      sum = seed * filled;
    },
    reset(): void {
      buf.fill(0);
      i = 0;
      sum = 0;
      filled = 0;
    },
  };
}
