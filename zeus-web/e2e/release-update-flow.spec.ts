// SPDX-License-Identifier: GPL-2.0-or-later

import { expect, test, type Page, type Route } from '@playwright/test';

const downloadUrl =
  'https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus/releases/download/v0.9.2/OpenHPSDR-Zeus-v0.9.2-win-x64-setup.exe';

const releaseUpdateStatus = {
  isGitRepo: false,
  branch: null,
  currentSha: null,
  currentShortSha: null,
  currentSubject: null,
  upstreamRef: null,
  behind: 0,
  ahead: 0,
  dirty: false,
  canFastForward: false,
  latestRemoteSha: null,
  latestRemoteSubject: null,
  remoteUrl: null,
  checkedUtc: '2026-06-19T13:48:38.990Z',
  error: null,
  installedVersion: '0.9.1',
  runtimePlatform: 'windows',
  runtimeArchitecture: 'X64',
  updateAvailable: true,
  updateAction: 'download',
  latestVersion: '0.9.2',
  releaseTag: 'v0.9.2',
  releaseName: 'Zeus v0.9.2',
  releaseUrl: 'https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus/releases/tag/v0.9.2',
  releasePublishedUtc: '2026-06-19T12:00:00Z',
  releaseAssetName: 'OpenHPSDR-Zeus-v0.9.2-win-x64-setup.exe',
  releaseDownloadUrl: downloadUrl,
  releaseAssetSizeBytes: 134_217_728,
  releaseAssetDigest: 'sha256:0123456789abcdef',
};

async function fulfillJson(route: Route, body: unknown) {
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function stubZeusApi(page: Page) {
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
        /* No realtime frames are needed for this update-flow test. */
      }

      close() {
        if (this.readyState === NativeWebSocket.CLOSED) return;
        this.readyState = NativeWebSocket.CLOSED;
        const event = new CloseEvent('close');
        this.onclose?.call(this as unknown as WebSocket, event);
        this.dispatchEvent(event);
      }
    }

    const PatchedWebSocket = function (
      url: string | URL,
      protocols?: string | string[],
    ): WebSocket {
      const parsed = new URL(String(url), window.location.href);
      if (parsed.pathname === '/ws') {
        return new MockZeusWebSocket(url) as unknown as WebSocket;
      }
      return protocols === undefined
        ? new NativeWebSocket(url)
        : new NativeWebSocket(url, protocols);
    } as unknown as typeof WebSocket;
    Object.assign(PatchedWebSocket, {
      CONNECTING: NativeWebSocket.CONNECTING,
      OPEN: NativeWebSocket.OPEN,
      CLOSING: NativeWebSocket.CLOSING,
      CLOSED: NativeWebSocket.CLOSED,
    });
    PatchedWebSocket.prototype = NativeWebSocket.prototype;
    window.WebSocket = PatchedWebSocket;

    const openedUrls: string[] = [];
    Object.defineProperty(window, '__zeusOpenedUrls', {
      value: openedUrls,
      configurable: false,
    });
    window.open = ((url?: string | URL | null) => {
      openedUrls.push(url == null ? '' : String(url));
      return null;
    }) as typeof window.open;
  });

  await page.route(/^https?:\/\/[^/]+\/api(?:\/|\?|$)/, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();

    if (url.pathname === '/api/system/update') {
      await fulfillJson(route, releaseUpdateStatus);
      return;
    }

    if (url.pathname === '/api/capabilities') {
      await fulfillJson(route, {
        host: 'server',
        platform: 'windows',
        architecture: 'X64',
        version: '0.9.1',
        lanHttpsUrls: [],
        features: {},
      });
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
      await fulfillJson(route, {
        sdkAbi: 1,
        sdkVersion: '0.9.1',
        plugins: [],
      });
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

    await route.fulfill({
      status: 404,
      contentType: 'application/json',
      body: '{}',
    });
  });
}

test('packaged startup update opens Settings and the selected release asset', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (err) => pageErrors.push(err.message));
  await stubZeusApi(page);

  await page.goto('/');

  await expect(page.getByText('UPDATE AVAILABLE')).toBeVisible();
  await expect(page.getByText('Zeus 0.9.2 is ready')).toBeVisible();
  await expect(page.getByText(/Installed 0\.9\.1.*OpenHPSDR-Zeus-v0\.9\.2-win-x64-setup\.exe/)).toBeVisible();

  await page.getByRole('button', { name: 'DETAILS' }).click();

  await expect(page.getByRole('region', { name: 'Settings' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'UPDATES' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('heading', { name: 'SOFTWARE UPDATES' })).toBeVisible();
  await expect(page.getByText('Version 0.9.2 available')).toBeVisible();
  await expect(page.getByText('OpenHPSDR-Zeus-v0.9.2-win-x64-setup.exe')).toBeVisible();

  await page.getByRole('button', { name: 'UPDATE NOW' }).click();

  await expect
    .poll(() =>
      page.evaluate(() =>
        ((window as unknown as { __zeusOpenedUrls: string[] }).__zeusOpenedUrls),
      ),
    )
    .toEqual([downloadUrl]);

  expect(pageErrors).toEqual([]);
});
