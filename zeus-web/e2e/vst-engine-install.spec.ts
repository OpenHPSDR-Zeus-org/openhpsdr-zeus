// SPDX-License-Identifier: GPL-2.0-or-later
//
// "Download VST Engine" — a new operator on a fresh PC selects the VST route,
// finds no engine installed, and clicks one button that downloads, installs,
// and CONFIGURES the out-of-process engine so VST is immediately working. This
// drives that flow end-to-end against a stubbed backend: the install endpoints
// report progress, and the configure step flips the TX route to an active VST
// engine. Mirrors audio-devices.spec.ts for the Settings → Audio Tools harness.

import { expect, test, type Page, type Route } from '@playwright/test';

async function fulfillJson(route: Route, body: unknown) {
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

// Server-side install + engine state, mutated by the stubbed endpoints so the
// flow reads like the real thing: engine absent → installed → active.
type EngineWorld = {
  engineInstalled: boolean;
  engineActive: boolean;
  installPosts: number;
  configurePuts: number;
};

async function stubZeusApi(page: Page): Promise<EngineWorld> {
  const world: EngineWorld = {
    engineInstalled: false,
    engineActive: false,
    installPosts: 0,
    configurePuts: 0,
  };

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
        /* No realtime frames are needed for this install e2e. */
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
        platform: 'windows',
        architecture: 'x64',
        version: 'e2e',
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
      await fulfillJson(route, { sdkAbi: 1, sdkVersion: 'e2e', plugins: [] });
      return;
    }

    if (url.pathname === '/api/audio/devices') {
      await fulfillJson(route, {
        supported: true,
        inputDeviceId: null,
        outputDeviceId: null,
        activeInputDeviceId: null,
        activeOutputDeviceId: null,
        inputs: [],
        outputs: [],
        error: null,
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

    if (url.pathname === '/api/theme-settings') {
      await fulfillJson(route, { theme: 'dark', overrides: {} });
      return;
    }

    // VST engine provisioning. POST kicks the "download" off and immediately
    // marks the engine installed; GET reports it done. (The real server streams
    // download → extract → stage; the contract the frontend depends on is the
    // terminal "done" + engineAvailable flip, which is what we assert.)
    if (url.pathname.endsWith('/vst-engine/install')) {
      if (method === 'POST') {
        world.installPosts += 1;
        world.engineInstalled = true;
        await fulfillJson(route, {
          phase: 'downloading',
          percent: 0,
          message: 'Downloading…',
          engineAvailable: false,
        });
        return;
      }
      await fulfillJson(route, {
        phase: 'done',
        percent: 100,
        message: 'VST engine installed.',
        engineAvailable: world.engineInstalled,
      });
      return;
    }

    if (url.pathname.endsWith('/processing-mode')) {
      if (method === 'PUT') {
        // The configure step: enabling VST activates the engine once installed.
        world.configurePuts += 1;
        world.engineActive = world.engineInstalled;
      }
      await fulfillJson(route, {
        mode: 'vst',
        engineAvailable: world.engineInstalled,
        engineActive: world.engineActive,
      });
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
    if (url.pathname.endsWith('/chain/order')) {
      await fulfillJson(route, { pluginIds: [] });
      return;
    }

    await fulfillJson(route, {});
  });

  return world;
}

test('new operator downloads, installs, and auto-configures the VST engine', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (err) => pageErrors.push(err.message));
  const world = await stubZeusApi(page);

  await page.goto('/#pa');
  await expect(page.getByRole('region', { name: 'Settings' })).toBeVisible();
  await page.getByRole('tab', { name: 'AUDIO TOOLS' }).click();

  // The TX tools panel is in VST mode (engine absent) → the download affordance
  // is offered, and "Download Audio Suite" (the native-mode peer) is not.
  // Scope to the TX Audio rail — the RX Audio rail also offers a "Download VST
  // Engine" button on Windows when the shared engine is missing (issue #1276).
  const txRail = page.getByRole('region', { name: 'TX Audio' });
  const getEngine = txRail.getByRole('button', { name: 'Download VST Engine' });
  await expect(getEngine).toBeVisible();
  await expect(page.getByRole('button', { name: 'Download Audio Suite' })).toHaveCount(0);

  await getEngine.click();

  // Install runs, then the configure step flips the route to an active engine.
  await expect(txRail.getByRole('button', { name: 'VST Engine Ready' })).toBeVisible();
  await expect(page.getByText('VST engine ready — TX audio now routes through VST.')).toBeVisible();

  // The engine was actually installed and configured exactly once.
  await expect.poll(() => world.installPosts).toBe(1);
  await expect.poll(() => world.configurePuts).toBeGreaterThanOrEqual(1);
  await expect.poll(() => world.engineActive).toBe(true);

  expect(pageErrors).toEqual([]);
});
