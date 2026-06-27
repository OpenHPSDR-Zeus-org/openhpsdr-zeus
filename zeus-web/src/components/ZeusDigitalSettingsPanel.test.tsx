// SPDX-License-Identifier: GPL-2.0-or-later
//
// ZeusDigitalSettingsPanel — the "Zeus Digital" section of the MAIN Settings
// menu. Verifies the GLOBAL operator identity (the TX unblock), the PER-MODE
// config plumbing (mode selector → per-mode hydrate/persist), the live decode
// depth wiring, the waterfall/display controls, and the Network deep-link.
// Import the harness first so its localStorage polyfill + act flag are installed
// before any store loads, and mock every REST layer so no real fetch fires.

import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from './meters/__tests__/harness';

vi.mock('../api/operator', () => ({
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
  wfDbMin: -140,
  wfDbMax: -50,
  palette: 'blue',
  rbw: 'auto',
  smoothing: 0,
  zoom: 1.0,
  spanHz: 3000,
}));

vi.mock('../api/ft8-settings', () => ({
  DIGITAL_MODES: ['FT8', 'FT4', 'WSPR'] as const,
  WF_PALETTES: ['blue', 'inferno', 'viridis'] as const,
  WF_SMOOTHING_MIN: 0,
  WF_SMOOTHING_MAX: 10,
  WF_ZOOM_MIN: 1.0,
  WF_ZOOM_MAX: 64.0,
  WF_SPAN_MIN_HZ: 500,
  WF_SPAN_MAX_HZ: 6000,
  FT8_SETTINGS_DEFAULTS: FT8_DEFAULTS,
  getFt8Settings: vi.fn(async () => FT8_DEFAULTS),
  postFt8Settings: vi.fn(async (_mode: unknown, s: unknown) => s),
}));

vi.mock('../api/spotting', () => ({
  getSpottingStatus: vi.fn(async () => ({
    pskReporterEnabled: false,
    wsprnetEnabled: false,
    callsign: '',
    grid: '',
    identityResolved: false,
  })),
  postSpottingConfig: vi.fn(async (c: unknown) => c),
}));

vi.mock('../api/wsjtx', () => ({
  getWsjtxStatus: vi.fn(async () => ({ enabled: false, host: '127.0.0.1', port: 2237 })),
  postWsjtxConfig: vi.fn(async (c: unknown) => c),
}));

import { ZeusDigitalSettingsPanel } from './ZeusDigitalSettingsPanel';
import { useFt8Store } from '../state/ft8-store';
import { useFt8SettingsStore } from '../state/ft8-settings-store';
import { useLayoutStore } from '../state/layout-store';
import * as operatorApi from '../api/operator';
import * as ft8SettingsApi from '../api/ft8-settings';

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

function clickByText(container: HTMLElement, text: string) {
  const btn = Array.from(container.querySelectorAll('button')).find(
    (b) => b.textContent === text,
  ) as HTMLButtonElement | undefined;
  expect(btn, `button "${text}"`).toBeTruthy();
  act(() => btn!.click());
}

describe('ZeusDigitalSettingsPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useFt8Store.setState({ enabled: false, passes: 3, receiver: -1, protocol: 'FT8' });
    useFt8SettingsStore.setState({
      byMode: { FT8: FT8_DEFAULTS, FT4: FT8_DEFAULTS, WSPR: FT8_DEFAULTS },
      hydrated: { FT8: true, FT4: true, WSPR: true },
    });
    useLayoutStore.setState({ settingsViewOpen: false, settingsInitialTab: undefined });
  });

  it('renders the GLOBAL Station identity fields and the mode selector', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    expect(container.textContent).toContain('My Call');
    expect(container.textContent).toContain('My Grid');
    // Mode selector exposes all three per-mode tabs.
    const segLabels = Array.from(container.querySelectorAll('button')).map((b) => b.textContent);
    expect(segLabels).toContain('FT8');
    expect(segLabels).toContain('FT4');
    expect(segLabels).toContain('WSPR');
    unmount();
  });

  it('writing My Call persists the shared (global) operator identity', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    const callInput = container.querySelector('input.ft8-set-input') as HTMLInputElement;
    setInputValue(callInput, 'k1abc');
    expect(operatorApi.postOperator).toHaveBeenCalledWith({ callsign: 'K1ABC', grid: '' });
    unmount();
  });

  it('decode-depth Deepest applies live (passes=4) and persists to the active mode', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    clickByText(container, 'Deepest');
    expect(useFt8Store.getState().passes).toBe(4);
    expect(ft8SettingsApi.postFt8Settings).toHaveBeenCalledWith(
      'FT8',
      expect.objectContaining({ decodePasses: 4 }),
    );
    unmount();
  });

  it('switching mode hydrates that mode and routes edits to it (per-mode persist)', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    clickByText(container, 'FT4'); // switch the editing mode
    // The mode-change effect re-hydrates the newly selected mode from the server.
    expect(
      vi.mocked(ft8SettingsApi.getFt8Settings).mock.calls.some((c) => c[0] === 'FT4'),
    ).toBe(true);
    // A subsequent edit now targets FT4, not FT8.
    clickByText(container, 'Deepest');
    expect(ft8SettingsApi.postFt8Settings).toHaveBeenCalledWith(
      'FT4',
      expect.objectContaining({ decodePasses: 4 }),
    );
    unmount();
  });

  it('renders the per-mode waterfall/display controls', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    expect(container.textContent).toContain('Waterfall dB min');
    expect(container.textContent).toContain('Palette');
    expect(container.textContent).toContain('Span');
    unmount();
  });

  it('surfaces the waterfall/display controls as "coming soon" (disabled, no persist) until #1014', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    // The palette segmented control is disabled and a click does not POST.
    const palette = Array.from(container.querySelectorAll('button')).find(
      (b) => b.textContent === 'Inferno',
    ) as HTMLButtonElement | undefined;
    expect(palette).toBeTruthy();
    expect(palette!.disabled).toBe(true);
    act(() => palette!.click());
    expect(ft8SettingsApi.postFt8Settings).not.toHaveBeenCalled();
    // The numeric inputs WITHIN the waterfall section are disabled too (scope to
    // that section so the live TX-section number inputs aren't swept in).
    const wfSection = Array.from(container.querySelectorAll('section')).find((s) =>
      s.querySelector('.ft8-region__head')?.textContent?.includes('Waterfall / display'),
    );
    expect(wfSection).toBeTruthy();
    const numInputs = Array.from(
      wfSection!.querySelectorAll('input.ft8-set-input--num'),
    ) as HTMLInputElement[];
    expect(numInputs.length).toBeGreaterThan(0);
    expect(numInputs.every((i) => i.disabled)).toBe(true);
    unmount();
  });

  it('WSPR hides the TX & Decode sections and QSO-logging toggles, keeps Station/Waterfall/Reporting', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    clickByText(container, 'WSPR');
    // Beacon mode — no TX sequencer, no QSO decode filters, no QSO logging.
    expect(container.textContent).not.toContain('TX & Auto-sequence');
    expect(container.textContent).not.toContain('Decode depth');
    expect(container.textContent).not.toContain('Auto-log QSO');
    // Station identity, waterfall/display and reporting status stay available.
    expect(container.textContent).toContain('My Call');
    expect(container.textContent).toContain('Waterfall / display');
    expect(container.textContent).toContain('PSK Reporter');
    unmount();
  });

  it('surfaces the reporting chips and deep-links to the Network tab', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    expect(container.textContent).toContain('PSK Reporter');
    expect(container.textContent).toContain('WSPRnet');
    expect(container.textContent).toContain('WSJT-X UDP');
    clickByText(container, 'Open Network settings →');
    expect(useLayoutStore.getState().settingsViewOpen).toBe(true);
    expect(useLayoutStore.getState().settingsInitialTab).toBe('network');
    unmount();
  });
});
