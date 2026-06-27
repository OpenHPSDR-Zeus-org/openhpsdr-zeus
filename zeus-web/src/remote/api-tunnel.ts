// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Read-write REST tunnel for remote (WebRTC) operating. On the static Pages
// host the SPA has no same-origin `/api/*` backend, so its REST chrome and
// controls (VFO, mode, band, filter, AGC, drive, MOX/TUN, …) cannot reach the
// radio. This module monkeypatches `window.fetch` so that same-origin `/api/*`
// requests — reads AND writes — are tunnelled over the WebRTC "api" DataChannel
// to the operator's radio, which loopback-proxies them to its own local Kestrel
// and returns the response. This is what makes a remote session control the
// radio natively, as if sitting at the desktop app.
//
// SAFETY POSTURE (the SERVER is the authority — this side mirrors it):
//   - DENY-BY-DEFAULT: tunnelled requests QUEUE until the channel is open AND
//     the session is unlocked, then flush. Nothing is sent before unlock.
//   - The server enforces the real guards: an always-denied list (secrets /
//     identity / exports), a write-denied list (PureSignal burn-zone + prefs
//     DB), path-traversal/loopback/size caps, and a dead-man TX un-key if the
//     link drops while keyed. See RemoteWebRtcSession.
//   - Every tunnelled request has a timeout so a never-connecting session
//     rejects rather than hanging the UI forever.
//   - Non-`/api` requests delegate to the original fetch untouched.

const API_PREFIX = '/api/';
const REQUEST_TIMEOUT_MS = 15_000;

// The LAN Browser proxy (/api/lan/proxy?...&inline=1) is the one tunnelled
// endpoint whose reply is legitimately slow: the radio host fetches a whole
// device page AND inlines its stylesheets/images/fonts before replying. The
// 15 s default — sized for the small chrome JSON — fires before that work can
// finish, so this path gets a longer deadline (kept above the server's own
// inline budget; see LanProxyService.InlineMaxDuration).
const LAN_PROXY_PREFIX = '/api/lan/proxy';
const LAN_PROXY_TIMEOUT_MS = 60_000;

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
  body?: string;
  contentType?: string;
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
  const msg: Record<string, unknown> = { id: req.id, method: req.method, path: req.path };
  if (req.body !== undefined) msg.body = req.body;
  if (req.contentType !== undefined) msg.contentType = req.contentType;
  channel!.send(JSON.stringify(msg));
}

/**
 * Enqueue (or immediately dispatch) a tunnelled request and return a Promise
 * that resolves with the radio's response or rejects on timeout/disconnect.
 * GET/HEAD carry no body; mutating methods carry the serialized request body
 * and its content-type.
 */
function tunnel(
  path: string,
  method: string,
  body?: string,
  contentType?: string,
): Promise<TunnelResponse> {
  const id = nextId++;
  // The LAN Browser proxy reply is legitimately slow (whole-page inline on the
  // radio host); everything else is small chrome JSON on the default deadline.
  const timeoutMs = path.startsWith(LAN_PROXY_PREFIX)
    ? LAN_PROXY_TIMEOUT_MS
    : REQUEST_TIMEOUT_MS;
  return new Promise<TunnelResponse>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      // Drop any still-queued copy so a flush can't resurrect a dead id.
      const qi = queue.findIndex((q) => q.id === id);
      if (qi >= 0) queue.splice(qi, 1);
      reject(new Error('Remote API request timed out.'));
    }, timeoutMs);

    pending.set(id, { resolve, reject, timer });
    const req: QueuedRequest = { id, path, method, body, contentType };
    if (channel && channel.readyState === 'open') dispatch(req);
    else queue.push(req);
  });
}

/**
 * Extract a request's body (as text) and content-type from the fetch arguments,
 * handling the shapes the SPA actually uses: an `init.body` string (the common
 * `JSON.stringify(...)` case), a body carried on a `Request` object, and the
 * other BodyInit kinds (Blob/ArrayBuffer/URLSearchParams) via a throwaway
 * Response. GET/HEAD never call this.
 */
async function extractBody(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<{ body?: string; contentType?: string }> {
  const initHeaders = init?.headers ? new Headers(init.headers) : null;
  let contentType = initHeaders?.get('content-type') ?? undefined;

  const raw = init?.body;
  if (raw == null) {
    if (input instanceof Request) {
      if (!contentType) contentType = input.headers.get('content-type') ?? undefined;
      const text = await input.clone().text();
      return { body: text || undefined, contentType };
    }
    return { contentType };
  }
  if (typeof raw === 'string') return { body: raw, contentType };
  try {
    const text = await new Response(raw as BodyInit).text();
    return { body: text || undefined, contentType };
  } catch {
    return { contentType };
  }
}

/** Build a synthetic browser Response from the radio's tunnelled reply. */
function toResponse(r: TunnelResponse): Response {
  const headers = new Headers(r.headers ?? {});
  // HEAD/204/304 cannot carry a body per the Fetch spec — pass null.
  const bodyless = r.status === 204 || r.status === 304;
  return new Response(bodyless ? null : (r.body ?? ''), { status: r.status, headers });
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
 * Install the read-write `/api/*` fetch shim. Idempotent. Call once at remote-
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
    if (method === 'GET' || method === 'HEAD') {
      return tunnel(path, method).then(toResponse);
    }
    // Mutating request: extract its body, then tunnel to the radio, which
    // loopback-proxies it (the server enforces the write denylist + caps).
    return extractBody(input, init)
      .then(({ body, contentType }) => tunnel(path, method, body, contentType))
      .then(toResponse);
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
