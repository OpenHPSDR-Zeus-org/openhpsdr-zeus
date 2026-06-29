// SPDX-License-Identifier: GPL-2.0-or-later
//
// DxClusterSettingsPanel — Network-tab DX-cluster section test. Verifies the
// default-OFF form, that SAVE persists the operator-entered host/callsign/port,
// and that CONNECT calls the store. The harness import installs the localStorage
// polyfill + act flag before the store loads; store methods are stubbed so no
// real fetch fires.

import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from './meters/__tests__/harness';
import { DxClusterSettingsPanel } from './DxClusterSettingsPanel';
import { useDxClusterStore } from '../state/dxcluster-store';
import type { DxClusterConfig } from '../api/dxcluster';

const OFF: DxClusterConfig = {
  enabled: false,
  host: '',
  port: 7373,
  callsign: '',
  password: '',
  loginCommands: '',
  autoConnect: false,
};

function setInputValue(input: HTMLInputElement | HTMLTextAreaElement, value: string) {
  const proto =
    input instanceof HTMLTextAreaElement
      ? window.HTMLTextAreaElement.prototype
      : window.HTMLInputElement.prototype;
  const setter = Object.getOwnPropertyDescriptor(proto, 'value')!.set!;
  act(() => {
    setter.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

function clickByText(container: HTMLElement, text: string) {
  const btn = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === text);
  expect(btn, `button "${text}"`).toBeTruthy();
  act(() => (btn as HTMLButtonElement).click());
}

describe('DxClusterSettingsPanel', () => {
  beforeEach(() => {
    act(() => {
      useDxClusterStore.setState({
        config: { ...OFF },
        status: null,
        refreshStatus: async () => {},
        saveConfig: async () => ({
          ...OFF,
          hasPassword: false,
          state: 'Disconnected',
          spotsReceived: 0,
          lastSpotCallsign: null,
          error: null,
        }),
        connect: async () => ({
          ...OFF,
          hasPassword: false,
          state: 'Connecting',
          spotsReceived: 0,
          lastSpotCallsign: null,
          error: null,
        }),
        disconnect: async () => ({
          ...OFF,
          hasPassword: false,
          state: 'Disconnected',
          spotsReceived: 0,
          lastSpotCallsign: null,
          error: null,
        }),
      } as never);
    });
  });

  it('renders the DX cluster section, default disabled', () => {
    const { container, unmount } = render(createElement(DxClusterSettingsPanel));
    expect(container.textContent).toContain('DX CLUSTER (TELNET)');
    const enabled = container.querySelector('input[type="checkbox"]') as HTMLInputElement;
    expect(enabled.checked).toBe(false);
    unmount();
  });

  it('SAVE persists the operator-entered host/port/callsign', () => {
    const saveConfig = vi.fn(async () => ({
      ...OFF,
      hasPassword: false,
      state: 'Disconnected' as const,
      spotsReceived: 0,
      lastSpotCallsign: null,
      error: null,
    }));
    act(() => useDxClusterStore.setState({ saveConfig } as never));

    const { container, unmount } = render(createElement(DxClusterSettingsPanel));
    const enabled = container.querySelector('input[type="checkbox"]') as HTMLInputElement;
    act(() => enabled.click());

    setInputValue(container.querySelector('input[placeholder="dxc.example.org"]') as HTMLInputElement, 'dxc.example.org');
    setInputValue(container.querySelector('input[type="number"]') as HTMLInputElement, '7300');
    setInputValue(container.querySelector('input[placeholder="K1ABC"]') as HTMLInputElement, 'k1abc');

    clickByText(container, 'SAVE');
    expect(saveConfig).toHaveBeenCalledWith(
      expect.objectContaining({
        enabled: true,
        host: 'dxc.example.org',
        port: 7300,
        callsign: 'K1ABC',
      }),
    );
    unmount();
  });

  it('SAVE with an out-of-range port surfaces an error and does not persist', async () => {
    const saveConfig = vi.fn(async () => ({
      ...OFF,
      hasPassword: false,
      state: 'Disconnected' as const,
      spotsReceived: 0,
      lastSpotCallsign: null,
      error: null,
    }));
    act(() => useDxClusterStore.setState({ saveConfig } as never));

    const { container, unmount } = render(createElement(DxClusterSettingsPanel));
    setInputValue(container.querySelector('input[placeholder="dxc.example.org"]') as HTMLInputElement, 'dxc.example.org');
    setInputValue(container.querySelector('input[type="number"]') as HTMLInputElement, '99999');

    clickByText(container, 'SAVE');
    // The submit handler is async; flush its synchronous state update to the DOM.
    await act(async () => {});
    expect(saveConfig).not.toHaveBeenCalled();
    expect(container.textContent).toContain('Port must be a whole number between 1 and 65535.');

    // Editing the port clears the error, and a valid value now saves.
    setInputValue(container.querySelector('input[type="number"]') as HTMLInputElement, '7300');
    expect(container.textContent).not.toContain('Port must be a whole number');
    clickByText(container, 'SAVE');
    await act(async () => {});
    expect(saveConfig).toHaveBeenCalledWith(expect.objectContaining({ port: 7300 }));
    unmount();
  });

  it('CONNECT calls the store connect action', () => {
    const connect = vi.fn(async () => ({
      ...OFF,
      hasPassword: false,
      state: 'Connecting' as const,
      spotsReceived: 0,
      lastSpotCallsign: null,
      error: null,
    }));
    act(() =>
      useDxClusterStore.setState({ config: { ...OFF, enabled: true }, connect } as never),
    );

    const { container, unmount } = render(createElement(DxClusterSettingsPanel));
    clickByText(container, 'CONNECT');
    expect(connect).toHaveBeenCalled();
    unmount();
  });
});
