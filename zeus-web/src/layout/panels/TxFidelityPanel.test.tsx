// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../../components/meters/__tests__/harness';
import {
  fetchTxFidelityPolicy,
  fetchTxStationProfiles,
  saveTxFidelityPolicy,
} from '../../api/client';
import {
  txStationProfileToDto,
  DX_PROFILE,
  STUDIO_SSB_PROFILE,
} from '../../audio/tx-station-profile';
import { useConnectionStore } from '../../state/connection-store';
import { TxFidelityPanel } from './TxFidelityPanel';

vi.mock('../../api/client', async () => {
  const actual = await vi.importActual<typeof import('../../api/client')>('../../api/client');
  return {
    ...actual,
    fetchTxFidelityPolicy: vi.fn(() =>
      Promise.resolve({ profileId: 'studio-ssb', targetSpectralDensity: 55 }),
    ),
    saveTxFidelityPolicy: vi.fn(async (policy: unknown) => policy),
    fetchTxStationProfiles: vi.fn(() => Promise.resolve([])),
    saveTxStationProfile: vi.fn(async (profile: unknown) => profile),
    resetTxStationProfile: vi.fn(async () => undefined),
  };
});

describe('TxFidelityPanel', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url === '/api/audio-suite/profiles') {
          return new Response(
            JSON.stringify({
              profiles: [
                {
                  name: 'ESSB Broadcast',
                  processingMode: 'vst',
                  order: ['com.openhpsdr.zeus.samples.compressor'],
                  parked: [],
                  masterBypass: false,
                  createdUtc: '2026-06-15T00:00:00Z',
                  updatedUtc: '2026-06-15T00:00:00Z',
                },
                {
                  name: 'Native x1',
                  processingMode: 'native',
                  order: ['com.openhpsdr.zeus.samples.eq'],
                  parked: [],
                  masterBypass: true,
                  createdUtc: '2026-06-15T00:00:00Z',
                  updatedUtc: '2026-06-15T00:00:00Z',
                },
              ],
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          );
        }
        if (url === '/api/audio-suite/processing-mode') {
          return new Response(
            JSON.stringify({
              mode: 'native',
              engineAvailable: true,
              engineActive: false,
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          );
        }
        if (url === '/api/audio-suite/chain/meters') {
          return new Response(
            JSON.stringify({
              inputPeak: 0.01,
              outputPeak: 0.02,
              inputDb: -54,
              outputDb: -48,
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          );
        }
        if (url === '/api/tx/leveling' || url === '/api/tx-filter' || url === '/api/tx/cfc') {
          return new Response(JSON.stringify({ status: 'Connected', mode: 'USB' }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          });
        }
        return new Response('{}', {
          status: 200,
          headers: { 'content-type': 'application/json' },
        });
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    useConnectionStore.setState({
      status: 'Disconnected',
      mode: 'USB',
    });
  });

  it('loads saved station profile labels into the selector', async () => {
    vi.mocked(fetchTxStationProfiles).mockResolvedValueOnce([
      {
        ...txStationProfileToDto(STUDIO_SSB_PROFILE),
        label: 'My Studio',
        summary: 'Saved operator profile',
      },
    ]);

    const { container, unmount } = render(createElement(TxFidelityPanel));
    expect(container.textContent).toContain('Audio Suite Chain');

    await act(async () => {
      await Promise.resolve();
    });

    const profileButton = container.querySelector<HTMLButtonElement>(
      'button[aria-label="TX station profile"]',
    );
    expect(profileButton).not.toBeNull();
    expect(profileButton!.textContent).toContain('My Studio');

    await act(async () => {
      profileButton!.click();
    });

    const profileOptions = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="option"]'),
    );
    expect(profileOptions.map((option) => option.textContent)).toEqual([
      'My Studio',
      'eSSB Wide',
      'DX Punch',
    ]);
    expect(container.textContent).toContain('density 55 CFC presence');
    expect(container.textContent).toContain('SSB 150..2900 Hz');

    unmount();
  });

  it('suggests and binds a matching Audio Suite chain profile', async () => {
    const { container, unmount } = render(createElement(TxFidelityPanel));

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    const profileButton = container.querySelector<HTMLButtonElement>(
      'button[aria-label="TX station profile"]',
    );
    expect(profileButton).not.toBeNull();

    await act(async () => {
      profileButton!.click();
    });

    const profileOptions = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="option"]'),
    );
    await act(async () => {
      profileOptions.find((option) => option.textContent === 'eSSB Wide')!.click();
    });

    expect(container.textContent).toContain('Suggested chain: ESSB Broadcast');

    const bindButton = Array.from(container.querySelectorAll<HTMLButtonElement>('button')).find(
      (button) => button.textContent === 'Bind',
    );
    expect(bindButton).not.toBeNull();

    await act(async () => {
      bindButton!.click();
    });

    expect(container.textContent).toContain('ESSB Broadcast');

    unmount();
  });

  it('loads the persisted TX fidelity policy into the station selector', async () => {
    vi.mocked(fetchTxFidelityPolicy).mockResolvedValueOnce({
      profileId: 'dx',
      targetSpectralDensity: 100,
    });

    const { container, unmount } = render(createElement(TxFidelityPanel));

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    const profileButton = container.querySelector<HTMLButtonElement>(
      'button[aria-label="TX station profile"]',
    );
    expect(profileButton).not.toBeNull();
    expect(profileButton!.textContent).toContain('DX Punch');
    expect(container.querySelector<HTMLInputElement>('input[aria-label="TX spectral density"]')?.value).toBe('100');
    expect(container.textContent).toContain('DENS --/100');

    unmount();
  });

  it('renders configurable station profiles and updates the profile summary', async () => {
    const { container, unmount } = render(createElement(TxFidelityPanel));

    const profileButton = container.querySelector<HTMLButtonElement>(
      'button[aria-label="TX station profile"]',
    );
    expect(profileButton).not.toBeNull();
    expect(profileButton!.textContent).toContain('Studio SSB');

    await act(async () => {
      profileButton!.click();
    });

    const profileOptions = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="option"]'),
    );
    expect(profileOptions.map((option) => option.textContent)).toEqual([
      'Studio SSB',
      'eSSB Wide',
      'DX Punch',
    ]);
    expect(container.textContent).toContain('density 55 CFC presence');
    expect(container.textContent).toContain('SSB 150..2900 Hz');

    await act(async () => {
      profileOptions.find((option) => option.textContent === 'DX Punch')!.click();
    });

    expect(saveTxFidelityPolicy).toHaveBeenCalledWith({
      profileId: 'dx',
      targetSpectralDensity: 100,
    });
    expect(container.textContent).toContain('DX Punch selected / connect to apply');
    expect(container.querySelector<HTMLInputElement>('input[aria-label="TX spectral density"]')?.value).toBe('100');
    expect(container.querySelector<HTMLInputElement>('input[aria-label="TX profile high cut"]')?.value).toBe('2850');
    expect(container.textContent).toContain('DENS --/100');
    expect(container.querySelector('[aria-label="TX profile audio route"]')).toBeNull();
    expect(container.querySelector('[aria-label="TX profile audio suite rack"]')).toBeNull();
    expect(
      Array.from(container.querySelectorAll<HTMLButtonElement>('button')).some(
        (button) => button.textContent === 'Apply',
      ),
    ).toBe(false);

    const audioProfileButton = container.querySelector<HTMLButtonElement>(
      'button[aria-label="TX profile audio suite profile"]',
    );
    expect(audioProfileButton).not.toBeNull();

    await act(async () => {
      audioProfileButton!.click();
    });

    const audioProfileOptions = Array.from(
      container.querySelectorAll<HTMLButtonElement>(
        '[aria-label="TX profile audio suite profile options"] [role="option"]',
      ),
    );
    expect(audioProfileOptions.map((option) => option.textContent)).toEqual([
      'Use current chain',
      'ESSB Broadcast',
      'Native x1',
    ]);

    await act(async () => {
      audioProfileOptions[1]!.click();
    });
    expect(container.textContent).toContain('ESSB Broadcast');
    expect(container.textContent).toContain('VST route / rack hot saved in chain profile');

    await act(async () => {
      audioProfileButton!.click();
    });
    const refreshedAudioProfileOptions = Array.from(
      container.querySelectorAll<HTMLButtonElement>(
        '[aria-label="TX profile audio suite profile options"] [role="option"]',
      ),
    );
    await act(async () => {
      refreshedAudioProfileOptions.find((option) => option.textContent === 'Native x1')!.click();
    });
    expect(container.textContent).toContain('Native x1');
    expect(container.textContent).toContain('Native route / rack bypass saved in chain profile');

    unmount();
  });

  it('auto-applies the bound chain profile when a station profile is selected', async () => {
    act(() => {
      useConnectionStore.setState({
        status: 'Connected',
        mode: 'USB',
      });
    });
    vi.mocked(fetchTxStationProfiles).mockResolvedValueOnce([
      {
        ...txStationProfileToDto(DX_PROFILE),
        audioSuiteProfileName: 'ESSB Broadcast',
      },
    ]);

    const { container, unmount } = render(createElement(TxFidelityPanel));

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    const profileButton = container.querySelector<HTMLButtonElement>(
      'button[aria-label="TX station profile"]',
    );
    expect(profileButton).not.toBeNull();

    await act(async () => {
      profileButton!.click();
    });

    const profileOptions = Array.from(
      container.querySelectorAll<HTMLButtonElement>('[role="option"]'),
    );
    await act(async () => {
      profileOptions.find((option) => option.textContent === 'DX Punch')!.click();
    });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
      await Promise.resolve();
    });

    const fetchUrls = vi
      .mocked(fetch)
      .mock.calls.map(([input]) => String(input));
    expect(fetchUrls).toContain('/api/audio-suite/profiles/ESSB%20Broadcast/apply');
    expect(fetchUrls).toContain('/api/mic-gain');
    expect(fetchUrls).toContain('/api/tx/leveler-max-gain');
    expect(fetchUrls).toContain('/api/tx/leveling');
    expect(fetchUrls).toContain('/api/tx-filter');
    expect(fetchUrls).toContain('/api/tx/cfc');
    expect(container.textContent).toContain('DX Punch active');
    expect(
      Array.from(container.querySelectorAll<HTMLButtonElement>('button')).some(
        (button) => button.textContent === 'Apply',
      ),
    ).toBe(false);

    unmount();
  });
});
