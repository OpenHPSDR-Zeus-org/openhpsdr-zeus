// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../components/meters/__tests__/harness';

function response(body: unknown, ok = true): Response {
  return {
    ok,
    status: ok ? 200 : 500,
    json: async () => body,
  } as Response;
}

describe('notch-store backend hydration', () => {
  beforeEach(() => {
    vi.resetModules();
    localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it('uses server notches on startup instead of posting stale localStorage', async () => {
    localStorage.setItem('zeus.notches', JSON.stringify([{ centerHz: 7_100_000, widthHz: 500 }]));
    const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(response([
      { centerHz: 14_200_500, widthHz: 75, active: true },
    ]));
    vi.stubGlobal('fetch', fetchMock);

    const { hydrateNotchesFromBackend, useNotchStore } = await import('./notch-store');

    await hydrateNotchesFromBackend();

    expect(useNotchStore.getState().notches).toEqual([
      { id: expect.any(String), centerHz: 14_200_500, widthHz: 75 },
    ]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/rx/notches');
    expect(JSON.parse(localStorage.getItem('zeus.notches') ?? '[]')).toEqual([
      { centerHz: 14_200_500, widthHz: 75 },
    ]);
  });

  it('migrates legacy localStorage notches when the server has none yet', async () => {
    localStorage.setItem('zeus.notches', JSON.stringify([{ centerHz: 7_255_300, widthHz: 125 }]));
    const fetchMock = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(response([]))
      .mockResolvedValueOnce(response([]));
    vi.stubGlobal('fetch', fetchMock);

    const { hydrateNotchesFromBackend } = await import('./notch-store');

    await hydrateNotchesFromBackend();

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/rx/notches');
    const [postUrl, postInit] = fetchMock.mock.calls[1] ?? [];
    expect(postUrl).toBe('/api/rx/notches');
    expect(postInit?.method).toBe('POST');
    expect(JSON.parse((postInit?.body ?? '{}') as string)).toEqual({
      notches: [{ centerHz: 7_255_300, widthHz: 125, active: true }],
    });
  });
});
