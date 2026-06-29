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
  WSJTX_DEFAULT_PORT: 2237,
  WSJTX_DEFAULT_GROUP: '224.0.0.73',
  WSJTX_DEFAULT_TTL: 1,
  WSJTX_DEFAULT_INSTANCE: 'WSJT-X',
  getWsjtxStatus: vi.fn(async () => ({
    enabled: false,
    host: '127.0.0.1',
    port: 2237,
    instanceId: 'WSJT-X',
    transport: 'unicast',
    multicastGroup: '224.0.0.73',
    multicastTtl: 1,
    sendQsoLogged: false,
    sendLiveDecodes: false,
  })),
  postWsjtxConfig: vi.fn(async (c: Record<string, unknown>) => ({ ...c })),
}));

vi.mock('../api/n1mm', () => ({
  N1MM_DEFAULT_PORT: 2333,
  getN1mmConfig: vi.fn(async () => ({ enabled: false, host: '127.0.0.1', port: 2333 })),
  postN1mmConfig: vi.fn(async (c: Record<string, unknown>) => ({ ...c })),
}));

vi.mock('../api/cloud-log', () => {
  const EMPTY = {
    wavelog: { enabled: false, baseUrl: '', stationProfileId: '', hasApiKey: false },
    clubLog: { enabled: false, email: '', callsign: '', hasPassword: false, hasApiKey: false },
  };
  return {
    getCloudLogStatus: vi.fn(async () => EMPTY),
    postCloudLogConfig: vi.fn(async () => EMPTY),
    postCloudLogCredentials: vi.fn(async () => EMPTY),
  };
});

import { ZeusDigitalSettingsPanel } from './ZeusDigitalSettingsPanel';
import { useFt8Store } from '../state/ft8-store';
import { useFt8SettingsStore } from '../state/ft8-settings-store';
import { useLayoutStore } from '../state/layout-store';
import { useWsjtxStore } from '../state/wsjtx-store';
import { useN1mmStore } from '../state/n1mm-store';
import { useCloudLogStore } from '../state/cloud-log-store';
import type { WsjtxConfig } from '../api/wsjtx';
import * as operatorApi from '../api/operator';
import * as ft8SettingsApi from '../api/ft8-settings';
import * as wsjtxApi from '../api/wsjtx';
import * as n1mmApi from '../api/n1mm';
import * as cloudLogApi from '../api/cloud-log';

const WSJTX_BASE_CONFIG: WsjtxConfig = {
  enabled: false,
  host: '127.0.0.1',
  port: 2237,
  instanceId: 'WSJT-X',
  transport: 'unicast',
  multicastGroup: '224.0.0.73',
  multicastTtl: 1,
  sendQsoLogged: false,
  sendLiveDecodes: false,
};

function setSelectValue(select: HTMLSelectElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLSelectElement.prototype,
    'value',
  )!.set!;
  act(() => {
    setter.call(select, value);
    select.dispatchEvent(new Event('change', { bubbles: true }));
  });
}

