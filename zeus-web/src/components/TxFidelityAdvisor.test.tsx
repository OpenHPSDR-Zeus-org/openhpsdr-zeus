// SPDX-License-Identifier: GPL-2.0-or-later
//
// TX Fidelity must mount without an external-store render loop. React 19 +
// Zustand 5 treat selectors that return a new object every render as unstable
// snapshots; this test exercises the component path that used to throw the
// minified React #185 maximum-depth error in production builds.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';

import { render, act } from './meters/__tests__/harness';
import { useAudioSuiteStore } from '../state/audio-suite-store';
import { useTxStore } from '../state/tx-store';
import { TxFidelityAdvisor } from './TxFidelityAdvisor';

function requestPath(input: unknown): string {
  if (typeof input === 'string') return new URL(input, 'http://localhost').pathname;
  if (input instanceof URL) return input.pathname;
  if (input && typeof input === 'object' && 'url' in input) {
    return new URL(String((input as { url: string }).url), 'http://localhost').pathname;
  }
  return String(input);
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
  });
}

function stubAudioSuiteFetch({
  bypassed = true,
  mode = 'native',
  engineActive = false,
  chainInputDb = -120,
  chainOutputDb = -120,
}: {
  bypassed?: boolean;
  mode?: 'native' | 'vst';
  engineActive?: boolean;
  chainInputDb?: number;
  chainOutputDb?: number;
} = {}) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown) => {
      const path = requestPath(input);
      if (path === '/api/tx-audio-suite/master-bypass') {
        return jsonResponse({ bypassed });
      }
      if (path === '/api/tx-audio-suite/processing-mode') {
        return jsonResponse({ mode, engineAvailable: true, engineActive });
      }
      if (path === '/api/tx-audio-suite/chain/meters') {
        return jsonResponse({
          inputPeak: Math.pow(10, chainInputDb / 20),
          outputPeak: Math.pow(10, chainOutputDb / 20),
          inputDb: chainInputDb,
          outputDb: chainOutputDb,
        });
      }
      if (path === '/api/tx/diag') {
        return jsonResponse({
          micUplink: { status: 'live' },
          ingest: { droppedFrames: 0 },
          protocol2: {
            queuedPackets: 0,
            sendFailures: 0,
            queueWriteFailures: 0,
          },
          vstEngine: { active: engineActive, degradedBlocks: 0 },
        });
      }
      return jsonResponse({});
    }),
  );
}

function metric(container: HTMLElement, id: string): HTMLElement {
  const el = container.querySelector<HTMLElement>(`[data-testid="tx-fidelity-metric-${id}"]`);
  expect(el).not.toBeNull();
  return el!;
}

describe('TxFidelityAdvisor', () => {
  beforeEach(() => {
    stubAudioSuiteFetch();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    useTxStore.setState({
      moxOn: false,
      tunOn: false,
      txMonitorEnabled: false,
      micDbfs: -100,
      wdspMicPk: -Infinity,
      micAv: -Infinity,
      lvlrGr: 0,
      cfcGr: 0,
      compPk: -Infinity,
      compAv: -Infinity,
      alcGr: 0,
      outPk: -Infinity,
      outAv: -Infinity,
      swr: 1.0,
      psEnabled: false,
      psCorrecting: false,
      psFeedbackLevel: 0,
      psCalState: 0,
      psCalibrationStalled: false,
    });
    useAudioSuiteStore.setState({
      masterBypassed: true,
      processingMode: 'native',
      vstEngineAvailable: false,
      vstEngineActive: false,
    });
  });

  it('renders and reacts to TX meter updates without re-entering render', async () => {
    useTxStore.setState({
      moxOn: true,
      wdspMicPk: -10,
      micAv: -21,
      alcGr: 3,
      lvlrGr: 4,
      cfcGr: 2,
      compPk: -10,
      compAv: -20,
      outPk: -3,
      outAv: -14,
      swr: 1.15,
      psEnabled: true,
      psCorrecting: true,
      psFeedbackLevel: 150,
      psCalState: 0,
      psCalibrationStalled: false,
    });

    const { container, unmount } = render(createElement(TxFidelityAdvisor));

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(container.textContent).toContain('Broadcast sweet spot');
    expect(container.textContent).toContain('FIDELITY');
    expect(container.textContent).toContain('NEXT Hold levels; PureSignal is correcting the PA');
    expect(metric(container, 'out').textContent).toContain('-3.0 dBFS');
    expect(metric(container, 'out').dataset.status).toBe('met');
    expect(metric(container, 'dens').textContent).toMatch(/DENS\d+\/55/);
    expect(metric(container, 'crest').textContent).toContain('11.0 dB');
    expect(metric(container, 'swr').textContent).toContain('1.15');
    expect(metric(container, 'psfb').textContent).toContain('150');

    act(() => {
      useTxStore.setState({ wdspMicPk: -1, alcGr: 12 });
    });

    expect(container.textContent).toContain('Too hot');
    expect(container.textContent).toContain('NEXT Lower mic gain until peaks stay below -6 dBFS');
    expect(metric(container, 'mic').dataset.status).toBe('warn');
    unmount();
  });

  it('surfaces VST chain output headroom from live Audio Suite meters', async () => {
    stubAudioSuiteFetch({
      bypassed: false,
      mode: 'vst',
      engineActive: true,
      chainInputDb: -14.4,
      chainOutputDb: -0.2,
    });
    useAudioSuiteStore.setState({
      masterBypassed: false,
      processingMode: 'vst',
      vstEngineActive: true,
    });
    useTxStore.setState({
      moxOn: true,
      wdspMicPk: -6,
      micAv: -18,
      alcGr: 3,
      lvlrGr: 3,
      cfcGr: 0,
      compPk: -6,
      compAv: -17,
      outPk: -6.1,
      outAv: -17,
      swr: 1.15,
    });

    const { container, unmount } = render(createElement(TxFidelityAdvisor));

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(container.textContent).toContain('Too hot');
    expect(container.textContent).toContain('VST chain output has almost no headroom');
    expect(container.textContent).toContain('NEXT Lower VST/plugin output trim before raising mic or drive');
    expect(metric(container, 'vstout').textContent).toContain('-0.2 dBFS');
    expect(metric(container, 'vstout').dataset.status).toBe('warn');

    unmount();
  });

  it('renders live density against an explicit station-profile target', async () => {
    useTxStore.setState({
      moxOn: true,
      wdspMicPk: -10,
      micAv: -21,
      alcGr: 3,
      lvlrGr: 4,
      cfcGr: 2,
      compPk: -10,
      compAv: -20,
      outPk: -3,
      outAv: -14,
      swr: 1.15,
    });

    const { container, unmount } = render(
      createElement(TxFidelityAdvisor, { targetSpectralDensity: 100 }),
    );

    await act(async () => {
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(metric(container, 'dens').textContent).toMatch(/DENS\d+\/84/);
    expect(metric(container, 'dens').dataset.status).toBe('warn');
    expect(container.textContent).toContain('Under-driven');
    expect(container.textContent).toContain('TX density is near but below clean profile target');
    expect(container.textContent).toContain('NEXT Add controlled speech density, not RF drive');

    unmount();
  });
});
