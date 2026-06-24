// SPDX-License-Identifier: GPL-2.0-or-later
import { afterEach, describe, expect, it } from 'vitest';
import {
  estimateRowFloorDb,
  floorNormalizationOffsetDb,
  getReceiverFloorDb,
  reportReceiverFloorDb,
  resetReceiverFloors,
} from './floor-normalization';

afterEach(() => resetReceiverFloors());

describe('estimateRowFloorDb', () => {
  it('returns null for an empty row', () => {
    expect(estimateRowFloorDb(new Float32Array(0))).toBeNull();
  });

  it('estimates the low-percentile floor, ignoring strong carriers', () => {
    // A flat -130 dB floor with a handful of -40 dB carriers poking through.
    const row = new Float32Array(1000).fill(-130);
    for (let i = 0; i < 1000; i += 100) row[i] = -40;
    const floor = estimateRowFloorDb(row)!;
    expect(floor).toBeCloseTo(-130, 0); // 25th pct sits firmly in the floor
  });

  it('ignores non-finite bins', () => {
    const row = new Float32Array(512).fill(-120);
    row[0] = Number.NaN;
    row[1] = Number.POSITIVE_INFINITY;
    expect(estimateRowFloorDb(row)).toBeCloseTo(-120, 0);
  });
});

describe('floorNormalizationOffsetDb', () => {
  it('is 0 for the focused pane itself', () => {
    reportReceiverFloorDb(0, -120);
    expect(floorNormalizationOffsetDb(0, 0)).toBe(0);
  });

  it('is 0 when either floor is unknown', () => {
    reportReceiverFloorDb(0, -120);
    expect(floorNormalizationOffsetDb(1, 0)).toBe(0); // pane 1 unknown
    expect(floorNormalizationOffsetDb(0, 2)).toBe(0); // focus 2 unknown
  });

  it('shifts a noisier pane up by (here - focused)', () => {
    reportReceiverFloorDb(0, -135); // focused RX1 floor (20m, quiet)
    reportReceiverFloorDb(1, -120); // RX2 floor (40m, noisier)
    // RX2 is 15 dB hotter, so its window shifts +15 so its floor still maps low.
    expect(floorNormalizationOffsetDb(1, 0)).toBeCloseTo(15, 1);
    // And the reciprocal: a quieter pane shifts down.
    expect(floorNormalizationOffsetDb(0, 1)).toBeCloseTo(-15, 1);
  });

  it('clamps the offset to +/-40 dB', () => {
    reportReceiverFloorDb(0, -200);
    reportReceiverFloorDb(1, 0);
    expect(floorNormalizationOffsetDb(1, 0)).toBe(40);
    expect(floorNormalizationOffsetDb(0, 1)).toBe(-40);
  });
});

describe('reportReceiverFloorDb EMA', () => {
  it('seeds on first report then smooths', () => {
    reportReceiverFloorDb(3, -100);
    expect(getReceiverFloorDb(3)).toBe(-100);
    // Second report eases toward the new value (alpha 0.1), not a jump.
    reportReceiverFloorDb(3, -120);
    const v = getReceiverFloorDb(3)!;
    expect(v).toBeGreaterThan(-120);
    expect(v).toBeLessThan(-100);
    expect(v).toBeCloseTo(-102, 0); // -100 + (-20 * 0.1)
  });

  it('ignores non-finite reports', () => {
    reportReceiverFloorDb(4, -110);
    reportReceiverFloorDb(4, Number.NaN);
    expect(getReceiverFloorDb(4)).toBe(-110);
  });
});
