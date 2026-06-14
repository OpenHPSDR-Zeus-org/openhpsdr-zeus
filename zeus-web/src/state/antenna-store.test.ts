// SPDX-License-Identifier: GPL-2.0-or-later
//
// Antenna store — parse robustness + optimistic setBand round-trip
// (external-ports plan, Phase 2).

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  fetchAntennaSettings,
  updateAntennaBand,
  useAntennaStore,
} from './antenna-store';

afterEach(() => {
  vi.unstubAllGlobals();
  useAntennaStore.setState({
    settings: { hasTxAntennaRelays: false, hasRxAntennaRelays: false, bands: [] },
    loaded: false,
    inflight: false,
    error: null,
  });
});

function stubFetch(body: unknown, status = 200) {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify(body), {
        status,
        headers: { 'content-type': 'application/json' },
      }),
    ),
  );
}

describe('antenna-store parsing', () => {
  it('coerces unknown antenna strings to Ant1', async () => {
    stubFetch({
      hasTxAntennaRelays: true,
      hasRxAntennaRelays: true,
      bands: [{ band: '20m', txAnt: 'Ant9', rxAnt: 'Ant2' }],
    });
    const s = await fetchAntennaSettings();
    expect(s.bands[0].txAnt).toBe('Ant1');
    expect(s.bands[0].rxAnt).toBe('Ant2');
  });

  it('defaults gates false and bands [] on garbage', async () => {
    stubFetch(42);
    const s = await fetchAntennaSettings();
    expect(s.hasTxAntennaRelays).toBe(false);
    expect(s.bands).toEqual([]);
  });

  it('throws on a 409 from the PUT', async () => {
    stubFetch({ error: 'no relays' }, 409);
    await expect(updateAntennaBand('20m', 'Ant2', 'Ant1')).rejects.toThrow();
  });
});

describe('antenna-store setBand', () => {
  it('optimistically updates then adopts the server response', async () => {
    stubFetch({
      hasTxAntennaRelays: true,
      hasRxAntennaRelays: true,
      bands: [{ band: '20m', txAnt: 'Ant3', rxAnt: 'Ant1' }],
    });
    await useAntennaStore.getState().setBand('20m', 'Ant3', 'Ant1');
    const b = useAntennaStore.getState().settings.bands.find((x) => x.band === '20m');
    expect(b?.txAnt).toBe('Ant3');
  });

  it('rolls back on PUT failure', async () => {
    // Seed a known-good current state, then fail the PUT.
    useAntennaStore.setState({
      settings: {
        hasTxAntennaRelays: true,
        hasRxAntennaRelays: true,
        bands: [{ band: '20m', txAnt: 'Ant1', rxAnt: 'Ant1' }],
      },
    });
    stubFetch({ error: 'boom' }, 409);
    await useAntennaStore.getState().setBand('20m', 'Ant2', 'Ant1');
    const b = useAntennaStore.getState().settings.bands.find((x) => x.band === '20m');
    expect(b?.txAnt).toBe('Ant1'); // rolled back
    expect(useAntennaStore.getState().error).toBeTruthy();
  });
});
