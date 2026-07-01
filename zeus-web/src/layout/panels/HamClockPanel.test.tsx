// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { act, render } from '../../components/meters/__tests__/harness';

// Mock the OS-browser bridge so we can assert it's invoked without launching a
// real browser. Must be hoisted above the component import.
const openExternalUrlMock = vi.fn();
vi.mock('../../components/report-problem/openExternalUrl', () => ({
  openExternalUrl: (url: string) => openExternalUrlMock(url),
}));

import { HamClockPanel } from './HamClockPanel';
import { hamclockIframeUrl, useHamClockStore, type HamClockStatus } from '../../state/hamclock-store';

const RUNNING_PORT = 59950;

const RUNNING_STATUS: HamClockStatus = {
  phase: 'Running',
  installed: true,
  running: true,
  busy: false,
  port: RUNNING_PORT,
  version: '26.4.1',
  nodeAvailable: true,
  nodeVersion: 'v22.23.0',
  error: null,
  log: [],
};

const STARTING_STATUS: HamClockStatus = {
  ...RUNNING_STATUS,
  phase: 'Starting',
  busy: true,
};

async function flush() {
  await act(async () => {
    for (let i = 0; i < 8; i++) await Promise.resolve();
  });
}

describe('HamClockPanel external-link forwarding', () => {
  beforeEach(() => {
    openExternalUrlMock.mockReset();
    useHamClockStore.setState({ status: RUNNING_STATUS });
    // The panel's mount effect calls loadStatus() (GET /api/hamclock/status);
    // keep it returning the running status so the iframe stays mounted.
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify(RUNNING_STATUS), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
    useHamClockStore.setState({ status: { ...RUNNING_STATUS, running: false, port: 0 } });
  });

  function dispatchFromIframe(iframe: HTMLIFrameElement, data: unknown, origin: string) {
    const event = new MessageEvent('message', {
      data,
      origin,
      source: iframe.contentWindow,
    });
    act(() => {
      window.dispatchEvent(event);
    });
  }

  it('waits for the healthy Running phase before mounting the iframe', async () => {
    useHamClockStore.setState({ status: STARTING_STATUS });
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify(STARTING_STATUS), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        }),
      ),
    );

    const { container, unmount } = render(createElement(HamClockPanel));
    await flush();

    expect(container.querySelector('iframe')).toBeNull();
    expect(container.textContent).toContain('Starting HamClock server');

    unmount();
  });

  it('forwards a zeus.openExternal message from the iframe to the OS browser', async () => {
    const { container, unmount } = render(createElement(HamClockPanel));
    await flush();

    const iframe = container.querySelector('iframe') as HTMLIFrameElement;
    expect(iframe).not.toBeNull();

    const origin = new URL(hamclockIframeUrl(RUNNING_PORT)).origin;
    const url = 'https://github.com/accius/openhamclock#readme';
    dispatchFromIframe(iframe, { type: 'zeus.openExternal', url }, origin);

    expect(openExternalUrlMock).toHaveBeenCalledTimes(1);
    expect(openExternalUrlMock).toHaveBeenCalledWith(url);

    unmount();
  });

  it('ignores a zeus.openExternal message from a wrong origin', async () => {
    const { container, unmount } = render(createElement(HamClockPanel));
    await flush();

    const iframe = container.querySelector('iframe') as HTMLIFrameElement;
    dispatchFromIframe(
      iframe,
      { type: 'zeus.openExternal', url: 'https://evil.example/phish' },
      'http://evil.example',
    );

    expect(openExternalUrlMock).not.toHaveBeenCalled();
    unmount();
  });

  it('ignores a non-http url in a zeus.openExternal message', async () => {
    const { container, unmount } = render(createElement(HamClockPanel));
    await flush();

    const iframe = container.querySelector('iframe') as HTMLIFrameElement;
    const origin = new URL(hamclockIframeUrl(RUNNING_PORT)).origin;
    dispatchFromIframe(iframe, { type: 'zeus.openExternal', url: 'file:///etc/passwd' }, origin);

    expect(openExternalUrlMock).not.toHaveBeenCalled();
    unmount();
  });
});
