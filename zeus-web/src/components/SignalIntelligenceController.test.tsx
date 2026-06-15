// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DISPLAY_INTELLIGENCE_DEFAULTS } from '../api/display-intelligence';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';
import { SignalIntelligenceController } from './SignalIntelligenceController';

describe('SignalIntelligenceController display-intelligence sync', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    vi.useFakeTimers();
    useSignalEnhanceStore.getState().applySignalEnhanceSettings(DISPLAY_INTELLIGENCE_DEFAULTS);
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    useSignalEnhanceStore.getState().applySignalEnhanceSettings(DISPLAY_INTELLIGENCE_DEFAULTS);
  });

  const jsonResponse = (body: unknown): Response =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });

  async function flushPromises() {
    await Promise.resolve();
    await Promise.resolve();
  }

  it('hydrates Signal Intelligence from the backend policy', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      ...DISPLAY_INTELLIGENCE_DEFAULTS,
      profileId: 'dx',
      popEnabled: true,
      snapRadiusHz: 5000,
    }));
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    const state = useSignalEnhanceStore.getState();
    expect(state.profileId).toBe('dx');
    expect(state.popEnabled).toBe(true);
    expect(state.snapRadiusHz).toBe(5000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('debounces backend saves when operator settings change', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockImplementation(async (_input, init) =>
      jsonResponse(init?.method === 'PUT'
        ? { ...DISPLAY_INTELLIGENCE_DEFAULTS, popEnabled: true }
        : DISPLAY_INTELLIGENCE_DEFAULTS),
    );
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    act(() => {
      useSignalEnhanceStore.getState().setPopEnabled(true);
    });

    await act(async () => {
      vi.advanceTimersByTime(400);
      await flushPromises();
    });

    const putCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT');
    expect(putCall).toBeDefined();
    expect(putCall![0]).toBe('/api/dsp/display-intelligence');
    expect(JSON.parse((putCall![1]?.body ?? '') as string)).toMatchObject({
      profileId: 'balanced',
      popEnabled: true,
      snapEnabled: false,
    });
  });

  it('cancels a pending save when settings return to the saved policy', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse(DISPLAY_INTELLIGENCE_DEFAULTS));
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    act(() => {
      useSignalEnhanceStore.getState().setPopEnabled(true);
      useSignalEnhanceStore.getState().setPopEnabled(false);
    });

    await act(async () => {
      vi.advanceTimersByTime(400);
      await flushPromises();
    });

    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false);
  });

  it('does not let a late backend hydrate overwrite a local operator change', async () => {
    let resolveFetch!: (response: Response) => void;
    const fetchPromise = new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    });
    const fetchMock = vi.fn<typeof fetch>().mockReturnValue(fetchPromise);
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    act(() => {
      useSignalEnhanceStore.getState().applySignalEnhanceProfile('cw');
    });

    resolveFetch(jsonResponse({
      ...DISPLAY_INTELLIGENCE_DEFAULTS,
      profileId: 'dx',
      popEnabled: true,
    }));

    await act(async () => {
      await flushPromises();
    });

    expect(useSignalEnhanceStore.getState().profileId).toBe('cw');
  });

  it('merges an early Pop toggle into the server policy instead of resetting untouched settings', async () => {
    let resolveFetch!: (response: Response) => void;
    const fetchPromise = new Promise<Response>((resolve) => {
      resolveFetch = resolve;
    });
    const fetchMock = vi.fn<typeof fetch>().mockImplementation((_input, init) => {
      if (init?.method === 'PUT') return Promise.resolve(jsonResponse(JSON.parse(init.body as string)));
      return fetchPromise;
    });
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    act(() => {
      useSignalEnhanceStore.getState().setPopEnabled(true);
    });

    resolveFetch(jsonResponse({
      ...DISPLAY_INTELLIGENCE_DEFAULTS,
      profileId: 'dx',
      popEnabled: false,
      snapEnabled: true,
      snapRadiusHz: 5000,
    }));

    await act(async () => {
      await flushPromises();
    });

    const state = useSignalEnhanceStore.getState();
    expect(state.popEnabled).toBe(true);
    expect(state.profileId).toBe('dx');
    expect(state.snapEnabled).toBe(true);
    expect(state.snapRadiusHz).toBe(5000);

    await act(async () => {
      vi.advanceTimersByTime(400);
      await flushPromises();
    });

    const putCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT');
    expect(putCall).toBeDefined();
    expect(JSON.parse((putCall![1]?.body ?? '') as string)).toMatchObject({
      profileId: 'dx',
      popEnabled: true,
      snapEnabled: true,
      snapRadiusHz: 5000,
    });
  });
});
