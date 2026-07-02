// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../components/meters/__tests__/harness';
import { useAudioSuiteStore } from './audio-suite-store';
import { useTxStore } from './tx-store';

function response(body: unknown, ok = true): Response {
  return {
    ok,
    status: ok ? 200 : 500,
    json: async () => body,
  } as Response;
}

function resetStoreState(): void {
  useTxStore.setState(useTxStore.getInitialState(), true);
  useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
}

async function rehydrateAudioSuiteFromStorage(): Promise<void> {
  const persisted = localStorage.getItem('zeus-audio-suite');
  resetStoreState();
  if (persisted === null) {
    localStorage.removeItem('zeus-audio-suite');
  } else {
    localStorage.setItem('zeus-audio-suite', persisted);
  }
  await useAudioSuiteStore.persist.rehydrate();
}

describe('audio-suite-store profile selection', () => {
  beforeEach(() => {
    vi.useRealTimers();
    resetStoreState();
    localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    vi.useRealTimers();
    resetStoreState();
    localStorage.clear();
  });

  it('persists the selected profile until the operator changes it', async () => {
    useAudioSuiteStore.getState().setSelectedProfile('Ragchew');

    const stored = JSON.parse(localStorage.getItem('zeus-audio-suite') ?? '{}');
    expect(stored.state.selectedProfile).toBe('Ragchew');

    await rehydrateAudioSuiteFromStorage();
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('Ragchew');
  });

  it('persists the selected RX profile until the operator changes it', async () => {
    useAudioSuiteStore.getState().setSelectedProfileForRoute('rx', 'Clear RX');

    const stored = JSON.parse(localStorage.getItem('zeus-audio-suite') ?? '{}');
    expect(stored.state.rxSelectedProfile).toBe('Clear RX');

    await rehydrateAudioSuiteFromStorage();
    expect(useAudioSuiteStore.getState().rxSelectedProfile).toBe('Clear RX');
  });

  it('opens TX and RX suites as independent windows', async () => {
    expect(useAudioSuiteStore.getState().suiteRoute).toBe('tx');

    useAudioSuiteStore.getState().openTx();
    useAudioSuiteStore.getState().openRx();

    expect(useAudioSuiteStore.getState().isOpen).toBe(true);
    expect(useAudioSuiteStore.getState().txOpen).toBe(true);
    expect(useAudioSuiteStore.getState().rxOpen).toBe(true);
    expect(useAudioSuiteStore.getState().suiteRoute).toBe('rx');

    useAudioSuiteStore.getState().closeRx();

    expect(useAudioSuiteStore.getState().isOpen).toBe(true);
    expect(useAudioSuiteStore.getState().txOpen).toBe(true);
    expect(useAudioSuiteStore.getState().rxOpen).toBe(false);
    expect(useAudioSuiteStore.getState().suiteRoute).toBe('tx');

    useAudioSuiteStore.getState().openRx();
    useAudioSuiteStore.getState().closeTx();

    expect(useAudioSuiteStore.getState().isOpen).toBe(true);
    expect(useAudioSuiteStore.getState().txOpen).toBe(false);
    expect(useAudioSuiteStore.getState().rxOpen).toBe(true);
    expect(useAudioSuiteStore.getState().suiteRoute).toBe('rx');

    useAudioSuiteStore.getState().setWindowPosition('tx', 111, 122);
    useAudioSuiteStore.getState().setWindowPosition('rx', 211, 222);

    const stored = JSON.parse(localStorage.getItem('zeus-audio-suite') ?? '{}');
    expect(stored.state.suiteRoute).toBe('rx');
    expect(stored.state.txX).toBe(111);
    expect(stored.state.rxX).toBe(211);

    // Window placement persists, but neither suite reopens on rehydrate —
    // the suites only open on an explicit operator click after startup.
    await rehydrateAudioSuiteFromStorage();
    expect(useAudioSuiteStore.getState().suiteRoute).toBe('rx');
    expect(useAudioSuiteStore.getState().isOpen).toBe(false);
    expect(useAudioSuiteStore.getState().txOpen).toBe(false);
    expect(useAudioSuiteStore.getState().rxOpen).toBe(false);
    expect(useAudioSuiteStore.getState().txX).toBe(111);
    expect(useAudioSuiteStore.getState().rxX).toBe(211);
  });

  it('migrates legacy window placement but starts both suites closed', async () => {
    localStorage.setItem(
      'zeus-audio-suite',
      JSON.stringify({
        state: {
          isOpen: true,
          suiteRoute: 'rx',
          x: 321,
          y: 123,
          width: 777,
          height: 555,
        },
        version: 0,
      }),
    );

    await rehydrateAudioSuiteFromStorage();

    // A suite left open in legacy storage must NOT auto-open on startup; only
    // its persisted placement carries over.
    expect(useAudioSuiteStore.getState().isOpen).toBe(false);
    expect(useAudioSuiteStore.getState().rxOpen).toBe(false);
    expect(useAudioSuiteStore.getState().txOpen).toBe(false);
    expect(useAudioSuiteStore.getState().rxX).toBe(321);
    expect(useAudioSuiteStore.getState().rxY).toBe(123);
    expect(useAudioSuiteStore.getState().rxWidth).toBe(777);
    expect(useAudioSuiteStore.getState().rxHeight).toBe(555);
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

    await rehydrateAudioSuiteFromStorage();
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('Deleted profile');

    await useAudioSuiteStore.getState().loadProfiles();

    expect(useAudioSuiteStore.getState().profilesLoaded).toBe(true);
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('');
  });

  it('hydrates the selected RX profile from the server when profiles load', async () => {
    localStorage.setItem(
      'zeus-audio-suite',
      JSON.stringify({
        state: { rxSelectedProfile: 'Old RX' },
        version: 0,
      }),
    );
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(response({
        selectedProfile: 'Clear RX',
        profiles: [{ name: 'Clear RX' }],
      }));
    vi.stubGlobal('fetch', fetchMock);

    await rehydrateAudioSuiteFromStorage();
    await useAudioSuiteStore.getState().loadProfiles('rx');

    expect(fetchMock).toHaveBeenCalledWith('/api/rx-audio-suite/profiles');
    expect(useAudioSuiteStore.getState().rxProfilesLoaded).toBe(true);
    expect(useAudioSuiteStore.getState().rxSelectedProfile).toBe('Clear RX');
  });

  it('keeps a local RX profile selection when the server has no selected row yet', async () => {
    localStorage.setItem(
      'zeus-audio-suite',
      JSON.stringify({
        state: { rxSelectedProfile: 'Clear RX' },
        version: 0,
      }),
    );
    const fetchMock = vi
      .fn<typeof fetch>()
      .mockResolvedValue(response({
        selectedProfile: null,
        profiles: [{ name: 'Clear RX' }],
      }));
    vi.stubGlobal('fetch', fetchMock);

    await rehydrateAudioSuiteFromStorage();
    await useAudioSuiteStore.getState().loadProfiles('rx');

    expect(useAudioSuiteStore.getState().rxSelectedProfile).toBe('Clear RX');
  });

  it('marks a profile selected after a successful apply', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/tx-audio-suite/profiles/Native%20x1/apply') {
        return response({ pluginIds: ['noise-gate', 'compressor'] });
      }
      if (url === '/api/tx-audio-suite/master-bypass') {
        return response({ bypassed: false });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    const result = await useAudioSuiteStore.getState().applyProfile('Native x1');

    expect(result).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/tx-audio-suite/profiles/Native%20x1/apply',
      { method: 'POST' },
    );
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('Native x1');
    expect(useAudioSuiteStore.getState().chainOrder).toEqual(['noise-gate', 'compressor']);
    expect(useAudioSuiteStore.getState().masterBypassed).toBe(false);
    expect(useAudioSuiteStore.getState().pluginSettingsRevision).toBe(1);
  });

  it('switches to the processing mode returned by a profile apply', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/tx-audio-suite/profiles/VST%20rack/apply') {
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
    expect(useAudioSuiteStore.getState().pluginSettingsRevision).toBe(1);
  });

  it('switches back to native mode returned by a profile apply', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/tx-audio-suite/profiles/Native%20rack/apply') {
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
    expect(useAudioSuiteStore.getState().pluginSettingsRevision).toBe(1);
  });

  it('applies RX profiles through the RX suite endpoint only', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/rx-audio-suite/profiles/Clear%20RX/apply') {
        return response({
          pluginIds: ['com.openhpsdr.zeus.rxvst.supertone-clear'],
          processingMode: 'vst',
          engineAvailable: true,
          engineActive: true,
          activePlugins: 1,
          degradedBlocks: 3,
          masterBypass: false,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    useAudioSuiteStore.setState({
      chainOrder: ['com.openhpsdr.zeus.samples.eq'],
      rxChainOrder: ['com.openhpsdr.zeus.rxvst.old'],
      masterBypassed: true,
      rxMasterBypassed: true,
    });

    const result = await useAudioSuiteStore.getState().applyProfile('Clear RX', 'rx');

    expect(result).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/rx-audio-suite/profiles/Clear%20RX/apply',
      { method: 'POST' },
    );
    expect(useAudioSuiteStore.getState().chainOrder).toEqual([
      'com.openhpsdr.zeus.samples.eq',
    ]);
    expect(useAudioSuiteStore.getState().masterBypassed).toBe(true);
    expect(useAudioSuiteStore.getState().rxChainOrder).toEqual([
      'com.openhpsdr.zeus.rxvst.supertone-clear',
    ]);
    expect(useAudioSuiteStore.getState().rxMasterBypassed).toBe(false);
    expect(useAudioSuiteStore.getState().rxSelectedProfile).toBe('Clear RX');
    expect(useAudioSuiteStore.getState().rxVstEngineActive).toBe(true);
    expect(useAudioSuiteStore.getState().rxVstActivePlugins).toBe(1);
    expect(useAudioSuiteStore.getState().rxVstDegradedBlocks).toBe(3);
    expect(useAudioSuiteStore.getState().pluginSettingsRevision).toBe(1);
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
    expect(useAudioSuiteStore.getState().pluginSettingsRevision).toBe(0);
  });

  it('mirrors preview onto the TX monitor store', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/tx-audio-suite/preview') {
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
    expect(fetchMock).toHaveBeenCalledWith('/api/tx-audio-suite/preview', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled: true, meterOnly: false }),
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

  it('passes the requested VST scan route and refreshes TX/RX chain order', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/audio-suite/scan-vst-directory') {
        return response({
          directory: 'C:\\VST PLUGINS',
          registered: [
            { id: 'com.openhpsdr.zeus.vst.supertoneclear', name: 'Supertone Clear TX' },
            { id: 'com.openhpsdr.zeus.rxvst.supertoneclear', name: 'Supertone Clear RX' },
          ],
          skipped: [],
          errors: [],
        });
      }
      if (url === '/api/plugins') {
        return response({ plugins: [] });
      }
      if (url === '/api/tx-audio-suite/chain/order') {
        return response({ pluginIds: ['com.openhpsdr.zeus.vst.supertoneclear'] });
      }
      if (url === '/api/rx-audio-suite/chain/order') {
        return response({ pluginIds: ['com.openhpsdr.zeus.rxvst.supertoneclear'] });
      }
      if (url === '/api/rx-audio-suite/processing-mode') {
        return response({
          engineAvailable: true,
          engineActive: true,
          activePlugins: 1,
          degradedBlocks: 0,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    const result = await useAudioSuiteStore
      .getState()
      .scanVstDirectory('C:\\VST PLUGINS', 'both');

    expect(result.ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledWith('/api/audio-suite/scan-vst-directory', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ directory: 'C:\\VST PLUGINS', route: 'both' }),
    });
    expect(useAudioSuiteStore.getState().chainOrder).toEqual([
      'com.openhpsdr.zeus.vst.supertoneclear',
    ]);
    expect(useAudioSuiteStore.getState().rxChainOrder).toEqual([
      'com.openhpsdr.zeus.rxvst.supertoneclear',
    ]);
    expect(useAudioSuiteStore.getState().rxVstEngineActive).toBe(true);
    expect(useAudioSuiteStore.getState().rxVstActivePlugins).toBe(1);
  });

  it('uses route-specific VST scan endpoints for TX and RX scans', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (
        url === '/api/tx-audio-suite/scan-vst-directory' ||
        url === '/api/rx-audio-suite/scan-vst-directory'
      ) {
        return response({
          directory: url.includes('/rx-') ? 'C:\\RX VST' : 'C:\\TX VST',
          registered: [],
          skipped: [],
          errors: [],
        });
      }
      if (url === '/api/plugins') {
        return response({ plugins: [] });
      }
      if (url === '/api/tx-audio-suite/chain/order') {
        return response({ pluginIds: [] });
      }
      if (url === '/api/rx-audio-suite/chain/order') {
        return response({ pluginIds: [] });
      }
      if (url === '/api/rx-audio-suite/processing-mode') {
        return response({
          engineAvailable: true,
          engineActive: false,
          activePlugins: 0,
          degradedBlocks: 0,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');

    await useAudioSuiteStore.getState().scanVstDirectory('C:\\TX VST', 'tx');
    await useAudioSuiteStore.getState().scanVstDirectory('C:\\RX VST', 'rx');

    expect(fetchMock).toHaveBeenCalledWith('/api/tx-audio-suite/scan-vst-directory', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ directory: 'C:\\TX VST', route: 'tx' }),
    });
    expect(fetchMock).toHaveBeenCalledWith('/api/rx-audio-suite/scan-vst-directory', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ directory: 'C:\\RX VST', route: 'rx' }),
    });
  });

  it('loads RX VST engine diagnostics separately from TX mode', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/rx-audio-suite/processing-mode') {
        return response({
          engineAvailable: true,
          engineActive: false,
          activePlugins: 0,
          degradedBlocks: 3,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    await useAudioSuiteStore.getState().loadRxProcessingModeFromServer();

    expect(fetchMock).toHaveBeenCalledWith('/api/rx-audio-suite/processing-mode');
    expect(useAudioSuiteStore.getState().rxVstEngineAvailable).toBe(true);
    expect(useAudioSuiteStore.getState().rxVstEngineActive).toBe(false);
    expect(useAudioSuiteStore.getState().rxVstActivePlugins).toBe(0);
    expect(useAudioSuiteStore.getState().rxVstDegradedBlocks).toBe(3);
  });

  it('loads and toggles RX master bypass through RX endpoints', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/rx-audio-suite/master-bypass' && !init) {
        return response({ bypassed: true });
      }
      if (url === '/api/rx-audio-suite/master-bypass') {
        return response({ bypassed: false });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    await useAudioSuiteStore.getState().loadRxMasterBypassFromServer();

    expect(useAudioSuiteStore.getState().rxMasterBypassed).toBe(true);

    await useAudioSuiteStore.getState().setRxMasterBypassed(false);

    expect(useAudioSuiteStore.getState().rxMasterBypassed).toBe(false);
    expect(fetchMock).toHaveBeenCalledWith('/api/rx-audio-suite/master-bypass', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bypassed: false }),
    });
  });

  it('applies RX master bypass broadcasts without touching TX bypass', async () => {
    const { useAudioSuiteStore } = await import('./audio-suite-store');
    useAudioSuiteStore.setState({
      masterBypassed: false,
      rxMasterBypassed: false,
    });

    useAudioSuiteStore.getState().setRxMasterBypassedFromServer(true);

    expect(useAudioSuiteStore.getState().masterBypassed).toBe(false);
    expect(useAudioSuiteStore.getState().rxMasterBypassed).toBe(true);
  });

  it('refreshes RX VST diagnostics after RX chain membership changes', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/rx-audio-suite/plugins/com.openhpsdr.zeus.rxvst.clear/chain-membership') {
        return response({ pluginIds: ['com.openhpsdr.zeus.rxvst.clear'] });
      }
      if (url === '/api/rx-audio-suite/processing-mode') {
        return response({
          engineAvailable: true,
          engineActive: true,
          activePlugins: 1,
          degradedBlocks: 2,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    await useAudioSuiteStore
      .getState()
      .setRxChainMembership('com.openhpsdr.zeus.rxvst.clear', true);

    expect(useAudioSuiteStore.getState().rxChainOrder).toEqual([
      'com.openhpsdr.zeus.rxvst.clear',
    ]);
    expect(useAudioSuiteStore.getState().rxVstEngineAvailable).toBe(true);
    expect(useAudioSuiteStore.getState().rxVstEngineActive).toBe(true);
    expect(useAudioSuiteStore.getState().rxVstActivePlugins).toBe(1);
    expect(useAudioSuiteStore.getState().rxVstDegradedBlocks).toBe(2);
    expect(fetchMock).toHaveBeenCalledWith('/api/rx-audio-suite/processing-mode');
  });

  it('uses RX endpoints for RX chain membership and ordering', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/rx-audio-suite/plugins/com.openhpsdr.zeus.rxvst.clear/chain-membership') {
        return response({
          pluginIds: [
            'com.openhpsdr.zeus.rxvst.clear',
            'com.openhpsdr.zeus.rxvst.rnnoise',
          ],
        });
      }
      if (url === '/api/rx-audio-suite/chain/order') {
        return response({
          pluginIds: [
            'com.openhpsdr.zeus.rxvst.rnnoise',
            'com.openhpsdr.zeus.rxvst.clear',
          ],
        });
      }
      if (url === '/api/rx-audio-suite/processing-mode') {
        return response({
          engineAvailable: true,
          engineActive: true,
          activePlugins: 2,
          degradedBlocks: 0,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    useAudioSuiteStore.setState({
      rxChainOrder: [
        'com.openhpsdr.zeus.rxvst.clear',
        'com.openhpsdr.zeus.rxvst.rnnoise',
      ],
    });

    await useAudioSuiteStore
      .getState()
      .setRxChainMembership('com.openhpsdr.zeus.rxvst.clear', true);
    await useAudioSuiteStore.getState().reorderRxChain(0, 1);

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/rx-audio-suite/plugins/com.openhpsdr.zeus.rxvst.clear/chain-membership',
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ active: true }),
      },
    );
    expect(fetchMock).toHaveBeenCalledWith('/api/rx-audio-suite/chain/order', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        pluginIds: [
          'com.openhpsdr.zeus.rxvst.rnnoise',
          'com.openhpsdr.zeus.rxvst.clear',
        ],
      }),
    });
  });
});

