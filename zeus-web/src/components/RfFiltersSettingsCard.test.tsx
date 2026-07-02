// SPDX-License-Identifier: GPL-2.0-or-later

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from './meters/__tests__/harness';
import { RfFiltersSettingsCard } from './RfFiltersSettingsCard';
import {
  useRfFilterStore,
  type RfFilterActive,
  type RfFilterSettings,
} from '../state/rf-filter-store';

const ACTIVE: RfFilterActive = {
  profileKey: 'anan-7000',
  profileLabel: 'ANAN-7000 / Saturn BPF',
  rx1Hz: 7_186_000,
  rx2Hz: 7_186_000,
  txHz: 7_186_000,
  txActive: false,
  rx1Key: '40_30',
  rx1Label: '40 / 30 m',
  rx2Key: '40_30',
  rx2Label: '40 / 30 m',
  txKey: '60_40',
  txLabel: '60 / 40 m LPF',
  reason: 'Legacy auto matrix active.',
};

function settings(): RfFilterSettings {
  return {
    supported: true,
    boardFamily: 'ANAN-7000 / Saturn BPF',
    activeProfileKey: 'anan-7000',
    customMatrixEnabled: false,
    rxBypassAll: false,
    rxBypassOnTx: false,
    rxBypassOnPureSignal: false,
    profiles: [
      {
        key: 'anan-7000',
        label: 'ANAN-7000 / Saturn BPF',
        rxFilters: [
          { key: '40_30', label: '40 / 30 m', startHz: 5_500_000, endHz: 10_999_999, forceBypass: false },
          { key: '6_pre', label: '6 m / preamp', startHz: 35_000_000, endHz: 61_440_000, forceBypass: false },
        ],
        txFilters: [
          { key: '12_10', label: '12 / 10 m LPF', startHz: 24_000_001, endHz: 35_600_000, forceBypass: false },
          { key: '6_bypass', label: '6 m / bypass LPF', startHz: 35_600_001, endHz: 61_440_000, forceBypass: false },
        ],
      },
    ],
    active: ACTIVE,
    warnings: [],
  };
}

function seedStore(save = vi.fn(async (_next: RfFilterSettings) => {})) {
  act(() => {
    useRfFilterStore.setState({
      settings: settings(),
      loaded: true,
      inflight: false,
      error: null,
      load: async () => {},
      save,
      reset: async () => {},
    } as never);
  });
  return save;
}

function clickByText(container: HTMLElement, text: string) {
  const button = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === text);
  expect(button, `button "${text}"`).toBeTruthy();
  act(() => (button as HTMLButtonElement).click());
}

function setInputValue(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!;
  act(() => {
    input.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    setter.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new FocusEvent('focusout', { bubbles: true }));
  });
}

describe('RfFiltersSettingsCard', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('keeps edits in a draft until Save is pressed', () => {
    const save = seedStore();
    const { container, unmount } = render(createElement(RfFiltersSettingsCard));

    clickByText(container, 'Manual');
    expect(save).not.toHaveBeenCalled();

    const saveButton = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === 'Save');
    expect((saveButton as HTMLButtonElement).disabled).toBe(false);
    clickByText(container, 'Save');

    expect(save).toHaveBeenCalledWith(expect.objectContaining({ customMatrixEnabled: true }));
    unmount();
  });

  it('preserves Thetis-style six-decimal MHz boundaries in the save payload', () => {
    const save = seedStore();
    const { container, unmount } = render(createElement(RfFiltersSettingsCard));

    const inputs = Array.from(container.querySelectorAll('input.rf-filter-input')) as HTMLInputElement[];
    const boundary = inputs.find((input) => input.value === '35.600001');
    expect(boundary).toBeTruthy();

    setInputValue(boundary!, '35.600002');
    expect(save).not.toHaveBeenCalled();
    clickByText(container, 'Save');

    const saved = save.mock.calls[0]![0] as RfFilterSettings;
    const tx = saved.profiles[0]!.txFilters.find((row) => row.key === '6_bypass');
    expect(tx?.startHz).toBe(35_600_002);
    unmount();
  });
});
