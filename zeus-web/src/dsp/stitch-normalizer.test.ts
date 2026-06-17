// SPDX-License-Identifier: GPL-2.0-or-later
//
// Verifies the cross-receiver floor-matching shift used by the stitched
// (split RX1│RX2) panadapter/waterfall view. The load-bearing property is the
// temporal smoothing: the raw shift is recomputed every frame from each
// receiver's instantaneous floor, which jitters by a few dB on a busy band;
// added uniformly to a waterfall row, raw jitter paints full-width horizontal
// bands. The EMA must (a) snap on first acquisition, (b) low-pass subsequent
// jitter so it can't step row-to-row, and (c) reset when a floor pair drops.

import { afterEach, describe, expect, it } from 'vitest';
import {
  _resetStitchFloorShiftSmoothing,
  normalizeStitchedBins,
  stitchFloorShiftDb,
} from './stitch-normalizer';
import { createEmptyDisplaySlice, useDisplayStore } from '../state/display-store';

function setFloors(aWf: number | null, bWf: number | null): void {
  useDisplayStore.setState({
    wfFloorDb: aWf,
    rx2: { ...createEmptyDisplaySlice(), wfFloorDb: bWf },
  });
}

afterEach(() => {
  _resetStitchFloorShiftSmoothing();
  useDisplayStore.setState({
    panFloorDb: null,
    wfFloorDb: null,
    rx2: createEmptyDisplaySlice(),
  });
});

describe('stitchFloorShiftDb', () => {
  it('returns 0 when either floor is unavailable', () => {
    setFloors(null, -110);
    expect(stitchFloorShiftDb('B', 'waterfall')).toBe(0);
    setFloors(-90, null);
    expect(stitchFloorShiftDb('B', 'waterfall')).toBe(0);
  });

  it('snaps to the full shift on first acquisition (no glide-in)', () => {
    // A floor -90, B floor -110 → target -100. B shift = target - own = +10.
    setFloors(-90, -110);
    expect(stitchFloorShiftDb('B', 'waterfall')).toBeCloseTo(10, 5);
    // A shift = target - own = -10 (independent smoothing key).
    expect(stitchFloorShiftDb('A', 'waterfall')).toBeCloseTo(-10, 5);
  });

  it('low-passes per-frame jitter instead of tracking it instantly', () => {
    // First frame snaps to +10.
    setFloors(-90, -110);
    expect(stitchFloorShiftDb('B', 'waterfall')).toBeCloseTo(10, 5);
    // RX1 floor jumps so the raw target shift would be 0; the smoothed value
    // must only ease a fraction of the way, NOT jump to 0 in one frame.
    setFloors(-110, -110);
    const afterJump = stitchFloorShiftDb('B', 'waterfall');
    expect(afterJump).toBeLessThan(10);
    expect(afterJump).toBeGreaterThan(9); // alpha 0.05 → ~9.5
  });

  it('converges toward a sustained new target over many frames', () => {
    setFloors(-90, -110); // snap to +10
    stitchFloorShiftDb('B', 'waterfall');
    setFloors(-110, -110); // sustained raw target 0
    let v = 10;
    for (let i = 0; i < 200; i++) v = stitchFloorShiftDb('B', 'waterfall');
    expect(v).toBeCloseTo(0, 1);
  });

  it('holds the last shift through a momentary null-floor frame (no band)', () => {
    setFloors(-90, -110);
    expect(stitchFloorShiftDb('B', 'waterfall')).toBeCloseTo(10, 5);
    // A single all-floor frame makes estimateDisplayFloorDb return null. The
    // shift must HOLD at the last value, not drop to 0 (which would unnormalise
    // that one row and paint a horizontal band — the bug we're fixing).
    setFloors(-90, null);
    expect(stitchFloorShiftDb('B', 'waterfall')).toBeCloseTo(10, 5);
    // Reacquiring the same floors keeps it steady (EMA from 10 toward 10).
    setFloors(-90, -110);
    expect(stitchFloorShiftDb('B', 'waterfall')).toBeCloseTo(10, 5);
  });

  it('returns 0 on a null floor only before the first acquisition', () => {
    setFloors(null, -110);
    expect(stitchFloorShiftDb('B', 'waterfall')).toBe(0);
  });

  it('clamps extreme floor gaps to ±18 dB', () => {
    setFloors(-50, -200); // raw B shift = +75 → clamped to +18
    expect(stitchFloorShiftDb('B', 'waterfall')).toBeCloseTo(18, 5);
  });
});

describe('normalizeStitchedBins', () => {
  it('returns the input unchanged for a negligible shift', () => {
    const input = new Float32Array([-100, -90, -110]);
    expect(normalizeStitchedBins(input, null, 0.01)).toBe(input);
  });

  it('adds the shift uniformly to every finite bin', () => {
    const input = new Float32Array([-100, -90, -110]);
    const out = normalizeStitchedBins(input, null, 5);
    expect(Array.from(out)).toEqual([-95, -85, -105]);
  });
});