describe('audio-suite-store VST engine install', () => {
  beforeEach(() => {
    vi.useRealTimers();
    resetStoreState();
    localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    vi.useRealTimers();
    resetStoreState();
    localStorage.clear();
  });

  it('downloads the engine and flips availability when staging completes', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/tx-audio-suite/vst-engine/install' && init?.method === 'POST') {
        return response({ phase: 'downloading', percent: 0 });
      }
      if (url === '/api/tx-audio-suite/vst-engine/install') {
        return response({ phase: 'done', percent: 100, message: 'installed' });
      }
      if (url === '/api/tx-audio-suite/processing-mode') {
        return response({ mode: 'vst', engineAvailable: true, engineActive: true });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const promise = useAudioSuiteStore.getState().installVstEngine();
    await vi.advanceTimersByTimeAsync(1100); // step past the 1s poll delay
    await promise;

    expect(useAudioSuiteStore.getState().vstEngineInstall.phase).toBe('done');
    // The follow-up processing-mode read makes the engine usable.
    expect(useAudioSuiteStore.getState().vstEngineAvailable).toBe(true);
    expect(useAudioSuiteStore.getState().vstEngineActive).toBe(true);
  });

  it('RX install refreshes RX diagnostics without switching TX to VST (#1276)', async () => {
    vi.useFakeTimers();
    let processingModePutCalled = false;
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/tx-audio-suite/vst-engine/install' && init?.method === 'POST') {
        return response({ phase: 'downloading', percent: 0 });
      }
      if (url === '/api/tx-audio-suite/vst-engine/install') {
        return response({ phase: 'done', percent: 100, message: 'installed' });
      }
      if (url === '/api/tx-audio-suite/processing-mode' && init?.method === 'PUT') {
        processingModePutCalled = true;
        return response({ mode: 'vst', engineAvailable: true, engineActive: true });
      }
      if (url === '/api/rx-audio-suite/processing-mode') {
        return response({ engineAvailable: true, engineActive: true, activePlugins: 1, degradedBlocks: 0 });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const promise = useAudioSuiteStore
      .getState()
      .installVstEngine('/api/tx-audio-suite/vst-engine/install', 'rx');
    await vi.advanceTimersByTimeAsync(1100);
    await promise;

    expect(useAudioSuiteStore.getState().vstEngineInstall.phase).toBe('done');
    expect(useAudioSuiteStore.getState().vstEngineInstall.message).toBe(
      'VST engine ready — RX audio now routes through VST.',
    );
    // RX diagnostics were refreshed with the shared engine now installed.
    expect(useAudioSuiteStore.getState().rxVstEngineAvailable).toBe(true);
    expect(useAudioSuiteStore.getState().rxVstEngineActive).toBe(true);
    // TX processing-mode PUT MUST NOT be called — the RX install path leaves
    // the TX route alone. Regression guard for issue #1276.
    expect(processingModePutCalled).toBe(false);
    expect(useAudioSuiteStore.getState().processingMode).toBe('native');
  });

  it('surfaces a failed install for retry', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/tx-audio-suite/vst-engine/install' && init?.method === 'POST') {
        return response({ phase: 'downloading', percent: 0 });
      }
      if (url === '/api/tx-audio-suite/vst-engine/install') {
        return response({ phase: 'failed', percent: 0, message: 'no engine in archive' });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const promise = useAudioSuiteStore.getState().installVstEngine();
    await vi.advanceTimersByTimeAsync(1100);
    await promise;

    expect(useAudioSuiteStore.getState().vstEngineInstall.phase).toBe('failed');
    expect(useAudioSuiteStore.getState().vstEngineInstall.message).toBe('no engine in archive');
    expect(useAudioSuiteStore.getState().vstEngineAvailable).toBe(false);
  });
});

