// SPDX-License-Identifier: GPL-2.0-or-later

import {
  selectDisplaySlice,
  useDisplayStore,
  type SpectrumReceiver,
} from '../state/display-store';

export type StitchDisplayKind = 'pan' | 'waterfall';

const MAX_STITCH_FLOOR_SHIFT_DB = 18;

function clampShiftDb(db: number): number {
  return Math.max(-MAX_STITCH_FLOOR_SHIFT_DB, Math.min(MAX_STITCH_FLOOR_SHIFT_DB, db));
}

function floorFor(receiver: SpectrumReceiver, kind: StitchDisplayKind): number | null {
  const slice = selectDisplaySlice(useDisplayStore.getState(), receiver);
  return kind === 'pan' ? slice.panFloorDb : slice.wfFloorDb;
}

// Temporal smoothing of the floor-matching shift (issue: RX2 waterfall
// banding). The shift is recomputed every frame from each receiver's
// instantaneous 22nd-percentile floor, which jitters by a few dB on a busy
// band as strong signals occupy/vacate bins. Because the shift is added
// uniformly to every bin of a row, a raw per-frame shift paints the jitter as
// full-width horizontal bands in the waterfall — most visibly on whichever
// receiver has the *quieter* floor, since the coupling injects the OTHER
// receiver's jitter into its rows. Low-passing the shift converges the panel
// brightness smoothly and can't step row-to-row.
//
// Keyed by receiver+kind: each of the four (A|B)×(pan|waterfall) combos is
// driven once per frame by exactly one consumer, so a per-call EMA tracks a
// ~1 s time constant at the ~25-30 Hz display tick.
const SHIFT_SMOOTHING_ALPHA = 0.05;
const smoothedShiftDb = new Map<string, number>();

function shiftKey(receiver: SpectrumReceiver, kind: StitchDisplayKind): string {
  return `${receiver}:${kind}`;
}

export function stitchFloorShiftDb(
  receiver: SpectrumReceiver,
  kind: StitchDisplayKind,
): number {
  const key = shiftKey(receiver, kind);
  const a = floorFor('A', kind);
  const b = floorFor('B', kind);
  const prev = smoothedShiftDb.get(key);
  if (a === null || b === null) {
    // No valid floor pair this frame. estimateDisplayFloorDb returns null for
    // a momentary all-floor frame, so this flickers null↔valid on a quiet
    // band. HOLD the last smoothed shift through the gap rather than dropping
    // to 0 — dropping would unnormalise that single row and paint exactly the
    // horizontal band we're trying to remove. Falls back to 0 only before the
    // first-ever acquisition (no held value yet).
    return prev ?? 0;
  }
  const own = receiver === 'B' ? b : a;
  const target = (a + b) / 2;
  const raw = clampShiftDb(target - own);
  // Snap on first acquisition (no glide-in artifact); low-pass thereafter so a
  // few-dB floor jitter can't step the per-row offset frame-to-frame.
  const next = prev === undefined ? raw : prev + SHIFT_SMOOTHING_ALPHA * (raw - prev);
  smoothedShiftDb.set(key, next);
  return clampShiftDb(next);
}

/** Test-only: clears the per-receiver smoothing state. */
export function _resetStitchFloorShiftSmoothing(): void {
  smoothedShiftDb.clear();
}

export function normalizeStitchedBins(
  input: Float32Array,
  scratch: Float32Array | null,
  shiftDb: number,
): Float32Array {
  if (Math.abs(shiftDb) < 0.05) return input;
  const output = scratch && scratch.length === input.length
    ? scratch
    : new Float32Array(input.length);
  for (let i = 0; i < input.length; i++) {
    const v = input[i];
    output[i] = v !== undefined && Number.isFinite(v) ? v + shiftDb : (v ?? 0);
  }
  return output;
}
