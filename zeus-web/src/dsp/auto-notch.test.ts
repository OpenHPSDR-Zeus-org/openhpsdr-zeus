// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import {
  createAutoNotchTracker,
  detectAutoNotches,
  explainAutoNotchAt,
  explainAutoNotchRejections,
  type AutoNotchCandidate,
} from './auto-notch';

const WIDTH = 128;
const CENTER_HZ = 14_200_000;
const HZ_PER_PIXEL = 50;
const NOISE_DB = -120;

function arrays(): {
  spectrum: Float32Array;
  floor: Float32Array;
  stationarity: Float32Array;
} {
  return {
    spectrum: new Float32Array(WIDTH).fill(NOISE_DB),
    floor: new Float32Array(WIDTH).fill(NOISE_DB),
    stationarity: new Float32Array(WIDTH),
  };
}

/** Paint a steep narrow carrier: a single-bin peak with the immediate
 *  neighbours dropped well below the −6 dB width line. */
function paintCarrier(
  spectrum: Float32Array,
  stationarity: Float32Array,
  bin: number,
  snrDb: number,
  steadiness: number,
): void {
  spectrum[bin] = NOISE_DB + snrDb;
  spectrum[bin - 1] = NOISE_DB + Math.max(0, snrDb - 12);
  spectrum[bin + 1] = NOISE_DB + Math.max(0, snrDb - 12);
  stationarity[bin] = steadiness;
  stationarity[bin - 1] = steadiness;
  stationarity[bin + 1] = steadiness;
}

/** Paint a broad occupied hump (SSB-voice-like): a wide plateau that stays
 *  above the −6 dB line for many bins. `steadiness` lets a test isolate the
 *  narrowness gate (steady hump) from the stationarity gate (fluctuating). */
function paintHump(
  spectrum: Float32Array,
  stationarity: Float32Array,
  lo: number,
  hi: number,
  snrDb: number,
  steadiness: number,
): void {
  for (let i = lo; i <= hi; i++) {
    spectrum[i] = NOISE_DB + snrDb;
    stationarity[i] = steadiness;
  }
}

