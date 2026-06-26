// SPDX-License-Identifier: GPL-2.0-or-later
//
// Bug 2 regression: the docked Audio Tools rail showed NO affordance button on
// macOS because the per-OS platform flags were never fetched for the docked
// rails. The fix has the TX and RX rails call loadEngineSupportFromServer on
// mount; this test proves the macOS shape (engineSupported=false,
// auSupported=true) makes the in-process "Scan AU" affordance render. Before the
// fix engineSupportLoaded stayed false and neither the Download nor the Scan
// button appeared.
//
// `./meters/__tests__/harness` is imported first for its side-effect: it
// installs a dependable in-memory localStorage polyfill before any Zustand
// store module loads (jsdom's bare localStorage breaks the persist middleware).

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from './meters/__tests__/harness';
import { useAudioSuiteStore } from '../state/audio-suite-store';
import { TxAudioToolsPanel } from './TxAudioToolsPanel';

// Neutralise the panel's non-rail children so the test exercises only the
// docked rails' mount behaviour (no plugin panels, no CFC fetches). The audio
// devices rail self-disables when host capabilities are unseeded.
vi.mock('../plugins/runtime/usePluginPanels', () => ({
  usePluginPanels: () => [],
}));
vi.mock('./CfcSettingsPanel', () => ({
  CfcSettingsPanel: () => null,
}));

function response(body: unknown, ok = true): Response {
  return {
    ok,
    status: ok ? 200 : 500,
    json: async () => body,
  } as Response;
}

// macOS shape: no out-of-process engine, AU hosting available in-process.
const MACOS_INSTALL_DTO = {
  phase: 'idle',
  percent: 0,
  engineAvailable: false,
  engineSupported: false,
  inProcessHostSupported: true,
  auSupported: true,
};

async function flush(cond: () => boolean): Promise<void> {
  for (let i = 0; i < 50; i++) {
    if (cond()) return;
    // eslint-disable-next-line no-await-in-loop
    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
    });
  }
}

describe('TxAudioToolsPanel docked rails (Bug 2 platform affordance)', () => {
  beforeEach(() => {
    useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
    const fetchMock = vi.fn<typeof fetch>(async (input: RequestInfo | URL) => {
      if (String(input) === '/api/tx-audio-suite/vst-engine/install') {
        return response(MACOS_INSTALL_DTO);
      }
      // Permissive default for the rails' other on-mount loaders
      // (chain order / master-bypass / processing-mode).
      return response({});
    });
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    useAudioSuiteStore.setState(useAudioSuiteStore.getInitialState(), true);
  });

  it('fetches the platform affordance DTO on mount and renders the in-process Scan button', async () => {
    const { container, unmount } = render(createElement(TxAudioToolsPanel));

    await flush(() => useAudioSuiteStore.getState().engineSupportLoaded);

    // The rails pulled the per-OS flags from the server.
    expect(fetch).toHaveBeenCalledWith('/api/tx-audio-suite/vst-engine/install');
    const state = useAudioSuiteStore.getState();
    expect(state.engineSupportLoaded).toBe(true);
    expect(state.engineSupported).toBe(false);
    expect(state.auSupported).toBe(true);

    // macOS affordance is rendered (auSupported => "Scan AU"); the Windows-only
    // "Download VST Engine" button is not.
    const buttons = Array.from(container.querySelectorAll('button'));
    const scan = buttons.find((b) => b.textContent === 'Scan AU');
    expect(scan).toBeTruthy();
    expect(buttons.some((b) => b.textContent?.includes('Download VST Engine'))).toBe(false);

    unmount();
  });
});
