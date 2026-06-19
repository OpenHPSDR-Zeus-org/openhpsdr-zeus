// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../../components/meters/__tests__/harness';
import { useConnectionStore } from '../../state/connection-store';
import { useTxStore } from '../../state/tx-store';
import { useTxAudioProfileStore } from '../../state/tx-audio-profile-store';
import { TxFidelityPanel } from './TxFidelityPanel';

vi.mock('../../api/client', async () => {
  const actual = await vi.importActual<typeof import('../../api/client')>('../../api/client');
  return {
    ...actual,
    fetchTxFidelityPolicy: vi.fn(() =>
      Promise.resolve({ profileId: 'studio-ssb', targetSpectralDensity: 55 }),
    ),
    saveTxFidelityPolicy: vi.fn(async (policy: unknown) => policy),
    // Unified TX audio profile endpoints.
    fetchTxAudioProfiles: vi.fn(async () => [
      {
        id: 'studio-ssb',
        name: 'Studio SSB',
        micGainDb: 0,
        levelerMaxGainDb: 8,
        txLeveling: {
          alcMaxGainDb: 3,
          alcDecayMs: 10,
          levelerEnabled: true,
          levelerDecayMs: 100,
          compressorEnabled: false,
          compressorGainDb: 0,
        },
        cfcConfig: { enabled: false, postEqEnabled: false, preCompDb: 0, prePeqDb: 0, bands: [] },
        lowCutHz: 150,
        highCutHz: 2850,
        processingMode: 'native' as const,
        masterBypass: false,
        chainOrder: [],
        chainParked: [],
        vstPluginStates: {},
        nativePluginStates: {},
        targetSpectralDensity: 55,
        createdUtc: '',
        updatedUtc: '',
      },
    ]),
    fetchLastLoadedTxAudioProfile: vi.fn(async () => ({ id: 'studio-ssb' })),
  };
});

describe('TxFidelityPanel', () => {
  beforeEach(() => {
    useTxAudioProfileStore.setState({ profiles: [], loaded: false, lastLoadedId: null, busy: false });
    useConnectionStore.setState({ status: 'Connected', mode: 'USB' });
    useTxStore.setState({ micGainDb: 0, levelerMaxGainDb: 8 });
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url === '/api/tx-audio-suite/preview') {
          return new Response(JSON.stringify({ supported: true, enabled: false }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          });
        }
        return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } });
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it('renders the unified TX Audio Profile bar and live shaping controls', async () => {
    const { container, unmount } = render(createElement(TxFidelityPanel));
    await act(async () => {
      for (let i = 0; i < 8; i++) await Promise.resolve();
    });

    // The shared profile bar is mounted (Save TX Audio Profile button).
    expect(container.querySelector('[aria-label="Save TX audio profile"]')).not.toBeNull();
    // Live shaping number boxes use the controlled-input pattern.
    expect(container.querySelector('[aria-label="TX mic gain"]')).not.toBeNull();
    expect(container.querySelector('[aria-label="TX leveler max gain"]')).not.toBeNull();
    expect(container.querySelector('[aria-label="TX filter low cut"]')).not.toBeNull();

    // The profile dropdown is populated from the unified store and shows the
    // last-loaded id as selected.
    const select = container.querySelector('[aria-label="TX audio profile"]') as HTMLSelectElement;
    expect(select).not.toBeNull();
    // The studio-ssb option is rendered from the unified store list and shown
    // as selected (the last-loaded pointer).
    const options = Array.from(select.querySelectorAll('option')).map((o) => o.value);
    expect(options).toContain('studio-ssb');
    expect(select.value).toBe('studio-ssb');

    unmount();
  });
});
