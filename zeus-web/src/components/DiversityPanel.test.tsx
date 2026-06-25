/** @vitest-environment jsdom */

import { createElement } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  DIVERSITY_CONFIG_DEFAULT,
  setDiversity,
  type DiversityConfigDto,
  type RadioStateDto,
  type ReceiverDto,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useRadioStore } from '../state/radio-store';
import { UNKNOWN_BOARD_CAPABILITIES } from '../api/board-capabilities';
import { act, render } from './meters/__tests__/harness';
import { DiversityPanel } from './DiversityPanel';

function currentStateDto(): RadioStateDto {
  return useConnectionStore.getState() as unknown as RadioStateDto;
}

vi.mock('../api/client', async () => {
  const actual = await vi.importActual<typeof import('../api/client')>('../api/client');
  return {
    ...actual,
    setDiversity: vi.fn(async () => currentStateDto()),
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
  diversity?: DiversityConfigDto | null;
  rxAdcCount?: number;
}) {
  useConnectionStore.setState({
    status: 'Connected',
    connectedProtocol: opts.protocol ?? 'P2',
    receivers: opts.receivers ?? [rx(0, true), rx(1, true, 1)],
    diversity: opts.diversity ?? null,
    maxReceivers: 8,
  });
  useRadioStore.setState({
    capabilities: {
      ...UNKNOWN_BOARD_CAPABILITIES,
      rxAdcCount: opts.rxAdcCount ?? 2,
    },
  });
}

describe('DiversityPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('gates on a Protocol-2 connection', () => {
    setup({ protocol: 'P1' });
    const { container, unmount } = render(createElement(DiversityPanel));
    expect(container.textContent).toContain('Protocol 2 only');
    expect(container.querySelector('[aria-label="Diversity gain"]')).toBeNull();
    unmount();
  });

  it('gates on dual-ADC capability', () => {
    setup({ protocol: 'P2', rxAdcCount: 1 });
    const { container, unmount } = render(createElement(DiversityPanel));
    expect(container.textContent).toContain('Single-ADC board');
    expect(container.querySelector('[aria-label="Diversity gain"]')).toBeNull();
    unmount();
  });

  it('toggles enabled and sends to the API', async () => {
    setup({ protocol: 'P2', diversity: { ...DIVERSITY_CONFIG_DEFAULT } });
    const { container, unmount } = render(createElement(DiversityPanel));
    const toggle = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent === 'OFF');
    expect(toggle).toBeTruthy();
    await act(async () => {
      toggle?.click();
      await Promise.resolve();
    });
    expect(setDiversity).toHaveBeenCalledWith({ enabled: true });
    unmount();
  });

  it('commits phase changes', async () => {
    setup({
      protocol: 'P2',
      diversity: { ...DIVERSITY_CONFIG_DEFAULT, enabled: true },
    });
    const { container, unmount } = render(createElement(DiversityPanel));
    const phase = container.querySelector<HTMLInputElement>('[aria-label="Diversity phase"]');
    expect(phase).toBeTruthy();
    await act(async () => {
      if (!phase) throw new Error('expected phase slider');
      // React tracks input.value via a hidden property; assigning .value
      // directly bypasses the tracker. Use the prototype setter so the
      // 'input' event surfaces the change to React.
      const setter = Object.getOwnPropertyDescriptor(
        HTMLInputElement.prototype,
        'value',
      )!.set!;
      setter.call(phase, '45');
      phase.dispatchEvent(new Event('input', { bubbles: true }));
      await Promise.resolve();
    });
    expect(setDiversity).toHaveBeenCalledWith({ phaseDeg: 45 });
    unmount();
  });

  it('warns when the source receiver sits on ADC 0', () => {
    setup({
      protocol: 'P2',
      receivers: [rx(0, true), rx(1, true, 0)],
      diversity: { ...DIVERSITY_CONFIG_DEFAULT, enabled: true, sourceRx: 1 },
    });
    const { container, unmount } = render(createElement(DiversityPanel));
    expect(container.textContent).toContain('same antenna as RX1');
    unmount();
  });
});