describe('audio-suite-store platform affordance', () => {
  beforeEach(() => {
    vi.useRealTimers();
    resetStoreState();
    localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    vi.useRealTimers();
    resetStoreState();
    localStorage.clear();
  });

  it('mirrors the platform flags from the engine-install DTO (macOS shape)', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/tx-audio-suite/vst-engine/install') {
        return response({
          phase: 'idle',
          percent: 0,
          engineAvailable: false,
          engineSupported: false,
          inProcessHostSupported: true,
          auSupported: true,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    await useAudioSuiteStore.getState().loadEngineSupportFromServer();

    expect(fetchMock).toHaveBeenCalledWith('/api/tx-audio-suite/vst-engine/install');
    expect(useAudioSuiteStore.getState().engineSupported).toBe(false);
    expect(useAudioSuiteStore.getState().inProcessHostSupported).toBe(true);
    expect(useAudioSuiteStore.getState().auSupported).toBe(true);
    expect(useAudioSuiteStore.getState().engineSupportLoaded).toBe(true);
  });

  it('mirrors the platform flags from the engine-install DTO (Windows shape)', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/tx-audio-suite/vst-engine/install') {
        return response({
          phase: 'idle',
          percent: 0,
          engineSupported: true,
          inProcessHostSupported: true,
          auSupported: false,
        });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    await useAudioSuiteStore.getState().loadEngineSupportFromServer();

    expect(useAudioSuiteStore.getState().engineSupported).toBe(true);
    expect(useAudioSuiteStore.getState().auSupported).toBe(false);
  });

  it('defaults to the engine-supported shape before the DTO loads', () => {
    const state = useAudioSuiteStore.getState();
    expect(state.engineSupported).toBe(true);
    expect(state.engineSupportLoaded).toBe(false);
  });

  it('scans AU components on the TX route and reports support', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/tx-audio-suite/scan-au') {
        return response({
          supported: true,
          registered: [{ id: 'com.openhpsdr.zeus.au.eq', name: 'EQ' }],
          skipped: [],
          errors: [],
        });
      }
      if (url === '/api/plugins') return response({ plugins: [] });
      if (url === '/api/tx-audio-suite/chain/order') return response({ pluginIds: [] });
      if (url === '/api/rx-audio-suite/chain/order') return response({ pluginIds: [] });
      if (url === '/api/rx-audio-suite/processing-mode') {
        return response({ engineAvailable: false, engineActive: false });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await useAudioSuiteStore.getState().scanAuComponents('tx');

    expect(result.ok).toBe(true);
    expect(result.supported).toBe(true);
    expect(result.registered).toHaveLength(1);
    expect(fetchMock).toHaveBeenCalledWith('/api/tx-audio-suite/scan-au', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ route: 'tx' }),
    });
  });

  it('routes AU scans to the RX endpoint for the RX path', async () => {
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/rx-audio-suite/scan-au') {
        return response({ supported: true, registered: [], skipped: [], errors: [] });
      }
      if (url === '/api/plugins') return response({ plugins: [] });
      if (url === '/api/tx-audio-suite/chain/order') return response({ pluginIds: [] });
      if (url === '/api/rx-audio-suite/chain/order') return response({ pluginIds: [] });
      if (url === '/api/rx-audio-suite/processing-mode') {
        return response({ engineAvailable: false, engineActive: false });
      }
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);

    await useAudioSuiteStore.getState().scanAuComponents('rx');

    expect(fetchMock).toHaveBeenCalledWith('/api/rx-audio-suite/scan-au', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ route: 'rx' }),
    });
  });

  it('reports an error result when the AU scan endpoint rejects', async () => {
    const fetchMock = vi.fn<typeof fetch>(async () =>
      response({ error: 'boom' }, false),
    );
    vi.stubGlobal('fetch', fetchMock);

    const result = await useAudioSuiteStore.getState().scanAuComponents('tx');

    expect(result.ok).toBe(false);
    expect(result.error).toBe('boom');
  });
});