describe('auto notch carrier detector', () => {
  it('detects a steady narrow carrier', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 64, 28, 0.9);

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(notches).toHaveLength(1);
    expect(notches[0]!.centerHz).toBeCloseTo(CENTER_HZ, 0);
    // A carrier yields a NARROW notch now — not the multi-hundred-Hz slab the
    // old SNR-run detector produced.
    expect(notches[0]!.widthHz).toBeLessThanOrEqual(200);
    expect(notches[0]!.snrDb).toBeGreaterThanOrEqual(25); // prominence, not raw SNR
  });

  it('rejects a broad occupied region (narrowness gate) even when steady', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintHump(spectrum, stationarity, 45, 74, 22, 0.9);

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(notches).toEqual([]);
  });

  it('rejects a fluctuating voice formant even when narrow and prominent', () => {
    const { spectrum, floor, stationarity } = arrays();
    // Narrow + prominent (would pass the shape gates) but its amplitude swings,
    // so stationarity is low — this is exactly the voice low-end the old
    // detector notched.
    paintCarrier(spectrum, stationarity, 64, 30, 0.2);

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(notches).toEqual([]);
  });

  it('detects a strong carrier whose local CFAR floor is lifted by its own skirts', () => {
    const { spectrum, floor, stationarity } = arrays();
    // Self-masking: the spatial floor near the carrier is raised, so the old
    // "SNR above floor" gate would collapse — but topographic prominence over
    // the local saddle still towers, so the carrier is caught.
    paintCarrier(spectrum, stationarity, 70, 45, 0.9);
    for (let i = 66; i <= 74; i++) floor[i] = -85; // lifted local floor

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(notches).toHaveLength(1);
    expect(notches[0]!.centerHz).toBeCloseTo(CENTER_HZ + (70 - WIDTH / 2) * HZ_PER_PIXEL, 0);
  });

  it('emits nothing when the stationarity map is unavailable (fail safe)', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 64, 30, 0.95);

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity: null,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(notches).toEqual([]);
  });

  it('skips carriers inside a protected digital segment (FT8/FT4)', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 64, 30, 0.9); // center = CENTER_HZ

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
      protectedRanges: [{ lowHz: CENTER_HZ - 1_000, highHz: CENTER_HZ + 1_000 }],
    });

    expect(notches).toEqual([]);
  });

  it('still detects a carrier outside the protected segments', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 64, 30, 0.9);

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
      protectedRanges: [{ lowHz: CENTER_HZ + 50_000, highHz: CENTER_HZ + 60_000 }],
    });

    expect(notches).toHaveLength(1);
  });

  it('detects a carrier smeared across a few bins at a wide display span', () => {
    const { spectrum, floor, stationarity } = arrays();
    // 3-bin-wide peak (≈600 Hz) at 200 Hz/bin: the old fixed 500 Hz narrowness
    // limit would have rejected it; the zoom-aware limit (≈4 bins) keeps it.
    for (let i = 63; i <= 65; i++) {
      spectrum[i] = NOISE_DB + 42;
      stationarity[i] = 0.9;
    }

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: 200,
    });

    expect(notches).toHaveLength(1);
  });

  it('explains a passing carrier via the diagnostic hook', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 64, 28, 0.9);

    const report = explainAutoNotchAt(
      { spectrum, floor, confidence: null, stationarity, centerHz: CENTER_HZ, hzPerPixel: HZ_PER_PIXEL },
      CENTER_HZ,
    );

    expect(report).not.toBeNull();
    expect(report!.verdict).toBe('pass');
    expect(report!.isLocalMax).toBe(true);
    expect(report!.prominenceDb).toBeGreaterThanOrEqual(8);
  });

  it('reports the gate that rejected a fluctuating peak', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 64, 30, 0.2); // low steadiness

    const rejections = explainAutoNotchRejections({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(rejections.length).toBeGreaterThanOrEqual(1);
    expect(rejections[0]!.verdict).toBe('stationarity');
  });

  it('does not replace a manual notch already covering the carrier', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 72, 30, 0.9);
    const barHz = CENTER_HZ + (72 - WIDTH / 2) * HZ_PER_PIXEL;

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
      existingNotches: [{ id: 'manual', centerHz: barHz, widthHz: 250 }],
    });

    expect(notches).toEqual([]);
  });

  it('rejects a narrow low-end crest riding on a wider voice channel', () => {
    const { spectrum, floor, stationarity } = arrays();
    // A voice low end can present a locally narrow crest, but it sits on a
    // broader occupied shoulder. Even if stationarity is fooled, it is signal
    // body, not EMF/RFI.
    for (let i = 56; i <= 88; i++) {
      spectrum[i] = NOISE_DB + 20;
      stationarity[i] = 0.9;
    }
    spectrum[64] = NOISE_DB + 32;

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
    });

    expect(notches).toEqual([]);
    const report = explainAutoNotchAt(
      { spectrum, floor, confidence: null, stationarity, centerHz: CENTER_HZ, hzPerPixel: HZ_PER_PIXEL },
      CENTER_HZ,
    );
    expect(report?.verdict).toBe('occupied');
    expect(report!.occupiedWidthHz).toBeGreaterThan(report!.maxOccupiedShoulderHz);
  });

  it('rejects voice low-end crests even before passband protection is considered', () => {
    const { spectrum, floor, stationarity } = arrays();
    for (let i = 64; i <= 98; i++) {
      spectrum[i] = NOISE_DB + 20;
      stationarity[i] = 0.9;
    }
    spectrum[84] = NOISE_DB + 32;

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
      tunedPassband: {
        dialHz: CENTER_HZ,
        lowOffsetHz: 150,
        highOffsetHz: 2850,
      },
    });

    expect(notches).toEqual([]);
    expect(
      explainAutoNotchAt(
        {
          spectrum,
          floor,
          confidence: null,
          stationarity,
          centerHz: CENTER_HZ,
          hzPerPixel: HZ_PER_PIXEL,
          tunedPassband: {
            dialHz: CENTER_HZ,
            lowOffsetHz: 150,
            highOffsetHz: 2850,
          },
        },
        CENTER_HZ + 1_000,
      )?.verdict,
    ).toBe('occupied');
  });

  it('protects the tuned passband from automatic notches', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 84, 32, 0.92); // +1 kHz from dial, inside USB passband

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
      tunedPassband: {
        dialHz: CENTER_HZ,
        lowOffsetHz: 150,
        highOffsetHz: 2850,
      },
    });

    expect(notches).toEqual([]);
    expect(
      explainAutoNotchAt(
        {
          spectrum,
          floor,
          confidence: null,
          stationarity,
          centerHz: CENTER_HZ,
          hzPerPixel: HZ_PER_PIXEL,
          tunedPassband: {
            dialHz: CENTER_HZ,
            lowOffsetHz: 150,
            highOffsetHz: 2850,
          },
        },
        CENTER_HZ + 1_000,
      )?.verdict,
    ).toBe('passband');
  });

  it('detects a narrow adjacent blocker outside the tuned passband', () => {
    const { spectrum, floor, stationarity } = arrays();
    paintCarrier(spectrum, stationarity, 124, 32, 0.92); // +3 kHz from dial, just above USB passband

    const notches = detectAutoNotches({
      spectrum,
      floor,
      confidence: null,
      stationarity,
      centerHz: CENTER_HZ,
      hzPerPixel: HZ_PER_PIXEL,
      tunedPassband: {
        dialHz: CENTER_HZ,
        lowOffsetHz: 150,
        highOffsetHz: 2850,
      },
    });

    expect(notches).toHaveLength(1);
    expect(notches[0]!.centerHz).toBeCloseTo(CENTER_HZ + 3_000, 0);
    expect(notches[0]!.widthHz).toBeLessThanOrEqual(200);
  });
});

