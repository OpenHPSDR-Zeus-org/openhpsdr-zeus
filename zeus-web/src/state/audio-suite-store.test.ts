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

  it('opens TX and RX suites as independent windows', async () => {
    const { useAudioSuiteStore } = await import('./audio-suite-store');

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
    expect(stored.state.txOpen).toBe(false);
    expect(stored.state.rxOpen).toBe(true);
    expect(stored.state.suiteRoute).toBe('rx');
    expect(stored.state.txX).toBe(111);
    expect(stored.state.rxX).toBe(211);

    vi.resetModules();
    const reloaded = await import('./audio-suite-store');
    expect(reloaded.useAudioSuiteStore.getState().suiteRoute).toBe('rx');
    expect(reloaded.useAudioSuiteStore.getState().txOpen).toBe(false);
    expect(reloaded.useAudioSuiteStore.getState().rxOpen).toBe(true);
    expect(reloaded.useAudioSuiteStore.getState().txX).toBe(111);
    expect(reloaded.useAudioSuiteStore.getState().rxX).toBe(211);
  });

  it('migrates a legacy open RX suite into the RX window', async () => {
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

    const { useAudioSuiteStore } = await import('./audio-suite-store');

    expect(useAudioSuiteStore.getState().rxOpen).toBe(true);
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

    const { useAudioSuiteStore } = await import('./audio-suite-store');
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('Deleted profile');

    await useAudioSuiteStore.getState().loadProfiles();

    expect(useAudioSuiteStore.getState().profilesLoaded).toBe(true);
    expect(useAudioSuiteStore.getState().selectedProfile).toBe('');
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
