// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DEFAULT_CW_SETTINGS } from '../api/cw';
import { useCwStore } from './cw-store';

// The store hydrates on module load via a fetch — stub it to a no-op
// before each test so we're always working from the API defaults.
const originalFetch = globalThis.fetch;

beforeEach(() => {
  useCwStore.setState({
    settings: { ...DEFAULT_CW_SETTINGS, macros: [...DEFAULT_CW_SETTINGS.macros] },
    status: {
      state: 'idle',
      text: '',
      wpm: 0,
      queueDepth: 0,
      receivedAtMs: 0,
    },
  });
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  vi.restoreAllMocks();
});

describe('cw-store', () => {
  it('seeds the default settings', () => {
    const { settings } = useCwStore.getState();
    expect(settings.wpm).toBe(22);
    expect(settings.macros).toHaveLength(6);
    expect(settings.macros[0]).toBe('CQ CQ CQ');
  });

  it('patchSettings PUTs once and applies the server response', async () => {
    // Server normalises the wpm to a clamped value — store must re-apply.
    const fetchSpy = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ...DEFAULT_CW_SETTINGS,
        wpm: 40,  // server clamped from a hypothetical 999
      }),
    });
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    await useCwStore.getState().patchSettings({ wpm: 999 });

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const call = fetchSpy.mock.calls[0]!;
    expect(call[0]).toBe('/api/cw/settings');
    expect(call[1]).toMatchObject({ method: 'PUT' });
    const body = JSON.parse(call[1].body as string);
    expect(body).toEqual({ wpm: 999 });
    // Re-applied with server-normalised value.
    expect(useCwStore.getState().settings.wpm).toBe(40);
  });

  it('patchSettings reverts the optimistic update on network failure', async () => {
    globalThis.fetch = (() => Promise.reject(new Error('offline'))) as unknown as typeof fetch;

    await useCwStore.getState().patchSettings({ wpm: 30 });

    // Reverted to the seeded default — the operator sees their edit roll
    // back, which is the honest signal that the save failed.
    expect(useCwStore.getState().settings.wpm).toBe(22);
  });

  it('setMacro PATCHes only the macros field', async () => {
    const fetchSpy = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ...DEFAULT_CW_SETTINGS,
        macros: ['HELLO', 'TU 73', 'QRZ?', 'AGN?', '5NN TU', 'UR RST'],
      }),
    });
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    await useCwStore.getState().setMacro(0, 'HELLO');

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const body = JSON.parse(fetchSpy.mock.calls[0]![1].body as string);
    // Only macros is in the body — wpm, sidetone etc. stay server-side.
    expect(Object.keys(body)).toEqual(['macros']);
    expect(body.macros[0]).toBe('HELLO');
    expect(body.macros).toHaveLength(6);
    expect(useCwStore.getState().settings.macros[0]).toBe('HELLO');
  });

  it('addMacro appends an empty slot and PUTs', async () => {
    const fetchSpy = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ...DEFAULT_CW_SETTINGS,
        macros: [...DEFAULT_CW_SETTINGS.macros, ''],
      }),
    });
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    await useCwStore.getState().addMacro();

    const body = JSON.parse(fetchSpy.mock.calls[0]![1].body as string);
    expect(body.macros).toHaveLength(DEFAULT_CW_SETTINGS.macros.length + 1);
    expect(body.macros[DEFAULT_CW_SETTINGS.macros.length]).toBe('');
    expect(useCwStore.getState().settings.macros).toHaveLength(
      DEFAULT_CW_SETTINGS.macros.length + 1,
    );
  });

  it('removeMacro drops the named index and PUTs', async () => {
    const fetchSpy = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        ...DEFAULT_CW_SETTINGS,
        macros: DEFAULT_CW_SETTINGS.macros.filter((_, i) => i !== 2),
      }),
    });
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    await useCwStore.getState().removeMacro(2);

    const body = JSON.parse(fetchSpy.mock.calls[0]![1].body as string);
    expect(body.macros).toHaveLength(DEFAULT_CW_SETTINGS.macros.length - 1);
    expect(body.macros).not.toContain(DEFAULT_CW_SETTINGS.macros[2]);
  });

  it('commitDebounced only PUTs once for a burst of calls', async () => {
    // Regression for the "slider snaps back" bug: rapid pointer moves
    // used to fire one PUT per tick, and out-of-order responses could
    // clobber the operator's final value. commitDebounced coalesces.
    vi.useFakeTimers();
    const fetchSpy = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ ...DEFAULT_CW_SETTINGS, wpm: 35 }),
    });
    globalThis.fetch = fetchSpy as unknown as typeof fetch;

    const { commitDebounced } = useCwStore.getState();
    commitDebounced({ wpm: 30 });
    commitDebounced({ wpm: 31 });
    commitDebounced({ wpm: 35 });
    expect(fetchSpy).toHaveBeenCalledTimes(0);

    vi.advanceTimersByTime(300);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const body = JSON.parse(fetchSpy.mock.calls[0]![1].body as string);
    expect(body.wpm).toBe(35);

    vi.useRealTimers();
  });

  it('setStatusFromServer replaces the in-flight status', () => {
    useCwStore.getState().setStatusFromServer({
      state: 'sending',
      text: 'CQ CQ CQ',
      wpm: 22,
      queueDepth: 2,
      receivedAtMs: 123456,
    });

    const { status } = useCwStore.getState();
    expect(status.state).toBe('sending');
    expect(status.text).toBe('CQ CQ CQ');
    expect(status.wpm).toBe(22);
    expect(status.queueDepth).toBe(2);
    expect(status.receivedAtMs).toBe(123456);
  });
});
