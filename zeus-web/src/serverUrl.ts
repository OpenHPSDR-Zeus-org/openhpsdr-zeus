// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
// See LICENSE / ATTRIBUTIONS.md at the repository root.

// Configurable server base URL. The web build defaults to relative paths
// (same-origin, served by Zeus.Server itself) so this module is invisible
// to browser users. Capacitor builds can't assume same-origin — the WebView
// runs on capacitor:// or http://localhost — so the user picks a LAN host
// like "http://192.168.1.23:6060" and we route every /api/* call and the
// /ws WebSocket to it.

const STORAGE_KEY = 'zeus.serverUrl';
const CHANGED_EVENT = 'zeus:server-url-changed';
const FETCH_PATCH_MARKER = '__zeusServerUrlPatched';

// Paths the interceptor rewrites onto the configured base. Anything else
// (absolute URLs, third-party hosts, blob: / data: / file: schemes) passes
// through untouched.
const REWRITE_PREFIXES = ['/api/', '/ws', '/hub/'] as const;

export type ServerUrlChangedEvent = CustomEvent<{ url: string }>;

interface PhotinoSurface {
  external?: {
    sendMessage?: unknown;
  };
}

function readRaw(): string {
  if (typeof localStorage === 'undefined') return '';
  try {
    return localStorage.getItem(STORAGE_KEY) ?? '';
  } catch {
    return '';
  }
}

/**
 * Returns the configured server base URL with no trailing slash, e.g.
 * "http://192.168.1.23:6060". Empty string means "use relative paths"
 * (the standard browser flow). Never throws.
 */
export function getServerBaseUrl(): string {
  return sanitizeForCurrentRuntime(normalize(readRaw()));
}

/** Persist a new base URL. Pass empty string to clear. */
export function setServerBaseUrl(url: string): void {
  const cleaned = sanitizeForCurrentRuntime(normalize(url));
  try {
    if (cleaned === '') {
      localStorage.removeItem(STORAGE_KEY);
    } else {
      localStorage.setItem(STORAGE_KEY, cleaned);
    }
  } catch {
    // Storage may be unavailable (private mode, etc.); the caller already
    // sees the rejection via the next read.
  }
  if (typeof window !== 'undefined') {
    window.dispatchEvent(
      new CustomEvent(CHANGED_EVENT, { detail: { url: cleaned } }) as ServerUrlChangedEvent,
    );
  }
}

function sanitizeForCurrentRuntime(url: string): string {
  if (!shouldClearForDesktopLoopback(url)) return url;
  clearStoredServerBaseUrl();
  return '';
}

function clearStoredServerBaseUrl(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Storage may be unavailable; the in-memory return value is still safe.
  }
}

function shouldClearForDesktopLoopback(url: string): boolean {
  if (url === '' || typeof window === 'undefined') return false;
  // Desktop Photino must stay on loopback HTTP; LAN HTTPS uses Zeus' self-signed cert.
  if (!isPhotinoDesktopShell() || !isLoopbackPage()) return false;
  try {
    const target = new URL(url);
    return target.protocol === 'https:' && isLocalNetworkHost(target.hostname);
  } catch {
    return false;
  }
}

function isPhotinoDesktopShell(): boolean {
  if (typeof window === 'undefined') return false;
  const external = (window as unknown as PhotinoSurface).external;
  return typeof external?.sendMessage === 'function';
}

function isLoopbackPage(): boolean {
  try {
    return isLocalNetworkHost(window.location.hostname);
  } catch {
    return false;
  }
}

function isLocalNetworkHost(hostname: string): boolean {
  const host = hostname.toLowerCase().replace(/^\[(.*)]$/, '$1');
  if (host === 'localhost' || host === '::1') return true;
  if (isPrivateIpv4(host)) return true;
  return (
    host.includes(':') &&
    (host.startsWith('fe80:') || host.startsWith('fc') || host.startsWith('fd'))
  );
}

