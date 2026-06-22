/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ReceiverDto } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { render } from './meters/__tests__/harness';
import { MultiRxMonitorStrip } from './MultiRxMonitorStrip';

function rx(index: number, enabled: boolean): ReceiverDto {
  return {
    index,
    enabled,
    adcSource: 0,
    vfoHz: 14_200_000 + index * 1_000_000,
    mode: 'USB',
    filterLowHz: 100,
    filterHighHz: 2850,
    filterPresetName: 'VAR1',
    afGainDb: 0,
    sampleRateHz: 192_000,
  };
}

function setup(opts: { protocol?: 'P1' | 'P2' | null; receivers?: ReceiverDto[] }) {
  useConnectionStore.setState({
    status: 'Connected',
    connectedProtocol: opts.protocol ?? 'P2',
    receivers: opts.receivers ?? [rx(0, true)],
  });
}

describe('MultiRxMonitorStrip', () => {
  beforeEach(() => {
    // jsdom has no WebGL2 — RxMonitorPane logs and bails out of renderer setup.
    // Suppress the expected console.error so it doesn't pollute test output.
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('renders nothing on a non-P2 connection even with RX3 enabled', () => {
    setup({ protocol: 'P1', receivers: [rx(0, true), rx(1, true), rx(2, true)] });
    const { container, unmount } = render(createElement(MultiRxMonitorStrip));
    expect(container.querySelector('[data-multi-rx-strip]')).toBeNull();
    unmount();
  });

  it('renders nothing when only RX1/RX2 are enabled', () => {
    setup({ protocol: 'P2', receivers: [rx(0, true), rx(1, true)] });
    const { container, unmount } = render(createElement(MultiRxMonitorStrip));
    expect(container.querySelector('[data-multi-rx-strip]')).toBeNull();
    unmount();
  });

  it('renders one monitor pane per enabled RX3+ receiver', () => {
    setup({
      protocol: 'P2',
      receivers: [rx(0, true), rx(1, true), rx(2, true), rx(3, true)],
    });
    const { container, unmount } = render(createElement(MultiRxMonitorStrip));

    const strip = container.querySelector('[data-multi-rx-strip]');
    expect(strip).not.toBeNull();
    // One pane (canvas) each for RX3 and RX4; RX1/RX2 live in HeroPanel, not here.
    expect(container.querySelectorAll('canvas').length).toBe(2);
    expect(container.textContent).toContain('RX3');
    expect(container.textContent).toContain('RX4');
    unmount();
  });

  it('skips a disabled extra receiver (contiguity gap is not rendered)', () => {
    setup({
      protocol: 'P2',
      receivers: [rx(0, true), rx(1, true), rx(2, true), rx(3, false)],
    });
    const { container, unmount } = render(createElement(MultiRxMonitorStrip));
    expect(container.querySelectorAll('canvas').length).toBe(1);
    expect(container.textContent).toContain('RX3');
    expect(container.textContent).not.toContain('RX4');
    unmount();
  });
});
