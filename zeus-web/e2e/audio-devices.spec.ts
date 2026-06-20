// SPDX-License-Identifier: GPL-2.0-or-later

import { expect, test, type Page, type Route } from '@playwright/test';

type HostPlatform = 'linux' | 'darwin';

type AudioDeviceSelection = {
  inputDeviceId: string | null;
  outputDeviceId: string | null;
};

async function fulfillJson(route: Route, body: unknown) {
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function audioDevices(selection: AudioDeviceSelection) {
  return {
    supported: true,
    inputDeviceId: selection.inputDeviceId,
    outputDeviceId: selection.outputDeviceId,
    activeInputDeviceId: selection.inputDeviceId,
    activeOutputDeviceId: selection.outputDeviceId,
    inputs: [
      { id: 'capture-default', name: 'System Capture', isDefault: true },
      { id: 'capture-headset', name: 'Headset Mic', isDefault: false },
    ],
    outputs: [
      { id: 'playback-default', name: 'System Playback', isDefault: true },
      { id: 'playback-monitor', name: 'Monitor Speakers', isDefault: false },
    ],
    error: null,
  };
}

async function stubZeusApi(page: Page, platform: HostPlatform) {
  const selection: AudioDeviceSelection = {
    inputDeviceId: null,
    outputDeviceId: null,
  };
  const writes: AudioDeviceSelection[] = [];

  await page.addInitScript(() => {
    const NativeWebSocket = window.WebSocket;
    class MockZeusWebSocket extends EventTarget {
      readonly url: string;
      readonly protocol = '';
      readonly extensions = '';
      binaryType: BinaryType = 'blob';
      bufferedAmount = 0;
      readyState = NativeWebSocket.CONNECTING;
      onopen: ((this: WebSocket, ev: Event) => unknown) | null = null;
      onmessage: ((this: WebSocket, ev: MessageEvent) => unknown) | null = null;
      onerror: ((this: WebSocket, ev: Event) => unknown) | null = null;
      onclose: ((this: WebSocket, ev: CloseEvent) => unknown) | null = null;

      constructor(url: string | URL) {
        super();
        this.url = String(url);
        window.setTimeout(() => {
          if (this.readyState !== NativeWebSocket.CONNECTING) return;
          this.readyState = NativeWebSocket.OPEN;
          const event = new Event('open');
          this.onopen?.call(this as unknown as WebSocket, event);
          this.dispatchEvent(event);
        }, 0);
      }

      send() {
        /* No realtime frames are needed for this settings e2e. */
      }

      close() {
        if (this.readyState === NativeWebSocket.CLOSED) return;
        this.readyState = NativeWebSocket.CLOSED;
        const event = new CloseEvent('close');
        this.onclose?.call(this as unknown as WebSocket, event);
        this.dispatchEvent(event);
      }
    }

    window.WebSocket = MockZeusWebSocket as unknown as typeof WebSocket;
  });

  await page.route(/^https?:\/\/[^/]+\/api(?:\/|\?|$)/, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();

    if (url.pathname === '/api/capabilities') {
      await fulfillJson(route, {
        host: 'desktop',
        platform,
        architecture: platform === 'darwin' ? 'arm64' : 'x64',
        version: 'e2e',
        lanHttpsUrls: [],
        features: {},
      });
      return;
    }

    if (url.pathname === '/api/audio/devices') {
      if (method === 'PUT') {
        const body = JSON.parse(request.postData() ?? '{}') as AudioDeviceSelection;
        selection.inputDeviceId = body.inputDeviceId ?? null;
        selection.outputDeviceId = body.outputDeviceId ?? null;
        writes.push({ ...selection });
      }
      await fulfillJson(route, audioDevices(selection));
      return;
    }

    if (url.pathname === '/api/radio/selection') {
      await fulfillJson(route, {
        preferred: 'Auto',
        connected: 'Unknown',
        effective: 'Unknown',
        overrideDetection: false,
      });
      return;
    }

    if (url.pathname === '/api/plugins') {
      await fulfillJson(route, { sdkAbi: 1, sdkVersion: 'e2e', plugins: [] });
      return;
    }

    if (url.pathname === '/api/ui/layouts' && method === 'GET') {
      await fulfillJson(route, {
        radioKey: url.searchParams.get('radio') ?? 'default',
        layouts: [],
        activeLayoutId: 'default',
      });
      return;
    }

    if (url.pathname === '/api/ui/layouts') {
      await route.fulfill({ status: 204, body: '' });
      return;
    }

    if (url.pathname === '/api/theme-settings') {
      await fulfillJson(route, { theme: 'dark', overrides: {} });
      return;
    }

    if (url.pathname.endsWith('/processing-mode')) {
      await fulfillJson(route, { mode: 'native', engineAvailable: false, engineActive: false });
      return;
    }

    if (url.pathname.endsWith('/master-bypass')) {
      await fulfillJson(route, { bypassed: false });
      return;
    }

    if (url.pathname.endsWith('/profiles')) {
      await fulfillJson(route, { profiles: [] });
      return;
    }

    if (url.pathname === '/api/audio-suite/preview') {
      await fulfillJson(route, { supported: true, enabled: false, meterOnly: false });
      return;
    }

    await fulfillJson(route, {});
  });

  return writes;
}

for (const platform of ['linux', 'darwin'] as const) {
  test(`Audio Tools native device selectors round-trip on ${platform}`, async ({ page }) => {
    const pageErrors: string[] = [];
    page.on('pageerror', (err) => pageErrors.push(err.message));
    const writes = await stubZeusApi(page, platform);

    await page.goto('/#pa');
    await expect(page.getByRole('region', { name: 'Settings' })).toBeVisible();
    await page.getByRole('tab', { name: 'AUDIO TOOLS' }).click();

    await expect(page.getByRole('region', { name: 'Audio Devices' })).toBeVisible();
    await expect(page.getByText('HOST')).toBeVisible();

    const input = page.getByLabel('Audio input device');
    const output = page.getByLabel('Audio output device');
    await expect(input).toContainText('Headset Mic');
    await expect(output).toContainText('Monitor Speakers');

    await input.selectOption('capture-headset');
    await expect.poll(() => writes).toContainEqual({
      inputDeviceId: 'capture-headset',
      outputDeviceId: null,
    });

    await output.selectOption('playback-monitor');
    await expect.poll(() => writes).toContainEqual({
      inputDeviceId: 'capture-headset',
      outputDeviceId: 'playback-monitor',
    });

    await expect(input).toHaveValue('capture-headset');
    await expect(output).toHaveValue('playback-monitor');
    expect(pageErrors).toEqual([]);
  });
}
