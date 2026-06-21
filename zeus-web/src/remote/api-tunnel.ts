// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Read-only REST tunnel for remote (WebRTC) RX monitoring. On the static Pages
// host the SPA has no same-origin `/api/*` backend, so its REST chrome (VFO,
// mode, band, capabilities, AGC, …) renders empty. This module monkeypatches
// `window.fetch` so that same-origin `/api/*` GET/HEAD requests are tunnelled
// over the WebRTC "api" DataChannel to the operator's radio, which loopback-
// proxies them to its own local Kestrel and returns the response.
//
// SAFETY POSTURE (matches the server-side gate):
//   - READ-ONLY: only GET/HEAD tunnel. Any non-GET (POST/PUT/DELETE/PATCH) is
//     refused locally with a synthetic 405 and NEVER reaches the radio — this
//     preserves "RX monitoring only", no remote TX/tuning/control.
//   - DENY-BY-DEFAULT: tunnelled GETs QUEUE until the channel is open AND the
//     session is unlocked, then flush. Nothing is sent before unlock.
//   - Every tunnelled request has a timeout so a never-connecting session
//     rejects rather than hanging the UI forever.
//   - Non-`/api` requests delegate to the original fetch untouched.
//
// The complementary sensitive-endpoint denylist lives on the SERVER
// (RemoteWebRtcSession) so the radio is the authority on what it will read.

const API_PREFIX = '/api/';
const REQUEST_TIMEOUT_MS = 15_000;

interface TunnelResponse {
  id: number;
  status: number;
  headers?: Record<string, string>;
  body?: string;
}

interface Pending {
  resolve: (r: TunnelResponse) => void;
  reject: (e: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

interface QueuedRequest {
  id: number;
  path: string;
  method: string;
}

let installed = false;
let originalFetch: typeof window.fetch | null = null;

let channel: RTCDataChannel | null = null;
let nextId = 1;
const pending = new Map<number, Pending>();
const queue: QueuedRequest[] = [];

/**
 * Resolve a fetch input to a same-origin `/api/...` path (including query
 * string) or null if it is not a same-origin API request. Accepts the same
 * input shapes the real fetch does (string, URL, Request).
 */
function resolveApiPath(input: RequestInfo | URL): string | null {
  let raw: string;
  if (typeof input === 'string') raw = input;
  else if (input instanceof URL) raw = input.href;
  else if (input instanceof Request) raw = input.url;
  else raw = String(input);

  let url: URL;
  try {
    url = new URL(raw, window.location.origin);
  } catch {
    return null;
  }
  if (url.origin !== window.location.origin) return null;
  if (!url.pathname.startsWith(API_PREFIX)) return null;
  return url.pathname + url.search;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (init?.method) return init.method.toUpperCase();
  if (input instanceof Request) return input.method.toUpperCase();
  return 'GET';
}

/** Send the request over the channel now (channel is open + unlocked). */
function dispatch(req: QueuedRequest): void {
  channel!.send(JSON.stringify({ id: req.id, method: req.method, path: req.path }));
}

/**
 * Enqueue (or immediately dispatch) a tunnelled GET/HEAD and return a Promise
 * that resolves with the radio's response or rejects on timeout/disconnect.
 */
function tunnel(path: string, method: string): Promise<TunnelResponse> {
  const id = nextId++;
  return new Promise<TunnelResponse>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      // Drop any still-queued copy so a flush can't resurrect a dead id.
      const qi = queue.findIndex((q) => q.id === id);
      if (qi >= 0) queue.splice(qi, 1);
      reject(new Error('Remote API request timed out.'));
    }, REQUEST_TIMEOUT_MS);

    pending.set(id, { resolve, reject, timer });
    const req: QueuedRequest = { id, path, method };
    if (channel && channel.readyState === 'open') dispatch(req);
    else queue.push(req);
  });
}

/** Build a synthetic browser Response from the radio's tunnelled reply. */
function toResponse(r: TunnelResponse): Response {
  const headers = new Headers(r.headers ?? {});
  // HEAD/204/304 cannot carry a body per the Fetch spec — pass null.
  const bodyless = r.status === 204 || r.status === 304;
  return new Response(bodyless ? null : (r.body ?? ''), { status: r.status, headers });
}

function readOnlyRefusal(): Response {
  return new Response(JSON.stringify({ error: 'Remote session is read-only' }), {
    status: 405,
    headers: { 'content-type': 'application/json' },
  });
}

function onChannelMessage(ev: MessageEvent): void {
  let msg: TunnelResponse;
  try {
    const raw = typeof ev.data === 'string' ? ev.data : new TextDecoder().decode(ev.data);
    msg = JSON.parse(raw) as TunnelResponse;
  } catch {
    return; // ignore malformed replies
  }
  const p = pending.get(msg.id);
  if (!p) return;
  clearTimeout(p.timer);
  pending.delete(msg.id);
  p.resolve(msg);
}

/**
 * Hand the tunnel its live "api" DataChannel once the WebRTC session is
 * connected AND unlocked. Flushes any requests queued before connect. Pass null
 * on disconnect to clear the channel and fail every pending request with a
 * network-style error.
 */
export function setApiChannel(ch: RTCDataChannel | null): void {
  if (ch === null) {
    channel = null;
    const err = new Error('Remote API channel closed.');
    for (const [, p] of pending) {
      clearTimeout(p.timer);
      p.reject(err);
    }
    pending.clear();
    queue.length = 0;
    return;
  }

  channel = ch;
  ch.onmessage = onChannelMessage;
  ch.onclose = () => {
    if (channel === ch) setApiChannel(null);
  };

  const flush = () => {
    while (queue.length) dispatch(queue.shift()!);
  };
  if (ch.readyState === 'open') flush();
  else ch.onopen = flush;
}

/**
 * Install the read-only `/api/*` fetch shim. Idempotent. Call once at remote-
 * client module load (when isRemoteMode()) so the shim is live BEFORE the
 * app's mount effects fire their `/api/state` etc. requests — those queue
 * until setApiChannel() flushes them post-unlock.
 */
export function installApiTunnel(): void {
  if (installed) return;
  installed = true;
  originalFetch = window.fetch.bind(window);

  window.fetch = (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const path = resolveApiPath(input);
    if (path === null) return originalFetch!(input, init);

    const method = methodOf(input, init);
    if (method !== 'GET' && method !== 'HEAD') {
      // Read-only: refuse locally, never touch the radio.
      return Promise.resolve(readOnlyRefusal());
    }
    return tunnel(path, method).then(toResponse);
  };
}

/** Test-only: restore the original fetch and reset module state. */
export function __resetApiTunnelForTests(): void {
  if (originalFetch) window.fetch = originalFetch;
  installed = false;
  originalFetch = null;
  channel = null;
  nextId = 1;
  for (const [, p] of pending) clearTimeout(p.timer);
  pending.clear();
  queue.length = 0;
}
