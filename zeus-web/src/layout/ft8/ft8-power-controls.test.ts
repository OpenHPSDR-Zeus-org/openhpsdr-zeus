// SPDX-License-Identifier: GPL-2.0-or-later
//
// Power-control + HOLD-TX-FREQ WIRING tests. The FT8 power cluster reuses the
// EXISTING /api/tx/drive, /api/tx/tune-drive and /api/tx/tun endpoints (no
// forked power model). These assert the workspace controls POST to those exact
// endpoints with the expected bodies (fetch + localStorage stubbed), and that a
// waterfall click respects HOLD TX FREQ end-to-end (RX always moves; TX only
// when not held).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setDrive, setTun, setTuneDrive } from '../../api/client';
import { Ft8TxController } from '../../dsp/ft8-tx-controller';
import { resolveWaterfallClick } from '../../dsp/ft8-passband';

interface Call {
  url: string;
  body: Record<string, unknown>;
}

let calls: Call[];

function jsonResponse(body: unknown): Response {
  return {
    ok: true,
    status: 200,
    statusText: 'OK',
    json: async () => body,
  } as unknown as Response;
}

beforeEach(() => {
  calls = [];
  // Deterministic localStorage so importing api/client + stores never touches a
  // real (or absent) browser store.
  const store = new Map<string, string>();
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, String(v)),
    removeItem: (k: string) => void store.delete(k),
    clear: () => store.clear(),
    key: () => null,
    length: 0,
  });
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
      const u = String(url);
      const body = init?.body ? (JSON.parse(String(init.body)) as Record<string, unknown>) : {};
      calls.push({ url: u, body });
      if (u === '/api/tx/drive') return jsonResponse({ drivePercent: body.percent });
      if (u === '/api/tx/tune-drive') return jsonResponse({ tunePercent: body.percent });
      if (u === '/api/tx/tun') return jsonResponse({ tunOn: body.on });
      return jsonResponse({});
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('FT8 power controls reuse the existing TX endpoints', () => {
  it('TX POWER posts to /api/tx/drive with the percent', async () => {
    const r = await setDrive(35);
    expect(calls).toContainEqual({ url: '/api/tx/drive', body: { percent: 35 } });
    expect(r.drivePercent).toBe(35);
  });

  it('TUNE PWR posts to /api/tx/tune-drive with the percent', async () => {
    const r = await setTuneDrive(40);
    expect(calls).toContainEqual({ url: '/api/tx/tune-drive', body: { percent: 40 } });
    expect(r.tunePercent).toBe(40);
  });

  it('TUNE posts to /api/tx/tun', async () => {
    const r = await setTun(true);
    expect(calls).toContainEqual({ url: '/api/tx/tun', body: { on: true } });
    expect(r.tunOn).toBe(true);
  });
});

describe('waterfall click honours HOLD TX FREQ end-to-end', () => {
  it('moves the TX audio offset when not held', () => {
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn: globalThis.fetch });
    // HOLD TX FREQ defaults ON; release it so a click can move the TX offset.
    ctrl.setHoldTxFreq(false);
    const click = resolveWaterfallClick(1800, ctrl.getState().holdTxFreq);
    if (click.txOffsetHz != null) ctrl.setTxFreq(click.txOffsetHz);
    expect(ctrl.getAudioHz()).toBe(1800);
    // RX focus always moves regardless of hold.
    expect(click.rxFocusHz).toBe(1800);
  });

  it('does NOT move the TX audio offset when HOLD TX FREQ is engaged', () => {
    const ctrl = new Ft8TxController({ myCall: 'KB2UKA', myGrid4: 'FN12', fetchFn: globalThis.fetch });
    ctrl.setHoldTxFreq(false);
    ctrl.setTxFreq(1500); // baseline while unheld
    ctrl.setHoldTxFreq(true);
    const click = resolveWaterfallClick(2300, ctrl.getState().holdTxFreq);
    // Overlay guard: no TX update offered.
    expect(click.txOffsetHz).toBeNull();
    // Controller guard (belt-and-suspenders): setTxFreq is a no-op when held.
    ctrl.setTxFreq(2300);
    expect(ctrl.getAudioHz()).toBe(1500);
    // RX focus still moves.
    expect(click.rxFocusHz).toBe(2300);
  });
});
