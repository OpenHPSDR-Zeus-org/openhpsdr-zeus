// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createElement, type ComponentType } from 'react';

import { render } from './meters/__tests__/harness';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useTxStore } from '../state/tx-store';
import { SMeterLive } from './SMeterLive';

const SMeterLiveComponent = SMeterLive as ComponentType<{ hideChips?: boolean }>;

beforeAll(() => {
  const g = globalThis as unknown as {
    ResizeObserver?: typeof ResizeObserver;
    requestAnimationFrame?: typeof requestAnimationFrame;
    cancelAnimationFrame?: typeof cancelAnimationFrame;
  };
  if (!g.ResizeObserver) {
    g.ResizeObserver = class ResizeObserver {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
  }
  if (!g.requestAnimationFrame) {
    g.requestAnimationFrame = (cb: FrameRequestCallback) =>
      window.setTimeout(() => cb(performance.now()), 16);
    g.cancelAnimationFrame = (id: number) => window.clearTimeout(id);
  }
});

function resetStores() {
  useTxStore.setState({
    moxOn: false,
    tunOn: false,
    fwdWatts: 0,
    swr: 1,
    micDbfs: -100,
    rxDbm: -130,
  });
  useRxMetersStore.setState({
    signalPk: -Infinity,
    signalAv: -Infinity,
    adcPk: -Infinity,
    adcAv: -Infinity,
    agcGain: 0,
    agcEnvPk: -Infinity,
    agcEnvAv: -Infinity,
  });
  useSignalEnhanceStore.getState().setSignalEnhanceSceneStatus(null);
}

function setSceneSnr(maxSnrDb: number) {
  useSignalEnhanceStore.getState().setSignalEnhanceSceneStatus({
    atUtc: '2026-06-17T19:30:00Z',
    profileId: 'balanced',
    baseProfileId: 'voice',
    reason: 'test signal',
    peakCount: 1,
    coherentPeakCount: 1,
    peaksPer10Khz: 0.4,
    occupiedPct: 1.2,
    coherentOccupiedPct: 0.8,
    impulsivePct: 0,
    maxSnrDb,
    coherentMaxSnrDb: maxSnrDb,
  });
}

describe('SMeterLive', () => {
  beforeEach(resetStores);

  it('renders calibrated RxMetersV2 signal in RX without receiver-chain chips', () => {
    useTxStore.setState({ rxDbm: -130 });
    useRxMetersStore.setState({
      signalPk: -73,
      signalAv: -76,
      adcPk: -18,
      adcAv: -30,
      agcGain: 4,
      agcEnvPk: -78,
      agcEnvAv: -82,
    });

    const { container, unmount } = render(createElement(SMeterLiveComponent));

    expect(container.textContent).toContain('-73');
    expect(container.textContent).not.toContain('RX chain optimized');
    expect(container.textContent).not.toContain('ADC HD');
    expect(container.textContent).not.toContain('AGC+4 dB');

    unmount();
  });

  it('falls back to the legacy RX meter before RxMetersV2 signal arrives', () => {
    useTxStore.setState({ rxDbm: -91 });

    const { container, unmount } = render(createElement(SMeterLiveComponent));

    expect(container.textContent).toContain('-91');
    expect(container.textContent).not.toContain('RX signal only');

    unmount();
  });

  it('renders the RX signal difference above the noise floor', () => {
    useTxStore.setState({ rxDbm: -130 });
    useRxMetersStore.setState({
      signalPk: -88,
      signalAv: -91,
    });
    setSceneSnr(18.4);

    const { container, unmount } = render(createElement(SMeterLiveComponent));

    expect(container.textContent).toContain('-88');
    expect(container.textContent).toContain('SNR 18 dB');

    unmount();
  });

  it('keeps TX chips hidden in mobile chip-suppressed mode', () => {
    useTxStore.setState({
      moxOn: true,
      fwdWatts: 25,
      swr: 2.34,
      micDbfs: -22,
    });

    const { container, unmount } = render(
      createElement(SMeterLiveComponent, { hideChips: true }),
    );

    expect(container.textContent).toContain('25.0');
    expect(container.textContent).not.toContain('SWR');
    expect(container.textContent).not.toContain('MIC');

    unmount();
  });

  it('renders TX SWR and MIC chips while transmitting', () => {
    useTxStore.setState({
      moxOn: true,
      fwdWatts: 25,
      swr: 2.34,
      micDbfs: -22,
    });

    const { container, unmount } = render(createElement(SMeterLiveComponent));

    expect(container.textContent).toContain('25.0');
    expect(container.textContent).toContain('SWR2.34');
    expect(container.textContent).toContain('MIC-22 dBfs');

    unmount();
  });
});