function isPrivateIpv4(host: string): boolean {
  const parts = host.split('.');
  if (parts.length !== 4) return false;
  const octets = parts.map((part) => Number(part));
  if (octets.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) {
    return false;
  }
  const a = octets[0] ?? -1;
  const b = octets[1] ?? -1;
  return (
    a === 10 ||
    a === 127 ||
    (a === 169 && b === 254) ||
    (a === 172 && b >= 16 && b <= 31) ||
    (a === 192 && b === 168)
  );
}

/** Strip whitespace and any trailing slashes. Empty in → empty out. */
function normalize(raw: string): string {
  const trimmed = (raw ?? '').trim();
  if (trimmed === '') return '';
  return trimmed.replace(/\/+$/, '');
}

/** Subscribe to base-URL changes (returns an unsubscribe). */
export function onServerBaseUrlChanged(cb: (url: string) => void): () => void {
  if (typeof window === 'undefined') return () => undefined;
  const handler = (ev: Event) => {
    const detail = (ev as ServerUrlChangedEvent).detail;
    cb(detail?.url ?? '');
  };
  window.addEventListener(CHANGED_EVENT, handler);
  return () => window.removeEventListener(CHANGED_EVENT, handler);
}

/**
 * True when the page is hosted by Capacitor (Android/iOS native shell).
 * Capacitor exposes a global, but it lands after the bridge initialises —
 * we also accept the capacitor:// protocol or the runtime-injected meta tag
 * so an isCapacitor() check at module-init time still works.
 */
export function isCapacitorRuntime(): boolean {
  if (typeof window === 'undefined') return false;
  const w = window as unknown as { Capacitor?: { isNativePlatform?: () => boolean } };
  if (w.Capacitor?.isNativePlatform?.()) return true;
  try {
    if (window.location.protocol === 'capacitor:') return true;
  } catch {
    // ignore
  }
  return false;
}

/**
 * Build a ws://host/path or wss://host/path URL using the configured
 * server, falling back to window.location when no base is set.
 */
export function wsUrl(path: string): string {
  if (typeof window === 'undefined') return path;
  const base = getServerBaseUrl();
  if (base) {
    try {
      const u = new URL(base);
      const proto = u.protocol === 'https:' ? 'wss:' : 'ws:';
      return `${proto}//${u.host}${path.startsWith('/') ? path : '/' + path}`;
    } catch {
      // fall through to same-origin fallback
    }
  }
  const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${window.location.host}${path.startsWith('/') ? path : '/' + path}`;
}

function shouldRewrite(input: string): boolean {
  if (/^[a-z][a-z0-9+.-]*:/i.test(input)) return false; // absolute URL
  return REWRITE_PREFIXES.some((p) => input === p || input.startsWith(p));
}

function rewriteRequestInfo(input: RequestInfo | URL, base: string): RequestInfo | URL {
  if (typeof input === 'string') {
    return shouldRewrite(input) ? base + input : input;
  }
  if (input instanceof URL) return input;
  // Request object — only rewrite if it's a same-origin relative path. The
  // simplest reliable signal is comparing against window.location.origin.
  try {
    const reqUrl = new URL(input.url);
    const here = new URL(window.location.href);
    if (reqUrl.origin !== here.origin) return input;
    const path = reqUrl.pathname + reqUrl.search + reqUrl.hash;
    if (!shouldRewrite(path)) return input;
    return new Request(base + path, input);
  } catch {
    return input;
  }
}

/**
 * Patch window.fetch so that any /api/*, /ws*, or /hub/* request is sent
 * to the configured server base instead of same-origin. Idempotent — safe
 * to call from main.tsx and again from tests. The patch is a no-op when no
 * base is configured (web users on the bundled deploy).
 */
export function installFetchInterceptor(): void {
  if (typeof window === 'undefined') return;
  const w = window as unknown as Record<string, unknown>;
  if (w[FETCH_PATCH_MARKER]) return;
  const original = window.fetch.bind(window);
  const patched: typeof fetch = (input, init) => {
    const base = getServerBaseUrl();
    if (!base) return original(input, init);
    return original(rewriteRequestInfo(input, base), init);
  };
  window.fetch = patched;
  w[FETCH_PATCH_MARKER] = true;
}
