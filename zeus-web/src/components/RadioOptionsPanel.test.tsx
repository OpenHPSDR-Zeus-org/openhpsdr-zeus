// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RadioOptionsPanel — checkbox renders, toggles, calls PUT with the right
// body, surfaces the caption text. Fetch mocked with vi.stubGlobal to
// match the pattern other panel tests use.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { RadioOptionsPanel } from './RadioOptionsPanel';
import { useRadioOptionsStore } from '../state/radio-options-store';
import { useRadioStore } from '../state/radio-store';
import { UNKNOWN_BOARD_CAPABILITIES } from '../api/board-capabilities';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function resetStore() {
  useRadioOptionsStore.setState({
    options: { bandVolts: false },
    g2Options: {
      ditherEnabled: true,
      randomEnabled: true,
      maxRxFreqMHz: 60,
      supported: false,
      rx1AttenuatorDb: 0,
      rx1AttenuatorMinDb: 0,
      rx1AttenuatorMaxDb: 31,
      rx1AttenuatorSupported: false,
    },
    loaded: false,
    inflight: false,
    error: null,
  });
  useRadioStore.setState((s) => ({
    ...s,
    capabilities: UNKNOWN_BOARD_CAPABILITIES,
  }));
}

function stubRadioOptionsFetch(
  hl2: Record<string, unknown> = { bandVolts: false },
  g2: Record<string, unknown> = {
    ditherEnabled: true,
    randomEnabled: true,
    maxRxFreqMHz: 60,
    supported: false,
    rx1AttenuatorDb: 0,
    rx1AttenuatorMinDb: 0,
    rx1AttenuatorMaxDb: 31,
    rx1AttenuatorSupported: false,
  },
) {
  const fetchMock = vi.fn<typeof fetch>(async (input, init) => {
    const url = typeof input === 'string'
      ? input
      : input instanceof URL
        ? input.toString()
        : input.url;
    if (url === '/api/radio/hl2-options') {
      return jsonResponse(init?.method === 'PUT' ? JSON.parse((init.body ?? '{}') as string) : hl2);
    }
    if (url === '/api/radio/g2-options') {
      if (init?.method === 'PUT') {
        return jsonResponse({
          ...g2,
          ...JSON.parse((init.body ?? '{}') as string),
          supported: true,
        });
      }
      return jsonResponse(g2);
    }
    return jsonResponse({});
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

// Resolves after every microtask in the current scheduler queue — used to
// let the panel's mount-effect-driven `load()` settle before assertions.
async function flushMicrotasks() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

describe('RadioOptionsPanel', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    resetStore();
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    vi.unstubAllGlobals();
  });

  it('renders the Band Volts checkbox and caption text', async () => {
    useRadioStore.setState((s) => ({
      ...s,
      capabilities: {
        ...UNKNOWN_BOARD_CAPABILITIES,
        hasHl2OptionalToggles: true,
      },
    }));
    stubRadioOptionsFetch({ bandVolts: false });

    await act(async () => {
      root.render(<RadioOptionsPanel />);
    });
    await flushMicrotasks();

    expect(container.textContent).toContain('Band Volts');
    expect(container.textContent).toContain('Enable Band Volts PWM output');
    expect(container.textContent).toContain('Xiegu XPA125B');
    expect(container.textContent).toContain('hermes-lite2-protocol.md');

    const checkbox = container.querySelector<HTMLInputElement>(
      'input[type="checkbox"]',
    );
    expect(checkbox).not.toBeNull();
    expect(checkbox!.checked).toBe(false);
  });

  it('loads the initial value from GET /api/radio/hl2-options', async () => {
    useRadioStore.setState((s) => ({
      ...s,
      capabilities: {
        ...UNKNOWN_BOARD_CAPABILITIES,
        hasHl2OptionalToggles: true,
      },
    }));
    const fetchMock = stubRadioOptionsFetch({ bandVolts: true });

    await act(async () => {
      root.render(<RadioOptionsPanel />);
    });
    await flushMicrotasks();

    expect(fetchMock).toHaveBeenCalledWith('/api/radio/hl2-options', expect.anything());

    const checkbox = container.querySelector<HTMLInputElement>(
      'input[type="checkbox"]',
    );
    expect(checkbox!.checked).toBe(true);
  });

  it('toggles state and PUTs the new value with the correct body', async () => {
    useRadioStore.setState((s) => ({
      ...s,
      capabilities: {
        ...UNKNOWN_BOARD_CAPABILITIES,
        hasHl2OptionalToggles: true,
      },
    }));
    const fetchMock = stubRadioOptionsFetch({ bandVolts: false });

    await act(async () => {
      root.render(<RadioOptionsPanel />);
    });
    await flushMicrotasks();

    const checkbox = container.querySelector<HTMLInputElement>(
      'input[type="checkbox"]',
    );
    expect(checkbox).not.toBeNull();

    await act(async () => {
      checkbox!.click();
    });
    await flushMicrotasks();

    const putCall = fetchMock.mock.calls.find(
      ([url, init]) => url === '/api/radio/hl2-options' && init?.method === 'PUT',
    );
    expect(putCall).toBeDefined();
    const [url, init] = putCall!;
    expect(url).toBe('/api/radio/hl2-options');
    expect(init?.method).toBe('PUT');
    expect(init?.headers).toMatchObject({ 'content-type': 'application/json' });
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      bandVolts: true,
    });

    expect(useRadioOptionsStore.getState().options.bandVolts).toBe(true);
  });

  it('renders and updates ANAN-G2 dither/random options', async () => {
    useRadioStore.setState((s) => ({
      ...s,
      capabilities: {
        ...UNKNOWN_BOARD_CAPABILITIES,
        supportsG2AdcOptions: true,
      },
    }));
    const fetchMock = stubRadioOptionsFetch(
      { bandVolts: false },
      {
        ditherEnabled: true,
        randomEnabled: true,
        maxRxFreqMHz: 60,
        supported: true,
        rx1AttenuatorDb: 7,
        rx1AttenuatorMinDb: 0,
        rx1AttenuatorMaxDb: 31,
        rx1AttenuatorSupported: true,
      },
    );

    await act(async () => {
      root.render(<RadioOptionsPanel />);
    });
    await flushMicrotasks();

    expect(container.textContent).toContain('ANAN-G2 Options');
    expect(container.textContent).toContain('Dither Enabled');
    expect(container.textContent).toContain('Random Enabled');
    expect(container.textContent).toContain('MaxRXFreq');
    expect(container.textContent).toContain('60.00 MHz');
    expect(container.textContent).toContain('ADC1 / RX2 Step Attenuator');

    const checkboxes = Array.from(
      container.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
    );
    expect(checkboxes).toHaveLength(2);
    const rx1Select = container.querySelector<HTMLSelectElement>('select');
    expect(rx1Select).not.toBeNull();
    expect(rx1Select!.value).toBe('7');
    expect(rx1Select!.querySelectorAll('option')).toHaveLength(32);

    await act(async () => {
      checkboxes[0]!.click();
    });
    await flushMicrotasks();

    const ditherPut = fetchMock.mock.calls.find(
      ([url, init]) => url === '/api/radio/g2-options'
        && init?.method === 'PUT'
        && JSON.parse((init.body ?? '{}') as string).ditherEnabled === false,
    );
    expect(ditherPut).toBeDefined();
    expect(useRadioOptionsStore.getState().g2Options.ditherEnabled).toBe(false);

    await act(async () => {
      rx1Select!.value = '12';
      rx1Select!.dispatchEvent(new Event('change', { bubbles: true }));
    });
    await flushMicrotasks();

    const rx1Put = fetchMock.mock.calls.find(
      ([url, init]) => url === '/api/radio/g2-options'
        && init?.method === 'PUT'
        && JSON.parse((init.body ?? '{}') as string).rx1AttenuatorDb === 12,
    );
    expect(rx1Put).toBeDefined();
    expect(useRadioOptionsStore.getState().g2Options.rx1AttenuatorDb).toBe(12);
  });

  it('renders Protocol-1 LT2208 ADC options via runtime supported flag', async () => {
    // No static G2 capability (a legacy P1 board), but the server reports
    // g2Options.supported = true once a non-HL2 P1 radio is connected. The card
    // must render dither/random with the board-neutral title and omit the
    // G2-only MaxRXFreq / RX1-attenuator rows.
    useRadioStore.setState((s) => ({
      ...s,
      capabilities: UNKNOWN_BOARD_CAPABILITIES, // supportsG2AdcOptions = false
    }));
    stubRadioOptionsFetch(
      { bandVolts: false },
      {
        ditherEnabled: false,
        randomEnabled: false,
        maxRxFreqMHz: 60,
        supported: true,
        rx1AttenuatorDb: 0,
        rx1AttenuatorMinDb: 0,
        rx1AttenuatorMaxDb: 31,
        rx1AttenuatorSupported: false,
      },
    );

    await act(async () => {
      root.render(<RadioOptionsPanel />);
    });
    await flushMicrotasks();

    expect(container.textContent).toContain('ADC Options');
    expect(container.textContent).not.toContain('ANAN-G2 Options');
    expect(container.textContent).toContain('Dither Enabled');
    expect(container.textContent).toContain('Random Enabled');
    // G2-only rows are gated out on the P1 path.
    expect(container.textContent).not.toContain('MaxRXFreq');
    expect(container.querySelector('select')).toBeNull();

    const checkboxes = Array.from(
      container.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'),
    );
    expect(checkboxes).toHaveLength(2);
    expect(checkboxes[0]!.checked).toBe(false);
    expect(checkboxes[1]!.checked).toBe(false);
  });
});
