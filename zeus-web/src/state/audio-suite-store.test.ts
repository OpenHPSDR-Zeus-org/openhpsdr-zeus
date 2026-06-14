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

describe('audio-suite-store profile selection', () => {
  beforeEach(() => {
    vi.resetModules();
    localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it('persists the selected profile until the operator changes it', async () => {
    const { useAudioSuiteStore } = await import('./audio-suite-store');

    useAudioSuiteStore.getState().setSelectedProfile('Ragchew');

    const stored = JSON.parse(localStorage.getItem('zeus-audio-suite') ?? '{}');
    expect(stored.state.selectedProfile).toBe('Ragchew');

    vi.resetModules();
    const reloaded = await import('./audio-suite-store');
    expect(reloaded.useAudioSuiteStore.getState().selectedProfile).toBe('Ragchew');
  });

  it('clears a stale selected profile only after profiles load', async () => {
    localStorage.setItem(
      'zeus-audio-suite',
      JSON.stringify({
        state: { selectedProfile: 'Deleted profile' },
        version: 0,
      }),
    );
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(response({ profiles: [{ name: 'Current profile' }] }));
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('Deleted profile');

    await useAudioSuiteStore.getState().loadProfiles();

    expect(useAudioSuiteStore.getState().profilesLoaded).toBe(true);
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('');
  });
});
