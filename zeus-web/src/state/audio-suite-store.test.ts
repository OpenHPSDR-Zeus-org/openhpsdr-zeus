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

  it('marks a profile selected after a successful apply', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/audio-suite/profiles/Native%20x1/apply') {
        return response({ pluginIds: ['noise-gate', 'compressor'] });
      }
      if (url === '/api/audio-suite/master-bypass') {
        return response({ bypassed: false });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    const result = await useAudioSuiteStore.getState().applyProfile('Native x1');

    expect(result).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/audio-suite/profiles/Native%20x1/apply',
      { method: 'POST' },
    );
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('Native x1');
    expect(useAudioSuiteStore.getState().chainOrder).toEqual(['noise-gate', 'compressor']);
    expect(useAudioSuiteStore.getState().masterBypassed).toBe(false);
  });

  it('switches to the processing mode returned by a profile apply', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/audio-suite/profiles/VST%20rack/apply') {
        return response({
          pluginIds: ['com.openhpsdr.zeus.vst.comp'],
          processingMode: 'vst',
          engineAvailable: true,
          engineActive: true,
          masterBypass: true,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    const result = await useAudioSuiteStore.getState().applyProfile('VST rack');

    expect(result).toEqual({ ok: true });
    expect(useAudioSuiteStore.getState().processingMode).toBe('vst');
    expect(useAudioSuiteStore.getState().vstEngineAvailable).toBe(true);
    expect(useAudioSuiteStore.getState().vstEngineActive).toBe(true);
    expect(useAudioSuiteStore.getState().masterBypassed).toBe(true);
    expect(useAudioSuiteStore.getState().chainOrder).toEqual([
      'com.openhpsdr.zeus.vst.comp',
    ]);
  });

  it('switches back to native mode returned by a profile apply', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/audio-suite/profiles/Native%20rack/apply') {
        return response({
          pluginIds: ['com.openhpsdr.zeus.samples.eq'],
          processingMode: 'native',
          engineAvailable: true,
          engineActive: false,
          masterBypass: false,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    useAudioSuiteStore.setState({
      processingMode: 'vst',
      vstEngineAvailable: true,
      vstEngineActive: true,
      masterBypassed: true,
      chainOrder: ['com.openhpsdr.zeus.vst.comp'],
    });

    const result = await useAudioSuiteStore.getState().applyProfile('Native rack');

    expect(result).toEqual({ ok: true });
    expect(useAudioSuiteStore.getState().processingMode).toBe('native');
    expect(useAudioSuiteStore.getState().vstEngineAvailable).toBe(true);
    expect(useAudioSuiteStore.getState().vstEngineActive).toBe(false);
    expect(useAudioSuiteStore.getState().masterBypassed).toBe(false);
    expect(useAudioSuiteStore.getState().chainOrder).toEqual([
      'com.openhpsdr.zeus.samples.eq',
    ]);
  });

  it('reports profile apply failures', async () => {
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue({ ok: false, status: 404, text: async () => 'missing profile' } as Response);
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    const result = await useAudioSuiteStore.getState().applyProfile('Missing');

    expect(result).toEqual({ ok: false, error: 'missing profile' });
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('');
  });

  it('mirrors preview onto the TX monitor store', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/audio-suite/preview') {
        return response({ supported: true, enabled: true });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    const { useTxStore } = await import('./tx-store');

    await useAudioSuiteStore.getState().setPreviewEnabled(true);

    expect(useAudioSuiteStore.getState().previewEnabled).toBe(true);
    expect(useTxStore.getState().txMonitorEnabled).toBe(true);
    expect(fetchMock).toHaveBeenCalledWith('/api/audio-suite/preview', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled: true }),
    });
  });

  it('mirrors TX monitor changes back onto preview state', async () => {
    const { useTxStore } = await import('./tx-store');
    useTxStore.getState().setTxMonitorEnabled(true);

    const { useAudioSuiteStore } = await import('./audio-suite-store');

    expect(useAudioSuiteStore.getState().previewEnabled).toBe(true);

    useTxStore.getState().setTxMonitorEnabled(false);

    expect(useAudioSuiteStore.getState().previewEnabled).toBe(false);
  });
});