function clickSwitch(container: HTMLElement, label: string) {
  const btn = container.querySelector(
    `button[aria-label="${label}"]`,
  ) as HTMLButtonElement | null;
  expect(btn, `switch "${label}"`).toBeTruthy();
  act(() => btn!.click());
}

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
    // Reset the (singleton) WSJT-X store to a known disabled baseline so the
    // external-logging form starts clean in every test.
    useWsjtxStore.setState({ config: { ...WSJTX_BASE_CONFIG }, status: { ...WSJTX_BASE_CONFIG } });
    // Reset the singleton N1MM + cloud-log stores to a clean disabled baseline.
    useN1mmStore.setState({ config: { enabled: false, host: '127.0.0.1', port: 2333 } });
    useCloudLogStore.setState({
      status: {
        wavelog: { enabled: false, baseUrl: '', stationProfileId: '', hasApiKey: false },
        clubLog: { enabled: false, email: '', callsign: '', hasPassword: false, hasApiKey: false },
      },
    });
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
    const callInput = container.querySelector('input.zd-input') as HTMLInputElement;
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
    // that card so the live TX-section number inputs aren't swept in).
    const wfSection = Array.from(container.querySelectorAll('.ps-card')).find((s) =>
      s.querySelector('h4')?.textContent?.includes('Waterfall / display'),
    );
    expect(wfSection).toBeTruthy();
    const numInputs = Array.from(
      wfSection!.querySelectorAll('input.zd-num'),
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
    clickByText(container, 'Open Network settings →');
    expect(useLayoutStore.getState().settingsViewOpen).toBe(true);
    expect(useLayoutStore.getState().settingsInitialTab).toBe('network');
    unmount();
  });

  it('Logging group frames external logging as ADDITIVE to the internal logbook (default off)', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    expect(container.textContent).toContain('Zeus internal logbook');
    expect(container.textContent).toContain('Also send to an external logger');
    // Default off — the preset/host/port form is hidden until the operator opts in.
    expect(container.textContent).not.toContain('Logger preset');
    unmount();
  });

  it('opting in reveals the external-logger form and surfaces the multicast option', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    clickSwitch(container, 'Also send to an external logger');
    expect(container.textContent).toContain('Logger preset');
    expect(container.textContent).toContain('Host');
    expect(container.textContent).toContain('Transport');
    // Multicast group/TTL only show once Multicast transport is selected.
    expect(container.textContent).not.toContain('Multicast group');
    clickByText(container, 'Multicast');
    expect(container.textContent).toContain('Multicast group');
    expect(container.textContent).toContain('Multicast TTL');
    unmount();
  });

  it('GridTracker preset turns on the live decode/status stream', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    clickSwitch(container, 'Also send to an external logger');
    const presetSelect = container.querySelector(
      'select[aria-label="Logger preset"]',
    ) as HTMLSelectElement;
    expect(presetSelect).toBeTruthy();
    setSelectValue(presetSelect, 'gridtracker');
    const liveSwitch = container.querySelector(
      'button[aria-label="Send live decodes & status (for GridTracker)"]',
    ) as HTMLButtonElement;
    expect(liveSwitch.getAttribute('aria-checked')).toBe('true');
    unmount();
  });

  it('SAVE persists the full external-logger config (transport/multicast/live) to the backend', async () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    clickSwitch(container, 'Also send to an external logger');
    clickByText(container, 'Multicast');
    clickSwitch(container, 'Send live decodes & status (for GridTracker)');
    clickByText(container, 'SAVE');
    await act(async () => {
      await Promise.resolve();
    });
    expect(wsjtxApi.postWsjtxConfig).toHaveBeenCalledWith(
      expect.objectContaining({
        enabled: true,
        transport: 'multicast',
        multicastGroup: '224.0.0.73',
        sendLiveDecodes: true,
      }),
    );
    unmount();
  });

  it('N1MM-format logging is additive + default off; opting in reveals host/port and SAVE persists', async () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    expect(container.textContent).toContain('Also send to an HRD / N1MM-format logger');
    // Default off — host/port hidden until opted in.
    expect(
      container.querySelector('button[aria-label="Also send to an HRD / N1MM-format logger"]')
        ?.getAttribute('aria-checked'),
    ).toBe('false');
    clickSwitch(container, 'Also send to an HRD / N1MM-format logger');
    // The N1MM SAVE persists the enabled config (default port 2333) to the backend.
    const group = container.querySelector(
      'div[aria-label="N1MM-format logging (HRD / DXKeeper)"]',
    ) as HTMLElement;
    expect(group).toBeTruthy();
    clickByText(group, 'SAVE');
    await act(async () => {
      await Promise.resolve();
    });
    expect(n1mmApi.postN1mmConfig).toHaveBeenCalledWith(
      expect.objectContaining({ enabled: true, port: 2333 }),
    );
    unmount();
  });

  it('Wavelog cloud logging is additive + default off; opting in + SAVE persists config and the write-only API key', async () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    expect(container.textContent).toContain('Also push to Wavelog / Cloudlog');
    expect(container.textContent).toContain('Also push to Club Log');
    // Wavelog fields hidden until opted in.
    expect(container.textContent).not.toContain('Base URL');
    clickSwitch(container, 'Also push to Wavelog / Cloudlog');
    expect(container.textContent).toContain('Base URL');

    const group = container.querySelector(
      'div[aria-label="Cloud logging (Wavelog / Club Log)"]',
    ) as HTMLElement;
    const baseUrl = group.querySelector(
      'input.zd-input:not([type="password"]):not([type="number"])',
    ) as HTMLInputElement;
    setInputValue(baseUrl, 'https://log.example.com');
    const apiKey = group.querySelector('input[type="password"]') as HTMLInputElement;
    setInputValue(apiKey, 'SECRETKEY');

    clickByText(group, 'SAVE');
    await act(async () => {
      await Promise.resolve();
    });
    expect(cloudLogApi.postCloudLogConfig).toHaveBeenCalledWith(
      expect.objectContaining({
        wavelog: expect.objectContaining({ enabled: true, baseUrl: 'https://log.example.com' }),
      }),
    );
    // The secret goes up the write-only credentials endpoint, not the config one.
    expect(cloudLogApi.postCloudLogCredentials).toHaveBeenCalledWith(
      expect.objectContaining({ wavelogApiKey: 'SECRETKEY' }),
    );
    unmount();
  });

  it('surfaces QRZ cloud logging and deep-links to the QRZ tab', () => {
    const { container, unmount } = render(createElement(ZeusDigitalSettingsPanel));
    expect(container.textContent).toContain('QRZ Logbook (cloud)');
    clickByText(container, 'Open QRZ settings →');
    expect(useLayoutStore.getState().settingsViewOpen).toBe(true);
    expect(useLayoutStore.getState().settingsInitialTab).toBe('qrz');
    unmount();
  });
});
