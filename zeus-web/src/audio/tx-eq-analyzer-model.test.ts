// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import { describe, expect, it } from 'vitest';
import {
  DB_MIN,
  DB_MAX,
  F_MIN,
  F_MAX,
  binToFreq,
  binsToColumns,
  buildEqCurve,
  dbToY,
  formatStageDb,
  freqToNorm,
  freqToX,
  gainToY,
  normToFreq,
} from './tx-eq-analyzer-model';

describe('log-frequency mapping', () => {
  it('pins the axis endpoints', () => {
    expect(freqToNorm(F_MIN)).toBeCloseTo(0, 6);
    expect(freqToNorm(F_MAX)).toBeCloseTo(1, 6);
  });

  it('clamps out-of-range frequencies', () => {
    expect(freqToNorm(1)).toBe(0);
    expect(freqToNorm(50_000)).toBe(1);
    expect(freqToNorm(0)).toBe(0);
    expect(freqToNorm(-100)).toBe(0);
  });

  it('is monotonic and invertible', () => {
    expect(freqToNorm(300)).toBeLessThan(freqToNorm(3000));
    expect(normToFreq(freqToNorm(1000))).toBeCloseTo(1000, 3);
  });

  it('offsets by x0 in pixel space', () => {
    expect(freqToX(F_MIN, 10, 200)).toBeCloseTo(10, 6);
    expect(freqToX(F_MAX, 10, 200)).toBeCloseTo(210, 6);
  });
});

describe('level mapping', () => {
  it('maps dB floor/ceiling to bottom/top', () => {
    expect(dbToY(DB_MAX, 0, 100)).toBeCloseTo(0, 6);
    expect(dbToY(DB_MIN, 0, 100)).toBeCloseTo(100, 6);
    expect(dbToY((DB_MIN + DB_MAX) / 2, 0, 100)).toBeCloseTo(50, 6);
  });

  it('clamps levels beyond the scale', () => {
    expect(dbToY(20, 0, 100)).toBeCloseTo(0, 6);
    expect(dbToY(-300, 0, 100)).toBeCloseTo(100, 6);
  });

  it('centres unity EQ gain and rises for boost', () => {
    const mid = gainToY(0, 0, 100);
    expect(mid).toBeCloseTo(50, 6);
    expect(gainToY(12, 0, 100)).toBeLessThan(mid); // boost goes up (smaller Y)
    expect(gainToY(-12, 0, 100)).toBeGreaterThan(mid);
  });
});

describe('binToFreq', () => {
  it('computes bin centre frequencies', () => {
    expect(binToFreq(0, 4096, 48000)).toBe(0);
    expect(binToFreq(1, 4096, 48000)).toBeCloseTo(48000 / 4096, 6);
    expect(binToFreq(2048, 4096, 48000)).toBe(24000);
  });
});

describe('binsToColumns', () => {
  const fftSize = 4096;
  const sampleRate = 48000;
  const bins = fftSize / 2;

  it('returns a continuous (no -Inf gaps) column array of the requested width', () => {
    const db = new Float32Array(bins).fill(-80);
    // Put a strong tone near 1 kHz.
    const toneBin = Math.round((1000 * fftSize) / sampleRate);
    db[toneBin] = -10;

    const width = 320;
    const cols = binsToColumns(db, width, sampleRate, fftSize);
    expect(cols.length).toBe(width);
    for (const v of cols) expect(Number.isFinite(v)).toBe(true);

    // The peak column should sit at the 1 kHz log-X position.
    let maxX = 0;
    for (let x = 1; x < width; x += 1) {
      if ((cols[x] ?? -Infinity) > (cols[maxX] ?? -Infinity)) maxX = x;
    }
    const expectedX = Math.round(freqToNorm(1000) * (width - 1));
    expect(Math.abs(maxX - expectedX)).toBeLessThanOrEqual(2);
    expect(cols[maxX] ?? -Infinity).toBeGreaterThan(-40);
  });

  it('reuses the provided output buffer', () => {
    const db = new Float32Array(bins).fill(-70);
    const out = new Float32Array(128);
    const cols = binsToColumns(db, 128, sampleRate, fftSize, out);
    expect(cols).toBe(out);
  });
});

describe('buildEqCurve', () => {
  const width = 256;

  it('is flat (unity) inside the passband with no band gains', () => {
    const curve = buildEqCurve(
      { filterLowHz: 150, filterHighHz: 2850, bands: [], bandsEnabled: true },
      width,
    );
    const at = (hz: number) => curve[Math.round(freqToNorm(hz) * (width - 1))] ?? 0;
    expect(at(1000)).toBeCloseTo(0, 3);
    expect(at(500)).toBeCloseTo(0, 3);
  });

  it('rolls off below the low edge and above the high edge', () => {
    const curve = buildEqCurve(
      { filterLowHz: 300, filterHighHz: 2700, bands: [], bandsEnabled: true },
      width,
    );
    const at = (hz: number) => curve[Math.round(freqToNorm(hz) * (width - 1))] ?? 0;
    expect(at(60)).toBeLessThan(-10);
    expect(at(5000)).toBeLessThan(-10);
    expect(at(1000)).toBeGreaterThan(at(60));
  });

  it('adds a boost bump at an active band centre', () => {
    const curve = buildEqCurve(
      {
        filterLowHz: 100,
        filterHighHz: 4000,
        bands: [{ freqHz: 1500, postGainDb: 9 }],
        bandsEnabled: true,
      },
      width,
    );
    const at = (hz: number) => curve[Math.round(freqToNorm(hz) * (width - 1))] ?? 0;
    expect(at(1500)).toBeGreaterThan(6);
    expect(at(1500)).toBeGreaterThan(at(500));
  });

  it('ignores band gains when the band processor is disabled', () => {
    const curve = buildEqCurve(
      {
        filterLowHz: 100,
        filterHighHz: 4000,
        bands: [{ freqHz: 1500, postGainDb: 9 }],
        bandsEnabled: false,
      },
      width,
    );
    const at = (hz: number) => curve[Math.round(freqToNorm(hz) * (width - 1))] ?? 0;
    expect(at(1500)).toBeCloseTo(0, 3);
  });
});

describe('formatStageDb', () => {
  it('formats real readings to one decimal', () => {
    expect(formatStageDb(-12.34)).toBe('-12.3');
    expect(formatStageDb(0)).toBe('0.0');
  });

  it('renders bypassed/idle stages as a dash', () => {
    expect(formatStageDb(-200)).toBe('—');
    expect(formatStageDb(-400)).toBe('—');
    expect(formatStageDb(Number.NEGATIVE_INFINITY)).toBe('—');
    expect(formatStageDb(Number.NaN)).toBe('—');
  });
});
