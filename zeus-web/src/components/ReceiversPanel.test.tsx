/** @vitest-environment jsdom */

import { createElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { setReceiver, type RadioStateDto, type ReceiverDto } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { act, render } from './meters/__tests__/harness';
import { ReceiversPanel } from './ReceiversPanel';

function currentStateDto(): RadioStateDto {
  return useConnectionStore.getState() as unknown as RadioStateDto;
}

vi.mock('../api/client', async () => {
  const actual = await vi.importActual<typeof import('../api/client')>('../api/client');
  return {
    ...actual,
    setReceiver: vi.fn(async () => currentStateDto()),
  };
});

function rx(index: number, enabled: boolean, adcSource = 0): ReceiverDto {
  return {
    index,
    enabled,
    adcSource,
    vfoHz: 14_200_000 + index * 1_000_000,
    mode: 'USB',
    filterLowHz: 100,
    filterHighHz: 2850,
    filterPresetName: 'VAR1',
    afGainDb: 0,
    sampleRateHz: 192_000,
    muted: false,
  };
}

function setup(opts: {
  protocol?: 'P1' | 'P2' | null;
  receivers?: ReceiverDto[];
}) {
  useConnectionStore.setState({
    status: 'Connected',
    connectedProtocol: opts.protocol ?? 'P2',
    receivers: opts.receivers ?? [rx(0, true)],
    maxReceivers: 8,
  });
}

describe('ReceiversPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('gates multi-DDC on a Protocol-2 connection', () => {
    setup({ protocol: 'P1' });
    const { container, unmount } = render(createElement(ReceiversPanel));
    expect(container.textContent).toContain('Protocol 2 only');
    // No exposed-count buttons on a non-P2 radio.
    expect(container.querySelector('[aria-label="Exposed receiver count"]')).toBeNull();
    unmount();
  });

  it('exposes a contiguous receiver count via the per-index endpoint', async () => {
    setup({ protocol: 'P2', receivers: [rx(0, true)] });
    const { container, unmount } = render(createElement(ReceiversPanel));

    const three = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent === '3');
    expect(three).toBeTruthy();

    await act(async () => {
      three?.click();
      await Promise.resolve();
    });

    // Exposing 3 enables index 2 (server contiguity turns on RX2..RX3).
    expect(setReceiver).toHaveBeenCalledWith(2, { enabled: true });
    unmount();
  });

  it('assigns a per-DDC ADC source', async () => {
    setup({ protocol: 'P2', receivers: [rx(0, true), rx(1, true)] });
    const { container, unmount } = render(createElement(ReceiversPanel));

    const adcSelects = container.querySelectorAll<HTMLSelectElement>('select');
    // One ADC select per active receiver (RX1, RX2).
    expect(adcSelects.length).toBe(2);

    const sel = adcSelects[1];
    if (!sel) throw new Error('expected an ADC select for RX2');
    await act(async () => {
      sel.value = '1';
      sel.dispatchEvent(new Event('change', { bubbles: true }));
      await Promise.resolve();
    });

    expect(setReceiver).toHaveBeenCalledWith(1, { adcSource: 1 });
    unmount();
  });
});
