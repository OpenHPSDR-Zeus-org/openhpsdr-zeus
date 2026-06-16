// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { NR_CONFIG_DEFAULT } from '../api/client';
import { DISPLAY_INTELLIGENCE_DEFAULTS } from '../api/display-intelligence';
import { resetEstimator, useSignalEnhanceStore } from '../dsp/signal-estimator';
import type { DecodedFrame } from '../realtime/frame';
import { useConnectionStore } from '../state/connection-store';
import {
  _resetFrameConsumerCount,
  hasActiveFrameConsumers,
  useDisplayStore,
} from '../state/display-store';
import { useNotchStore } from '../state/notch-store';
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
    resetEstimator();
    _resetFrameConsumerCount();
    useConnectionStore.setState({ status: 'Disconnected', mode: 'USB', nr: { ...NR_CONFIG_DEFAULT } });
    useDisplayStore.setState({
      connected: false,
      width: 0,
      centerHz: 0n,
      hzPerPixel: 0,
      panDb: null,
      wfDb: null,
      panValid: false,
      wfValid: false,
      lastSeq: 0,
    });
    useNotchStore.setState({ notches: [], armed: false, pending: null });
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

  function frameWithWeakCarrier(seq = 1): DecodedFrame {
    const width = 256;
    const panDb = new Float32Array(width).fill(-110);
    panDb[150] = -78;
    return {
      msgType: 1,
      headerFlags: 0,
      seq,
      tsUnixMs: Date.now(),
      rxId: 0,
      bodyFlags: 1,
      panValid: true,
      wfValid: false,
      width,
      centerHz: 7_251_000n,
      hzPerPixel: 100,
      panDb,
      wfDb: new Float32Array(width),
    };
  }

  function frameWithEmfBar(seq = 1): DecodedFrame {
    const frame = frameWithWeakCarrier(seq);
    frame.panDb.fill(-110);
    frame.panDb[149] = -94;
    frame.panDb[150] = -78;
    frame.panDb[151] = -94;
    return frame;
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

  it('publishes live scene metrics for diagnostics even when Auto Profile is off', async () => {
    vi.setSystemTime(new Date('2026-06-15T01:00:00Z'));
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse(DISPLAY_INTELLIGENCE_DEFAULTS));
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    expect(useSignalEnhanceStore.getState().autoProfileEnabled).toBe(false);

    act(() => {
      useDisplayStore.getState().pushFrame(frameWithWeakCarrier());
    });

    const scene = useSignalEnhanceStore.getState().sceneStatus;
    expect(scene).not.toBeNull();
    expect(scene?.maxSnrDb).toBeGreaterThan(20);
    expect(scene?.occupiedPct).toBeGreaterThan(0);
    expect(useSignalEnhanceStore.getState().autoProfileEnabled).toBe(false);
  });

  it('syncs persistent narrow EMF bars into auto notches while ANF is enabled', async () => {
    vi.setSystemTime(new Date('2026-06-15T01:05:00Z'));
    const fetchMock = vi.fn<typeof fetch>().mockImplementation(async (_input, init) =>
      jsonResponse(init?.method === 'POST'
        ? []
        : DISPLAY_INTELLIGENCE_DEFAULTS),
    );
    vi.stubGlobal('fetch', fetchMock);
    useConnectionStore.setState({
      status: 'Connected',
      mode: 'USB',
      nr: { ...NR_CONFIG_DEFAULT, anfEnabled: true },
    });

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    expect(useSignalEnhanceStore.getState().autoNotchEnabled).toBe(false);

    act(() => {
      useDisplayStore.getState().pushFrame(frameWithEmfBar(1));
    });
    expect(useNotchStore.getState().notches.filter((n) => n.source === 'auto')).toEqual([]);

    await act(async () => {
      vi.advanceTimersByTime(1000);
      useDisplayStore.getState().pushFrame(frameWithEmfBar(2));
      vi.advanceTimersByTime(1000);
      useDisplayStore.getState().pushFrame(frameWithEmfBar(3));
      vi.advanceTimersByTime(1000);
      useDisplayStore.getState().pushFrame(frameWithEmfBar(4));
      await flushPromises();
    });

    const autoNotches = useNotchStore.getState().notches.filter((n) => n.source === 'auto');
    expect(autoNotches).toHaveLength(1);
    expect(autoNotches[0]!.centerHz).toBe(7_253_200);
    expect(autoNotches[0]!.widthHz).toBeGreaterThanOrEqual(240);

    await act(async () => {
      vi.advanceTimersByTime(130);
      await flushPromises();
    });

    const postCall = fetchMock.mock.calls.find(
      ([url, init]) => url === '/api/rx/notches' && init?.method === 'POST',
    );
    expect(postCall).toBeDefined();
    expect(JSON.parse((postCall![1]?.body ?? '{}') as string)).toEqual({
      notches: [
        {
          centerHz: 7_253_200,
          widthHz: autoNotches[0]!.widthHz,
          active: true,
          source: 'auto',
        },
      ],
    });
  });

  it('keeps legacy Auto Notch settings inactive until ANF is enabled', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({
      ...DISPLAY_INTELLIGENCE_DEFAULTS,
      autoNotchEnabled: true,
    }));
    vi.stubGlobal('fetch', fetchMock);
    useConnectionStore.setState({
      status: 'Connected',
      mode: 'USB',
      nr: { ...NR_CONFIG_DEFAULT, anfEnabled: false },
    });

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    await act(async () => {
      useDisplayStore.getState().pushFrame(frameWithEmfBar(1));
      vi.advanceTimersByTime(1000);
      useDisplayStore.getState().pushFrame(frameWithEmfBar(2));
      vi.advanceTimersByTime(1000);
      useDisplayStore.getState().pushFrame(frameWithEmfBar(3));
      await flushPromises();
    });

    expect(useSignalEnhanceStore.getState().autoNotchEnabled).toBe(true);
    expect(useNotchStore.getState().notches.filter((n) => n.source === 'auto')).toEqual([]);
  });

  it('keeps display frame decoding active for realtime diagnostics while mounted', async () => {
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse(DISPLAY_INTELLIGENCE_DEFAULTS));
    vi.stubGlobal('fetch', fetchMock);

    expect(hasActiveFrameConsumers()).toBe(false);

    await act(async () => {
      root.render(<SignalIntelligenceController />);
      await flushPromises();
    });

    expect(hasActiveFrameConsumers()).toBe(true);

    act(() => {
      root.unmount();
    });

    expect(hasActiveFrameConsumers()).toBe(false);
  });
});
