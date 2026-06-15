// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../../components/meters/__tests__/harness';
import { fetchTxStationProfiles } from '../../api/client';
import { txStationProfileToDto, STUDIO_SSB_PROFILE } from '../../audio/tx-station-profile';
import { TxFidelityPanel } from './TxFidelityPanel';

vi.mock('../../api/client', async () => {
  const actual = await vi.importActual<typeof import('../../api/client')>('../../api/client');
  return {
    ...actual,
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
                  order: ['com.openhpsdr.zeus.samples.compressor'],
                  parked: [],
                  masterBypass: false,
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
    expect(container.textContent).toContain('Saved operator profile');

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
    expect(container.textContent).toContain('2.9k SSB');

    await act(async () => {
      profileOptions.find((option) => option.textContent === 'DX Punch')!.click();
    });

    expect(container.textContent).toContain('DX CFC');
    expect(
      Array.from(container.querySelectorAll<HTMLButtonElement>('button')).some(
        (button) => button.textContent === 'Apply',
      ),
    ).toBe(true);
    expect(container.querySelector<HTMLInputElement>('input[aria-label="TX spectral density"]')?.value).toBe('100');
    expect(container.querySelector<HTMLInputElement>('input[aria-label="TX profile high cut"]')?.value).toBe('2850');

    const routeButtons = Array.from(
      container.querySelectorAll<HTMLButtonElement>(
        '[aria-label="TX profile audio route"] button',
      ),
    );
    expect(routeButtons.map((button) => button.textContent)).toEqual([
      'Native',
      'VST',
    ]);
    expect(routeButtons[1]?.getAttribute('aria-pressed')).toBe('true');

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
      'Use current rack',
      'ESSB Broadcast',
    ]);

    unmount();
  });
});
