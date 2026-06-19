// SPDX-License-Identifier: GPL-2.0-or-later

import { expect, test, type Page, type Route } from '@playwright/test';

const defaultWorkspaceLayout = {
  schemaVersion: 8,
  tiles: [
    { uid: 'tile-filter', panelId: 'filter', x: 0, y: 0, w: 12, h: 10 },
    { uid: 'tile-filterpresets', panelId: 'filterpresets', x: 12, y: 0, w: 6, h: 10 },
    { uid: 'tile-hero', panelId: 'hero', x: 0, y: 10, w: 18, h: 38 },
    { uid: 'tile-vfo', panelId: 'vfo', x: 18, y: 0, w: 6, h: 11 },
    { uid: 'tile-smeter', panelId: 'smeter', x: 18, y: 11, w: 6, h: 5 },
    { uid: 'tile-tx', panelId: 'tx', x: 18, y: 16, w: 6, h: 10 },
    { uid: 'tile-txmeters', panelId: 'txmeters', x: 18, y: 26, w: 6, h: 12 },
    { uid: 'tile-dsp', panelId: 'dsp', x: 18, y: 38, w: 6, h: 10 },
  ],
};

interface TileRect {
  uid: string | null;
  x: number;
  y: number;
  width: number;
  height: number;
}

async function fulfillJson(route: Route, body: unknown) {
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

async function stubZeusApi(page: Page) {
  await page.addInitScript(() => {
    class MockZeusWebSocket extends EventTarget {
      readonly url: string;
      readonly protocol = '';
      readonly extensions = '';
      binaryType: BinaryType = 'blob';
      bufferedAmount = 0;
      readyState = WebSocket.CONNECTING;
      onopen: ((this: WebSocket, ev: Event) => unknown) | null = null;
      onmessage: ((this: WebSocket, ev: MessageEvent) => unknown) | null = null;
      onerror: ((this: WebSocket, ev: Event) => unknown) | null = null;
      onclose: ((this: WebSocket, ev: CloseEvent) => unknown) | null = null;

      constructor(url: string | URL) {
        super();
        this.url = String(url);
        window.setTimeout(() => {
          if (this.readyState !== WebSocket.CONNECTING) return;
          this.readyState = WebSocket.OPEN;
          const event = new Event('open');
          this.onopen?.call(this as unknown as WebSocket, event);
          this.dispatchEvent(event);
        }, 0);
      }

      send() {
        /* No realtime frames are needed for this workspace drag test. */
      }

      close() {
        if (this.readyState === WebSocket.CLOSED) return;
        this.readyState = WebSocket.CLOSED;
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
      await fulfillJson(route, { sdkAbi: 1, sdkVersion: '0.9.1', plugins: [] });
      return;
    }

    if (url.pathname === '/api/ui/layouts' && method === 'GET') {
      await fulfillJson(route, {
        radioKey: url.searchParams.get('radio') ?? 'default',
        layouts: [
          {
            id: 'default',
            name: 'Default',
            layoutJson: JSON.stringify(defaultWorkspaceLayout),
            updatedUtc: 0,
          },
        ],
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

    await fulfillJson(route, {});
  });
}

async function tileRects(page: Page): Promise<TileRect[]> {
  return page.locator('[data-tile-uid]').evaluateAll((tiles) =>
    tiles.map((tile) => {
      const rect = tile.getBoundingClientRect();
      return {
        uid: tile.getAttribute('data-tile-uid'),
        x: rect.x,
        y: rect.y,
        width: rect.width,
        height: rect.height,
      };
    }),
  );
}

function overlapPairs(rects: TileRect[]) {
  const pairs: Array<[string | null, string | null]> = [];
  for (let i = 0; i < rects.length; i += 1) {
    for (let j = i + 1; j < rects.length; j += 1) {
      const a = rects[i]!;
      const b = rects[j]!;
      const overlapX = a.x < b.x + b.width - 1 && a.x + a.width > b.x + 1;
      const overlapY = a.y < b.y + b.height - 1 && a.y + a.height > b.y + 1;
      if (overlapX && overlapY) pairs.push([a.uid, b.uid]);
    }
  }
  return pairs;
}

function rectFor(rects: TileRect[], uid: string) {
  const rect = rects.find((r) => r.uid === uid);
  if (!rect) throw new Error(`Expected rect for ${uid}`);
  return rect;
}

function expectRectNear(actual: TileRect, expected: TileRect) {
  expect(Math.abs(actual.x - expected.x)).toBeLessThanOrEqual(4);
  expect(Math.abs(actual.y - expected.y)).toBeLessThanOrEqual(4);
  expect(Math.abs(actual.width - expected.width)).toBeLessThanOrEqual(4);
  expect(Math.abs(actual.height - expected.height)).toBeLessThanOrEqual(4);
}

async function centerOf(locator: ReturnType<Page['locator']>) {
  const box = await locator.boundingBox();
  if (!box) throw new Error('Expected locator to have a bounding box');
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

test('dragging a panel into an occupied workspace slot displaces panels without blanking the grid', async ({
  page,
}) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (err) => pageErrors.push(err.message));
  await stubZeusApi(page);

  await page.goto('/');

  const draggedHeader = page.locator(
    '[data-tile-uid="tile-filterpresets"] .workspace-tile-header',
  );
  const targetHeader = page.locator('[data-tile-uid="tile-tx"] .workspace-tile-header');
  await expect(draggedHeader).toBeVisible();
  await expect(targetHeader).toBeVisible();

  const before = await tileRects(page);
  expect(before).toHaveLength(8);
  const draggedBefore = rectFor(before, 'tile-filterpresets');
  const targetBefore = rectFor(before, 'tile-tx');

  const start = await centerOf(draggedHeader);
  const target = await centerOf(targetHeader);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(target.x, target.y, { steps: 12 });
  await page.waitForTimeout(250);

  const duringDrag = await tileRects(page);
  expect(duringDrag).toHaveLength(8);
  expect(overlapPairs(duringDrag)).toEqual([]);
  expectRectNear(rectFor(duringDrag, 'tile-filterpresets'), targetBefore);
  expectRectNear(rectFor(duringDrag, 'tile-tx'), draggedBefore);

  await page.mouse.up();
  await page.waitForTimeout(500);

  const after = await tileRects(page);
  expect(after).toHaveLength(8);
  expect(overlapPairs(after)).toEqual([]);
  expectRectNear(rectFor(after, 'tile-filterpresets'), targetBefore);
  expectRectNear(rectFor(after, 'tile-tx'), draggedBefore);
  expect(pageErrors).not.toContainEqual(
    expect.stringContaining('Maximum update depth exceeded'),
  );
  expect(pageErrors).not.toContainEqual(
    expect.stringContaining('Minified React error #185'),
  );
});
