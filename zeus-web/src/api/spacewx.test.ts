// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';

import { fetchSpaceWeather } from './spacewx';

afterEach(() => {
  vi.restoreAllMocks();
});

describe('fetchSpaceWeather', () => {
  it('maps the full N0NBH-shaped payload', async () => {
    const payload = {
      available: true,
      unavailable: null,
      source: 'N0NBH',
      updated: '15 Jun 2026 1822 GMT',
      fetchedAt: 1781547737537,
      solarFlux: '128',
      sunspots: '78',
      aIndex: '6',
      kIndex: '1',
      kIndexNt: 'No Report',
      xray: 'B7.2',
      heliumLine: '119.1',
      protonFlux: '54',
      electronFlux: '2370',
      aurora: '2',
      normalization: '1.99',
      latDegree: '66.5',
      solarWind: '448.7',
      magneticField: '-0.2',
      fof2: '',
      mufFactor: '',
      muf: 'NoRpt',
      geomagField: 'VR QUIET',
      signalNoise: 'S0-S1',
      bandConditions: [
        { name: '80m-40m', time: 'day', condition: 'Fair' },
        { name: '80m-40m', time: 'night', condition: 'Good' },
      ],
      vhfConditions: [{ name: 'E-Skip', location: 'europe_6m', condition: '50MHz ES' }],
    };

    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify(payload), { status: 200 }),
    );

    const out = await fetchSpaceWeather();

    expect(out.available).toBe(true);
    expect(out.solarFlux).toBe('128');
    expect(out.kIndex).toBe('1');
    expect(out.geomagField).toBe('VR QUIET');
    expect(out.bandConditions).toHaveLength(2);
    expect(out.bandConditions[1]).toEqual({ name: '80m-40m', time: 'night', condition: 'Good' });
    expect(out.vhfConditions[0]?.condition).toBe('50MHz ES');
  });

  it('returns an unavailable snapshot on HTTP error rather than throwing', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('', { status: 503 }));

    const out = await fetchSpaceWeather();

    expect(out.available).toBe(false);
    expect(out.unavailable).toContain('503');
    expect(out.bandConditions).toEqual([]);
    expect(out.vhfConditions).toEqual([]);
  });

  it('coerces a sparse payload to safe defaults', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ available: true }), { status: 200 }),
    );

    const out = await fetchSpaceWeather();

    expect(out.available).toBe(true);
    expect(out.solarFlux).toBeNull();
    expect(out.bandConditions).toEqual([]);
  });
});