describe('auto notch tracker', () => {
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

  it('tracks slow drift after verification without unlocking the notch', () => {
    const tracker = createAutoNotchTracker({ verifySamples: 3 });
    const candidate: AutoNotchCandidate = {
      centerHz: CENTER_HZ + 100,
      widthHz: 220,
      snrDb: 30,
      confidence: 0.86,
    };

    tracker.update([candidate]);
    tracker.update([{ ...candidate, centerHz: CENTER_HZ + 110 }]);
    const locked = tracker.update([{ ...candidate, centerHz: CENTER_HZ + 95 }]);
    expect(locked).toHaveLength(1);
    const lockedCenter = locked[0]!.centerHz;

    let afterRefine = locked;
    for (const offsetHz of [140, 150, 160, 170, 175]) {
      afterRefine = tracker.update([{ ...candidate, centerHz: CENTER_HZ + offsetHz }]);
    }

    expect(afterRefine).toHaveLength(1);
    expect(afterRefine[0]!.locked).toBe(true);
    expect(afterRefine[0]!.centerHz).toBeGreaterThan(lockedCenter);
    expect(afterRefine[0]!.centerHz).toBeLessThan(CENTER_HZ + 175);
  });

  it('rejects candidates that wander before validation', () => {
    const tracker = createAutoNotchTracker({ verifySamples: 3 });
    const candidate: AutoNotchCandidate = {
      centerHz: CENTER_HZ,
      widthHz: 600,
      snrDb: 30,
      confidence: 0.82,
    };

    expect(tracker.update([candidate])).toEqual([]);
    expect(tracker.update([{ ...candidate, centerHz: CENTER_HZ + 350 }])).toEqual([]);
    expect(tracker.update([{ ...candidate, centerHz: CENTER_HZ - 300 }])).toEqual([]);
    expect(tracker.update([{ ...candidate, centerHz: CENTER_HZ + 450 }])).toEqual([]);
    expect(tracker.update([{ ...candidate, centerHz: CENTER_HZ - 420 }])).toEqual([]);
  });

  it('preserves a verified narrow notch width', () => {
    const tracker = createAutoNotchTracker({ verifySamples: 1 });
    const candidate: AutoNotchCandidate = {
      centerHz: CENTER_HZ + 2_000,
      widthHz: 220,
      snrDb: 34,
      confidence: 0.86,
    };

    const verified = tracker.update([candidate]);
    expect(verified).toHaveLength(1);
    expect(verified[0]!.widthHz).toBe(220);
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
