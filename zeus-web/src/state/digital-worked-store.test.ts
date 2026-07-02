// SPDX-License-Identifier: GPL-2.0-or-later
//
// digital-worked-store tests — the render-time worked-before set fed by the
// CORE endpoint GET /api/log/digital-worked (pinned shape: { calls: string[] }).
// A failed or malformed fetch must keep the last good set (highlight goes
// stale, never breaks).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useDigitalWorkedStore } from './digital-worked-store';

function fetchOk(body: unknown) {
  return vi.fn(async () => ({ ok: true, json: async () => body })) as never;
}

describe('digital-worked-store', () => {
  beforeEach(() => {
    useDigitalWorkedStore.setState({ calls: new Set<string>(), loaded: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('hydrates the upper-cased worked set from { calls: [...] }', async () => {
    vi.stubGlobal('fetch', fetchOk({ calls: ['K1ABC', 'g0xyz', ' w9xyz '] }));
    await useDigitalWorkedStore.getState().refresh();
    const s = useDigitalWorkedStore.getState();
    expect(s.loaded).toBe(true);
    expect(s.calls.has('K1ABC')).toBe(true);
    expect(s.calls.has('G0XYZ')).toBe(true); // upper-cased
    expect(s.calls.has('W9XYZ')).toBe(true); // trimmed
    expect(s.calls.size).toBe(3);
  });

  it('keeps the last good set on a non-2xx response', async () => {
    vi.stubGlobal('fetch', fetchOk({ calls: ['K1ABC'] }));
    await useDigitalWorkedStore.getState().refresh();
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, status: 500 })) as never);
    await useDigitalWorkedStore.getState().refresh();
    expect(useDigitalWorkedStore.getState().calls.has('K1ABC')).toBe(true);
  });

  it('keeps the last good set on a network error', async () => {
    vi.stubGlobal('fetch', fetchOk({ calls: ['K1ABC'] }));
    await useDigitalWorkedStore.getState().refresh();
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new Error('offline'))) as never);
    await useDigitalWorkedStore.getState().refresh();
    expect(useDigitalWorkedStore.getState().calls.has('K1ABC')).toBe(true);
  });

  it('ignores a malformed payload (calls not an array)', async () => {
    vi.stubGlobal('fetch', fetchOk({ calls: 'nope' }));
    await useDigitalWorkedStore.getState().refresh();
    expect(useDigitalWorkedStore.getState().loaded).toBe(false);
    expect(useDigitalWorkedStore.getState().calls.size).toBe(0);
  });

  it('drops non-string / empty entries defensively', async () => {
    vi.stubGlobal('fetch', fetchOk({ calls: ['K1ABC', 42, '', '  '] }));
    await useDigitalWorkedStore.getState().refresh();
    expect(useDigitalWorkedStore.getState().calls.size).toBe(1);
  });
});
