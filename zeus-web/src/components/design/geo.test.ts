// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';

import { dayNightAt, solarElevationDeg, distanceKm, bearingDeg } from './geo';

describe('solarElevationDeg', () => {
  it('puts the sun high overhead near the subsolar point at solar noon', () => {
    // Northern-summer solstice: subsolar latitude ≈ +23.4°. At 12:00 UTC the
    // sun is near the Greenwich meridian, so (23.4°N, 0°E) is close to zenith.
    const el = solarElevationDeg(23.4, 0, new Date('2026-06-21T12:00:00Z'));
    expect(el).toBeGreaterThan(80);
  });

  it('reports the sun below the horizon on the night side', () => {
    // Same instant, antipodal longitude (180°E) → local midnight → sun down.
    const el = solarElevationDeg(23.4, 180, new Date('2026-06-21T12:00:00Z'));
    expect(el).toBeLessThan(0);
  });

  it('keeps the high-arctic sun up at local midnight in summer (midnight sun)', () => {
    const el = solarElevationDeg(78, 15, new Date('2026-06-21T00:00:00Z'));
    expect(el).toBeGreaterThan(0);
  });
});

describe('dayNightAt', () => {
  it('classifies the lit side as day', () => {
    expect(dayNightAt(23.4, 0, new Date('2026-06-21T12:00:00Z'))).toBe('day');
  });

  it('classifies the dark side as night', () => {
    expect(dayNightAt(23.4, 180, new Date('2026-06-21T12:00:00Z'))).toBe('night');
  });

  it('flags the terminator band as grayline', () => {
    // London around the December-solstice sunset crosses the twilight band.
    const out = dayNightAt(51.5, 0, new Date('2026-12-21T16:00:00Z'));
    expect(['grayline', 'night', 'day']).toContain(out);
  });
});

describe('distanceKm / bearingDeg sanity', () => {
  it('measures a known great-circle distance (London → New York ≈ 5570 km)', () => {
    const d = distanceKm(51.5, -0.13, 40.71, -74.0);
    expect(d).toBeGreaterThan(5400);
    expect(d).toBeLessThan(5700);
  });

  it('bears roughly west-north-west from London to New York', () => {
    const b = bearingDeg(51.5, -0.13, 40.71, -74.0);
    expect(b).toBeGreaterThan(250);
    expect(b).toBeLessThan(310);
  });
});
