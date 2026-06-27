// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8SettingsView render tests — the SETTINGS page wires the server-backed
// operator identity (the TX unblock) and the persisted FT8 prefs. Import the
// harness first so its localStorage polyfill + act flag are installed before any
// store module loads, and mock every REST layer the view's stores touch so no
// real fetch fires.

import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from '../../components/meters/__tests__/harness';

vi.mock('../../api/operator', () => ({
  getOperator: vi.fn(async () => ({
    callsign: '',
    grid: '',
    resolvedCallsign: '',
    resolvedGrid: '',
    callsignFromQrz: false,
    gridFromQrz: false,
    identityResolved: false,
  })),
  postOperator: vi.fn(async (id: { callsign: string; grid: string }) => ({
    callsign: id.callsign,
    grid: id.grid,
    resolvedCallsign: id.callsign,
    resolvedGrid: id.grid,
    callsignFromQrz: false,
    gridFromQrz: false,
    identityResolved: true,
  })),
}));

const FT8_DEFAULTS = vi.hoisted(() => ({
  autoSequence: true,
  callFirst: false,
  holdTxFreq: false,
  disableTxAfter73: true,
  defaultTxSlot: 0,
  defaultTxOffsetHz: 1500,
  rr73InsteadOfRrr: true,
  skipGrid: false,
  callerMaxRetries: 0,
  cqMessage: 'CQ',
  cqDxMessage: 'CQ DX',
  freeTextMacro: '',
  decodePasses: 3,
  showOnlyCq: false,
  hideWorkedBefore: false,
  autoLog: true,
  promptBeforeLog: false,
  clearDxAfterLog: true,
  reportToComment: false,
}));

vi.mock('../../api/ft8-settings', () => ({
  FT8_SETTINGS_DEFAULTS: FT8_DEFAULTS,
  getFt8Settings: vi.fn(async () => FT8_DEFAULTS),
  postFt8Settings: vi.fn(async (s: unknown) => s),
}));

vi.mock('../../api/spotting', () => ({
  getSpottingStatus: vi.fn(async () => ({
    pskReporterEnabled: false,
    wsprnetEnabled: false,
    callsign: '',
    grid: '',
    identityResolved: false,
  })),
  postSpottingConfig: vi.fn(async (c: unknown) => c),
}));

vi.mock('../../api/wsjtx', () => ({
  getWsjtxStatus: vi.fn(async () => ({ enabled: false, host: '127.0.0.1', port: 2237 })),
  postWsjtxConfig: vi.fn(async (c: unknown) => c),
}));

import { Ft8SettingsView } from './Ft8SettingsView';
import { useFt8Store } from '../../state/ft8-store';
import { useFt8SettingsStore } from '../../state/ft8-settings-store';
import * as operatorApi from '../../api/operator';
import * as ft8SettingsApi from '../../api/ft8-settings';

function setInputValue(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLInputElement.prototype,
    'value',
  )!.set!;
  act(() => {
    setter.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

describe('Ft8SettingsView', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useFt8Store.setState({ enabled: false, passes: 3, receiver: -1, protocol: 'FT8' });
    useFt8SettingsStore.setState({ settings: FT8_DEFAULTS, hydrated: true });
  });

  it('renders the Station / Operator identity fields (the unblock)', () => {
    const { container, unmount } = render(createElement(Ft8SettingsView));
    expect(container.textContent).toContain('My Call');
    expect(container.textContent).toContain('My Grid');
    expect(container.querySelectorAll('input.ft8-set-input').length).toBeGreaterThan(0);
    unmount();
  });

  it('writing My Call persists the shared operator identity to the server', () => {
    const { container, unmount } = render(createElement(Ft8SettingsView));
    const callInput = container.querySelector('input.ft8-set-input') as HTMLInputElement;
    setInputValue(callInput, 'k1abc');
    expect(operatorApi.postOperator).toHaveBeenCalledWith({ callsign: 'K1ABC', grid: '' });
    unmount();
  });

  it('decode-depth Deepest applies live (passes=4) and persists', () => {
    const { container, unmount } = render(createElement(Ft8SettingsView));
    // The depth scale maps to the engine: Normal=1, Deep=3, Deepest=4 (no bogus
    // "Fast" — passes=1 is the engine floor).
    const deepest = Array.from(container.querySelectorAll('button')).find(
      (b) => b.textContent === 'Deepest',
    ) as HTMLButtonElement;
    act(() => deepest.click());
    expect(useFt8Store.getState().passes).toBe(4);
    expect(ft8SettingsApi.postFt8Settings).toHaveBeenCalledWith(
      expect.objectContaining({ decodePasses: 4 }),
    );
    unmount();
  });

  it('surfaces the reporting status chips and a Network deep-link', () => {
    const { container, unmount } = render(createElement(Ft8SettingsView));
    expect(container.textContent).toContain('PSK Reporter');
    expect(container.textContent).toContain('WSPRnet');
    expect(container.textContent).toContain('WSJT-X UDP');
    expect(container.textContent).toContain('Open Network settings');
    unmount();
  });
});
