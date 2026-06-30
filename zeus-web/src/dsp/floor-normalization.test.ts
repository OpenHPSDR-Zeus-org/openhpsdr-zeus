// SPDX-License-Identifier: GPL-2.0-or-later
import { afterEach, describe, expect, it } from 'vitest';
import {
  estimateRowFloorDb,
  floorNormalizationOffsetDb,
  forgetReceiverFloor,
  getReceiverFloorDb,
  referenceFloorDb,
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

describe('referenceFloorDb', () => {
  it('is null before any floor is reported', () => {
    expect(referenceFloorDb()).toBeNull();
  });

  it('is the receiver floor itself with one pane', () => {
    reportReceiverFloorDb(0, -123);
    expect(referenceFloorDb()).toBe(-123);
  });

  it('is the median (odd count), robust to an outlier band', () => {
    reportReceiverFloorDb(0, -135);
    reportReceiverFloorDb(1, -120);
    reportReceiverFloorDb(2, -160); // dead-band outlier
    expect(referenceFloorDb()).toBe(-135); // middle value, not dragged by -160
  });

  it('averages the two middle values (even count)', () => {
    reportReceiverFloorDb(0, -135);
    reportReceiverFloorDb(1, -120);
    expect(referenceFloorDb()).toBeCloseTo(-127.5, 5);
  });
});

describe('floorNormalizationOffsetDb', () => {
  it('is 0 before any floor is reported', () => {
    expect(floorNormalizationOffsetDb(0)).toBe(0);
  });

  it('is 0 for a lone pane (median is its own floor)', () => {
    reportReceiverFloorDb(0, -120);
    expect(floorNormalizationOffsetDb(0)).toBe(0);
  });

  it('is 0 when this pane has not reported yet', () => {
    reportReceiverFloorDb(0, -120);
    expect(floorNormalizationOffsetDb(1)).toBe(0); // pane 1 unknown
  });

  it('offsets each pane from the median floor', () => {
    reportReceiverFloorDb(0, -135); // quiet
    reportReceiverFloorDb(1, -120); // noisier → median is -127.5
    // Noisier pane shifts its window up so its floor still maps low; the quiet
    // pane shifts down. Both are symmetric about the shared median.
    expect(floorNormalizationOffsetDb(1)).toBeCloseTo(7.5, 1);
    expect(floorNormalizationOffsetDb(0)).toBeCloseTo(-7.5, 1);
  });

  it('clamps the offset to +/-80 dB', () => {
    reportReceiverFloorDb(0, -200);
    reportReceiverFloorDb(1, 0); // median -100 → raw offsets +/-100
    expect(floorNormalizationOffsetDb(1)).toBe(80);
    expect(floorNormalizationOffsetDb(0)).toBe(-80);
  });

  it('drops a forgotten pane from the anchor', () => {
    reportReceiverFloorDb(0, -135);
    reportReceiverFloorDb(1, -120);
    forgetReceiverFloor(1);
    // Only RX1 left → median is its own floor → offset 0.
    expect(referenceFloorDb()).toBe(-135);
    expect(floorNormalizationOffsetDb(0)).toBe(0);
  });
});

describe('Kiwi slice (index 10) is a foreign RX - aligns to, but never defines, the anchor', () => {
  it('is excluded from the reference median', () => {
    reportReceiverFloorDb(0, -135); // hardware RX1
    reportReceiverFloorDb(10, -100); // Kiwi - must not move the anchor
    expect(referenceFloorDb()).toBe(-135);
  });

  it('aligns the Kiwi fully to the hardware anchor (not half-way)', () => {
    reportReceiverFloorDb(0, -140); // quiet hardware RX
    reportReceiverFloorDb(10, -100); // noisier remote Kiwi
    // Kiwi shifts its whole window up by the full floor delta so its floor lands
    // at the hardware floor's colour - NOT the half-shift a shared median gives.
    expect(floorNormalizationOffsetDb(10)).toBeCloseTo(40, 1);
  });

  it('does not pull the hardware panes (RX1 offset stays 0 with only the Kiwi alongside)', () => {
    reportReceiverFloorDb(0, -140);
    reportReceiverFloorDb(10, -100);
    expect(floorNormalizationOffsetDb(0)).toBe(0);
  });

  it('falls back to no shift when no hardware floor has reported yet', () => {
    reportReceiverFloorDb(10, -100); // Kiwi alone -> anchor null -> raw window
    expect(referenceFloorDb()).toBeNull();
    expect(floorNormalizationOffsetDb(10)).toBe(0);
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
