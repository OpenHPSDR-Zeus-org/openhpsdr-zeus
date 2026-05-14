// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// RadioOptionsPanel — checkbox renders, toggles, calls PUT with the right
// body, surfaces the caption text. Fetch mocked with vi.stubGlobal to
// match the pattern other panel tests use (see vst-host-store.test.ts).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { RadioOptionsPanel } from './RadioOptionsPanel';
import { useRadioOptionsStore } from '../state/radio-options-store';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

function resetStore() {
  useRadioOptionsStore.setState({
    options: { bandVolts: false },
    loaded: false,
    inflight: false,
    error: null,
  });
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
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ bandVolts: false }));
    vi.stubGlobal('fetch', fetchMock);

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
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(jsonResponse({ bandVolts: true }));
    vi.stubGlobal('fetch', fetchMock);

    await act(async () => {
      root.render(<RadioOptionsPanel />);
    });
    await flushMicrotasks();

    expect(fetchMock).toHaveBeenCalled();
    const firstCallUrl = fetchMock.mock.calls[0]![0];
    expect(firstCallUrl).toBe('/api/radio/hl2-options');

    const checkbox = container.querySelector<HTMLInputElement>(
      'input[type="checkbox"]',
    );
    expect(checkbox!.checked).toBe(true);
  });

  it('toggles state and PUTs the new value with the correct body', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      // GET on mount
      .mockResolvedValueOnce(jsonResponse({ bandVolts: false }))
      // PUT after click
      .mockResolvedValueOnce(jsonResponse({ bandVolts: true }));
    vi.stubGlobal('fetch', fetchMock);

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

    expect(fetchMock).toHaveBeenCalledTimes(2);
    const [url, init] = fetchMock.mock.calls[1]!;
    expect(url).toBe('/api/radio/hl2-options');
    expect(init?.method).toBe('PUT');
    expect(init?.headers).toMatchObject({ 'content-type': 'application/json' });
    expect(JSON.parse((init?.body ?? '') as string)).toEqual({
      bandVolts: true,
    });

    expect(useRadioOptionsStore.getState().options.bandVolts).toBe(true);
  });
});
