// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';

import { fetchPropagation } from './propagation';

afterEach(() => {
  vi.restoreAllMocks();
});

describe('fetchPropagation', () => {
  it('maps a full engine payload and passes query params', async () => {
    const payload = {
      available: true,
      unavailable: null,
      model: 'ITU-R P.533-14',
      sfi: 142,
      ssn: 90,
      kIndex: 2,
      muf: 24.5,
      luf: 4.1,
      distanceKm: 5920,
      currentHourUtc: 18,
      bands: [
        { band: '20m', freqMhz: 14, reliability: 88, snr: '+15dB', status: 'GOOD' },
        { band: '17m', freqMhz: 18, reliability: 60, snr: '+6dB', status: 'FAIR' },
      ],
      currentBand: { band: '20m', freqMhz: 14, reliability: 88, snr: '+15dB', status: 'GOOD' },
    };

    const spy = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValue(new Response(JSON.stringify(payload), { status: 200 }));

    const out = await fetchPropagation({
      deLat: 53.3,
      deLon: -6.2,
      dxLat: 40.7,
      dxLon: -74,
      mode: 'SSB',
      freqMhz: 14.2,
    });

    expect(out.available).toBe(true);
    expect(out.model).toBe('ITU-R P.533-14');
    expect(out.bands).toHaveLength(2);
    expect(out.currentBand?.band).toBe('20m');
    expect(out.currentBand?.reliability).toBe(88);

    const url = String(spy.mock.calls[0]?.[0]);
    expect(url).toContain('/api/propagation?');
    expect(url).toContain('deLat=53.3');
    expect(url).toContain('mode=SSB');
    expect(url).toContain('freq=14.2');
  });

  it('returns an unavailable result on HTTP error rather than throwing', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('', { status: 503 }));

    const out = await fetchPropagation({ deLat: 0, deLon: 0, dxLat: 1, dxLon: 1 });

    expect(out.available).toBe(false);
    expect(out.unavailable).toContain('503');
    expect(out.bands).toEqual([]);
  });

  it('coerces a malformed/sparse payload to safe defaults', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ available: true }), { status: 200 }),
    );

    const out = await fetchPropagation({ deLat: 0, deLon: 0, dxLat: 1, dxLon: 1 });

    expect(out.available).toBe(true);
    expect(out.bands).toEqual([]);
    expect(out.currentBand).toBeNull();
    expect(out.muf).toBe(0);
  });
});
