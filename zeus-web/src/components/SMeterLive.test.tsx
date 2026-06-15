// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { createElement, type ComponentType } from 'react';

import { render } from './meters/__tests__/harness';
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
}

describe('SMeterLive', () => {
  beforeEach(resetStores);

  it('renders calibrated RxMetersV2 signal and receiver-chain health in RX', () => {
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
    expect(container.textContent).toContain('RX chain optimized');
    expect(container.textContent).toContain('ADC HD18 dB');
    expect(container.textContent).toContain('AGC+4 dB');

    unmount();
  });

  it('falls back to the legacy RX meter before RxMetersV2 signal arrives', () => {
    useTxStore.setState({ rxDbm: -91 });

    const { container, unmount } = render(createElement(SMeterLiveComponent));

    expect(container.textContent).toContain('-91');
    expect(container.textContent).toContain('RX signal only');

    unmount();
  });

  it('keeps RX health chips hidden in mobile chip-suppressed mode', () => {
    useRxMetersStore.setState({ signalPk: -73, adcPk: -18, agcGain: 4 });

    const { container, unmount } = render(
      createElement(SMeterLiveComponent, { hideChips: true }),
    );

    expect(container.textContent).not.toContain('RX chain optimized');
    expect(container.textContent).not.toContain('ADC HD');

    unmount();
  });
});
