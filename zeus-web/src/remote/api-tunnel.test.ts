// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Unit tests for the read-only /api/* fetch shim (api-tunnel.ts):
//   - GET /api/x tunnels over the data channel and resolves the radio's reply
//   - non-GET (POST/…) resolves a synthetic 405 WITHOUT touching the channel
//   - non-/api requests delegate to the original fetch untouched
//   - requests issued BEFORE connect queue and flush once setApiChannel() lands

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installApiTunnel, setApiChannel, __resetApiTunnelForTests } from './api-tunnel';

/** A minimal fake RTCDataChannel that captures sends and lets the test reply. */
class FakeChannel {
  readyState: 'connecting' | 'open' | 'closed' = 'open';
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onclose: (() => void) | null = null;
  sent: string[] = [];

  send(data: string): void {
    this.sent.push(data);
  }

  /** Simulate the radio replying to a request with the given id. */
  reply(body: { id: number; status: number; headers?: Record<string, string>; body?: string }): void {
    this.onmessage?.({ data: JSON.stringify(body) } as MessageEvent);
  }

  open(): void {
    this.readyState = 'open';
    this.onopen?.();
  }
}

function lastRequest(ch: FakeChannel): { id: number; method: string; path: string } {
  return JSON.parse(ch.sent[ch.sent.length - 1]);
}

describe('api-tunnel fetch shim', () => {
  let originalFetch: typeof window.fetch;

  beforeEach(() => {
    originalFetch = vi.fn(async () => new Response('passthrough', { status: 200 })) as typeof window.fetch;
    window.fetch = originalFetch;
    installApiTunnel();
  });

  afterEach(() => {
    __resetApiTunnelForTests();
  });

  it('tunnels a same-origin /api GET and resolves the radio reply', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const respPromise = window.fetch('/api/state');
    // The request was sent over the channel, not the network.
    expect(ch.sent.length).toBe(1);
    const req = lastRequest(ch);
    expect(req.method).toBe('GET');
    expect(req.path).toBe('/api/state');
    expect(originalFetch).not.toHaveBeenCalled();

    ch.reply({
      id: req.id,
      status: 200,
      headers: { 'content-type': 'application/json' },
      body: '{"vfoA":14200000}',
    });

    const resp = await respPromise;
    expect(resp.status).toBe(200);
    expect(await resp.json()).toEqual({ vfoA: 14200000 });
  });

  it('preserves the query string in the tunnelled path', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const p = window.fetch('/api/filter/presets?mode=USB');
    const req = lastRequest(ch);
    expect(req.path).toBe('/api/filter/presets?mode=USB');
    ch.reply({ id: req.id, status: 200, body: '[]' });
    await p;
  });

  it('refuses a non-GET with a synthetic 405 and never touches the channel', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const resp = await window.fetch('/api/state', { method: 'POST', body: '{}' });
    expect(resp.status).toBe(405);
    expect(await resp.json()).toEqual({ error: 'Remote session is read-only' });
    expect(ch.sent.length).toBe(0); // nothing reached the radio
  });

  it('delegates non-/api requests to the original fetch', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const resp = await window.fetch('https://example.com/data.json');
    expect(originalFetch).toHaveBeenCalledTimes(1);
    expect(await resp.text()).toBe('passthrough');
    expect(ch.sent.length).toBe(0);
  });

  it('queues GETs issued before connect and flushes them once the channel lands', async () => {
    // No channel yet — this fires during app mount, before unlock.
    const respPromise = window.fetch('/api/capabilities');
    expect(originalFetch).not.toHaveBeenCalled();

    // Channel connects (open) — queued request flushes now.
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);
    expect(ch.sent.length).toBe(1);
    const req = lastRequest(ch);
    expect(req.path).toBe('/api/capabilities');

    ch.reply({ id: req.id, status: 200, body: '{"ok":true}' });
    const resp = await respPromise;
    expect(await resp.json()).toEqual({ ok: true });
  });

  it('flushes a queue once a connecting channel transitions to open', async () => {
    const respPromise = window.fetch('/api/state');

    const ch = new FakeChannel();
    ch.readyState = 'connecting';
    setApiChannel(ch as unknown as RTCDataChannel);
    expect(ch.sent.length).toBe(0); // still buffered — channel not open yet

    ch.open(); // onopen flush
    expect(ch.sent.length).toBe(1);
    const req = lastRequest(ch);
    ch.reply({ id: req.id, status: 200, body: 'null' });
    await respPromise;
  });

  it('fails pending requests with a network-style error on disconnect', async () => {
    const ch = new FakeChannel();
    setApiChannel(ch as unknown as RTCDataChannel);

    const p = window.fetch('/api/state');
    expect(ch.sent.length).toBe(1);

    setApiChannel(null); // disconnect
    await expect(p).rejects.toThrow(/closed/i);
  });
});
