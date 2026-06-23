// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
// See LICENSE / ATTRIBUTIONS.md at the repository root.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Node 25 ships a stub `localStorage` global that lacks the Storage API
// methods, shadowing jsdom's implementation. Install a minimal in-memory
// stand-in BEFORE importing the module under test so its first read sees
// a working API.
function installLocalStorageShim() {
  const store = new Map<string, string>();
  const shim: Storage = {
    getItem: (k) => (store.has(k) ? (store.get(k) as string) : null),
    setItem: (k, v) => void store.set(k, String(v)),
    removeItem: (k) => void store.delete(k),
    clear: () => store.clear(),
    key: (i) => Array.from(store.keys())[i] ?? null,
    get length() {
      return store.size;
    },
  };
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: shim,
  });
  Object.defineProperty(window, 'localStorage', {
    configurable: true,
    value: shim,
  });
}
installLocalStorageShim();

const {
  getServerBaseUrl,
  installFetchInterceptor,
  setServerBaseUrl,
  wsUrl,
} = await import('./serverUrl');

const STORAGE_KEY = 'zeus.serverUrl';

beforeEach(() => {
  localStorage.removeItem(STORAGE_KEY);
});

afterEach(() => {
  localStorage.removeItem(STORAGE_KEY);
});

describe('getServerBaseUrl / setServerBaseUrl', () => {
  it('defaults to empty string when nothing is stored', () => {
    expect(getServerBaseUrl()).toBe('');
  });

  it('round-trips a configured URL', () => {
    setServerBaseUrl('http://192.168.1.23:6060');
    expect(getServerBaseUrl()).toBe('http://192.168.1.23:6060');
  });

  it('strips trailing slashes', () => {
    setServerBaseUrl('http://192.168.1.23:6060/');
    expect(getServerBaseUrl()).toBe('http://192.168.1.23:6060');
    setServerBaseUrl('http://example.invalid///');
    expect(getServerBaseUrl()).toBe('http://example.invalid');
  });

  it('trims whitespace', () => {
    setServerBaseUrl('   http://10.0.0.5:6060   ');
    expect(getServerBaseUrl()).toBe('http://10.0.0.5:6060');
  });

  it('clears when given empty input', () => {
    setServerBaseUrl('http://10.0.0.5:6060');
    setServerBaseUrl('');
    expect(getServerBaseUrl()).toBe('');
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
  });
});

describe('wsUrl', () => {
  it('uses window.location when no base is configured', () => {
    // jsdom default origin is http://localhost
    expect(wsUrl('/ws')).toMatch(/^ws:\/\/localhost(:\d+)?\/ws$/);
  });

  it('uses ws:// with http base', () => {
    setServerBaseUrl('http://192.168.1.23:6060');
    expect(wsUrl('/ws')).toBe('ws://192.168.1.23:6060/ws');
  });

  it('uses wss:// with https base', () => {
    setServerBaseUrl('https://radio.example:443');
    expect(wsUrl('/ws')).toBe('wss://radio.example/ws');
  });
});

describe('installFetchInterceptor', () => {
  let originalFetch: typeof fetch;

  beforeEach(() => {
    originalFetch = window.fetch;
  });

  afterEach(() => {
    window.fetch = originalFetch;
    // clear the patch marker so each test reinstalls cleanly
    delete (window as unknown as Record<string, unknown>).__zeusServerUrlPatched;
  });

  it('is a no-op when no base is configured', async () => {
    const spy = vi.fn(async () => new Response('ok'));
    window.fetch = spy as unknown as typeof fetch;
    installFetchInterceptor();
    await window.fetch('/api/state');
    expect(spy).toHaveBeenCalledWith('/api/state', undefined);
  });

  it('rewrites /api/* paths when a base is configured', async () => {
    const spy = vi.fn(async () => new Response('ok'));
    window.fetch = spy as unknown as typeof fetch;
    installFetchInterceptor();
    setServerBaseUrl('http://192.168.1.23:6060');
    await window.fetch('/api/state');
    expect(spy).toHaveBeenCalledWith(
      'http://192.168.1.23:6060/api/state',
      undefined,
    );
  });

  it('leaves non-matching relative paths alone', async () => {
    const spy = vi.fn(async () => new Response('ok'));
    window.fetch = spy as unknown as typeof fetch;
    installFetchInterceptor();
    setServerBaseUrl('http://192.168.1.23:6060');
    await window.fetch('/static/icon.png');
    expect(spy).toHaveBeenCalledWith('/static/icon.png', undefined);
  });

  it('leaves absolute URLs untouched', async () => {
    const spy = vi.fn(async () => new Response('ok'));
    window.fetch = spy as unknown as typeof fetch;
    installFetchInterceptor();
    setServerBaseUrl('http://192.168.1.23:6060');
    await window.fetch('https://other.example/api/state');
    expect(spy).toHaveBeenCalledWith(
      'https://other.example/api/state',
      undefined,
    );
  });

  it('is idempotent — re-installing does not double-wrap', async () => {
    const spy = vi.fn(async () => new Response('ok'));
    window.fetch = spy as unknown as typeof fetch;
    installFetchInterceptor();
    installFetchInterceptor();
    installFetchInterceptor();
    setServerBaseUrl('http://10.0.0.5:6060');
    await window.fetch('/api/x');
    // If we'd double-wrapped, the URL would be doubly-prefixed garbage.
    expect(spy).toHaveBeenCalledWith('http://10.0.0.5:6060/api/x', undefined);
  });
});
