// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { createAutoNotchTracker, detectAutoNotches, type AutoNotchCandidate } from './auto-notch';

const WIDTH = 128;
const CENTER_HZ = 14_200_000;
const HZ_PER_PIXEL = 50;
const NOISE_DB = -120;

function arrays(): {
  spectrum: Float32Array;
  floor: Float32Array;
  confidence: Float32Array;
} {
  return {
    spectrum: new Float32Array(WIDTH).fill(NOISE_DB),
    floor: new Float32Array(WIDTH).fill(NOISE_DB),
    confidence: new Float32Array(WIDTH),
  };
}

describe('auto notch detector', () => {
  it('detects a persistent narrow EMF bar', () => {
    const { spectrum, floor, confidence } = arrays();
    spectrum[63] = -104;
    spectrum[64] = -92;
    spectrum[65] = -104;
    confidence[63] = 0.62;
    confidence[64] = 0.86;
    confidence[65] = 0.62;

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(notches).toHaveLength(1);
    expect(notches[0]!.centerHz).toBeCloseTo(CENTER_HZ, 0);
    expect(notches[0]!.widthHz).toBeGreaterThanOrEqual(190);
    expect(notches[0]!.snrDb).toBeGreaterThan(25);
  });

  it('rejects broad occupied regions that look like real signals', () => {
    const { spectrum, floor, confidence } = arrays();
    for (let i = 45; i < 75; i++) {
      spectrum[i] = NOISE_DB + 22;
      confidence[i] = 0.8;
    }

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(notches).toEqual([]);
  });

  it('does not replace a manual notch already covering the bar', () => {
    const { spectrum, floor, confidence } = arrays();
    spectrum[72] = -94;
    confidence[72] = 0.9;
    const barHz = CENTER_HZ + (72 - WIDTH / 2) * HZ_PER_PIXEL;

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
      existingNotches: [{ id: 'manual', centerHz: barHz, widthHz: 250 }],
    });

    expect(notches).toEqual([]);
  });

  it('verifies repeated sightings before emitting a notch', () => {
    const tracker = createAutoNotchTracker({ verifySamples: 3 });
    const candidate: AutoNotchCandidate = {
      centerHz: CENTER_HZ + 120,
      widthHz: 180,
      snrDb: 28,
      confidence: 0.8,
    };

    expect(tracker.update([candidate])).toEqual([]);
    expect(tracker.update([{ ...candidate, centerHz: CENTER_HZ + 100 }])).toEqual([]);

    const verified = tracker.update([{ ...candidate, centerHz: CENTER_HZ + 90 }]);

    expect(verified).toHaveLength(1);
    expect(verified[0]!.verified).toBe(true);
    expect(verified[0]!.hits).toBe(3);
    expect(verified[0]!.centerHz).toBeGreaterThan(CENTER_HZ + 90);
    expect(verified[0]!.centerHz).toBeLessThan(CENTER_HZ + 120);
  });

  it('holds verified notches through brief missed samples', () => {
    const tracker = createAutoNotchTracker({ verifySamples: 2, holdMisses: 3 });
    const candidate: AutoNotchCandidate = {
      centerHz: CENTER_HZ - 300,
      widthHz: 200,
      snrDb: 32,
      confidence: 0.9,
    };

    tracker.update([candidate]);
    const verified = tracker.update([candidate]);
    expect(verified).toHaveLength(1);

    expect(tracker.update([])).toHaveLength(1);
    expect(tracker.update([])).toHaveLength(1);
    expect(tracker.update([])).toHaveLength(1);
    expect(tracker.update([])).toEqual([]);
  });
});
