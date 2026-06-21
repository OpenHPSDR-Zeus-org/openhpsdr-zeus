// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Distributed under the GNU General Public License v2 or later; see LICENSE.

import { describe, expect, it } from 'vitest';

import { computeImd } from './imd-measure';

// Build a synthetic spectrum: flat noise floor with narrow peaks (1-pixel spikes)
// at the given pixel positions/levels. Mirrors a two-tone panadapter capture.
function spectrum(width: number, floorDbm: number, peaks: Array<[number, number]>): Float32Array {
  const db = new Float32Array(width).fill(floorDbm);
  for (const [x, dbm] of peaks) db[x] = dbm;
  return db;
}

describe('computeImd', () => {
  // Center pixel = width/2 = 512. hzPerPixel = 10 → +700 Hz = +70 px (582),
  // +1900 Hz = +190 px (702). Tone spacing 1200 Hz = 120 px. IMD products sit
  // one/two spacings outward: IMD3 at 462/822, IMD5 at 342/942.
  const width = 1024;
  const centerHz = 14_200_000;
  const hzPerPixel = 10;

  it('locates the two fundamentals + IMD3/IMD5 and computes dBc', () => {
    const db = spectrum(width, -120, [
      [582, 0], // fundamental low
      [702, 0], // fundamental high
      [462, -30], // IMD3 lower
      [822, -30], // IMD3 upper
      [342, -50], // IMD5 lower
      [942, -50], // IMD5 upper
    ]);

    const r = computeImd({ db, width, centerHz, hzPerPixel });
    expect(r.ok).toBe(true);
    if (!r.ok) return;

    expect(r.toneSpacingHz).toBeCloseTo(1200, 0);
    // dbc = min(fundamental) - max(product) = 0 - (-30) = 30 down.
    expect(r.imd3.dbc).toBeCloseTo(30, 1);
    expect(r.imd5.dbc).toBeCloseTo(50, 1);
    // OIP3 = fundamental + imd3dBc/2 = 0 + 15.
    expect(r.oip3).toBeCloseTo(15, 1);
    expect(r.oip5).toBeCloseTo(25, 1);
    // Fundamental frequencies map back to carrier ±offset.
    expect(r.f0LowerHz).toBeCloseTo(centerHz + 700, 0);
    expect(r.f0UpperHz).toBeCloseTo(centerHz + 1900, 0);
  });

  it('uses expected generator spacing to avoid picking a strong IMD product as a fundamental', () => {
    const db = spectrum(width, -120, [
      [582, 0], // fundamental low
      [702, -0.5], // fundamental high
      [462, -0.2], // very strong IMD3 lower product
      [822, -30], // IMD3 upper
      [342, -35], // IMD5 lower
      [942, -45], // IMD5 upper
    ]);

    const withoutSpacing = computeImd({ db, width, centerHz, hzPerPixel });
    expect(withoutSpacing.ok).toBe(false);

    const r = computeImd({
      db,
      width,
      centerHz,
      hzPerPixel,
      expectedToneSpacingHz: 1200,
    });
    expect(r.ok).toBe(true);
    if (!r.ok) return;

    expect(r.f0LowerHz).toBeCloseTo(centerHz + 700, 0);
    expect(r.f0UpperHz).toBeCloseTo(centerHz + 1900, 0);
    expect(r.imd3.dbc).toBeCloseTo(-0.3, 1);
  });

  it('reports a spacing miss when displayed peaks do not match the generator', () => {
    const db = spectrum(width, -120, [
      [582, 0],
      [702, 0],
      [462, -30],
      [822, -30],
      [342, -50],
      [942, -50],
    ]);

    const r = computeImd({
      db,
      width,
      centerHz,
      hzPerPixel,
      expectedToneSpacingHz: 1500,
    });
    expect(r.ok).toBe(false);
    if (r.ok) return;
    expect(r.reason).toContain('two-tone spacing not found');
  });

  it('reports a miss when the tones are merged / zoomed too far out', () => {
    // Two peaks only 5 px apart — below the resolve threshold.
    const db = spectrum(width, -120, [
      [510, 0],
      [515, 0],
    ]);
    const r = computeImd({ db, width, centerHz, hzPerPixel });
    expect(r.ok).toBe(false);
  });

  it('reports a miss on an empty / single-tone spectrum', () => {
    const flat = spectrum(width, -120, [[512, 0]]);
    const r = computeImd({ db: flat, width, centerHz, hzPerPixel });
    expect(r.ok).toBe(false);
  });

  it('fails closed on malformed spectrum geometry', () => {
    const db = spectrum(width, -120, [
      [582, 0],
      [702, 0],
      [462, -30],
      [822, -30],
      [342, -50],
      [942, -50],
    ]);

    for (const bad of [
      { width: Infinity, centerHz, hzPerPixel },
      { width: width + 1, centerHz, hzPerPixel },
      { width: width + 0.5, centerHz, hzPerPixel },
      { width, centerHz: NaN, hzPerPixel },
      { width, centerHz, hzPerPixel: Infinity },
      { width, centerHz, hzPerPixel: 0 },
    ]) {
      const r = computeImd({ db, ...bad });
      expect(r).toEqual({ ok: false, reason: 'no spectrum' });
    }
  });

  it('reports a miss when the IMD products are off-screen', () => {
    // Fundamentals present and well-separated, but no products in the spectrum.
    const db = spectrum(width, -120, [
      [400, 0],
      [600, 0],
    ]);
    const r = computeImd({ db, width, centerHz, hzPerPixel });
    // Products would be searched at 400-200=200 / 600+200=800 etc.; the flat
    // floor there yields a "peak" only at array ends — with a uniform floor the
    // local-maxima finder returns nothing in-window, so this misses.
    expect(r.ok).toBe(false);
  });
});
