import type { Env } from './types';
import { DEFAULT_ROOM } from './protocol';

export { ChatRoom } from './chat-room';
export { RateLimiter } from './rate-limiter';

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === '/' || url.pathname === '/health') {
      return new Response('zeuschat-relay ok', {
        status: 200,
        headers: { 'content-type': 'text/plain' },
      });
    }

    if (url.pathname === '/chat') {
      const ip = request.headers.get('cf-connecting-ip') ?? 'unknown';

      // 1. Per-IP connection rate limit (protects the QRZ-verify path from
      //    connection-spam). Deterministic per-IP Durable Object counter.
      const limiter = env.RATE_DO.get(env.RATE_DO.idFromName(ip));
      const rlRes = await limiter.fetch('https://rl.internal/check');
      if (rlRes.status === 429) {
        return new Response('rate limited', { status: 429 });
      }

      // 2. Optional shared-secret gate (defense-in-depth; production relies on
      //    QRZ verification, so this is normally unset).
      if (env.RELAY_SHARED_SECRET) {
        const header = request.headers.get('Authorization');
        const bearer = header?.startsWith('Bearer ') ? header.slice(7) : undefined;
        const token = bearer ?? url.searchParams.get('token') ?? undefined;
        if (token !== env.RELAY_SHARED_SECRET) {
          return new Response('unauthorized', { status: 401 });
        }
      }

      // 3. QRZ-login gate. The backend presents its live QRZ session key + own
      //    callsign as headers; we validate the session against QRZ before
      //    admitting. Disable with QRZ_VERIFY="off" for local dev only.
      const verify = (env.QRZ_VERIFY ?? 'on').toLowerCase() !== 'off';
      const callsign = (request.headers.get('X-QRZ-Callsign') ?? '').trim().toUpperCase();
      if (verify) {
        const sessionKey = request.headers.get('X-QRZ-Session') ?? '';
        if (!sessionKey || !callsign) {
          return new Response('qrz auth required', { status: 401 });
        }
        const ok = await verifyQrzSessionCached(sessionKey, callsign, ctx);
        if (!ok) {
          return new Response('qrz session invalid or not logged in', { status: 403 });
        }
      }

      // 4. WebSocket upgrade required.
      if (request.headers.get('Upgrade') !== 'websocket') {
        return new Response('expected websocket upgrade', { status: 426 });
      }

      // 5. Route to the room DO, forwarding the (verified) callsign. P0: one room.
      const headers = new Headers(request.headers);
      if (callsign) headers.set('X-Operator-Callsign', callsign);
      const forwarded = new Request(request, { headers });

      const id = env.CHAT_ROOM.idFromName(DEFAULT_ROOM);
      const stub = env.CHAT_ROOM.get(id);
      return stub.fetch(forwarded);
    }

    return new Response('not found', { status: 404 });
  },
} satisfies ExportedHandler<Env>;

/** TTL (seconds) for cached positive QRZ-session verdicts. */
const QRZ_VERIFY_TTL = 300;

/**
 * QRZ verification with a short-lived positive-result cache (edge Cache API).
 * Legit reconnects within the TTL don't re-hit QRZ; only valid verdicts are
 * cached (never negative, so a freshly-logged-in operator is never locked out).
 */
async function verifyQrzSessionCached(
  sessionKey: string,
  callsign: string,
  ctx: ExecutionContext,
): Promise<boolean> {
  const digest = await sha256Hex(`${sessionKey}|${callsign}`);
  const cacheKey = new Request(`https://qrz-verify.zeuschat.internal/${digest}`);
  const cache = caches.default;

  const hit = await cache.match(cacheKey);
  if (hit) return (await hit.text()) === '1';

  const ok = await verifyQrzSession(sessionKey, callsign);
  if (ok) {
    ctx.waitUntil(
      cache.put(
        cacheKey,
        new Response('1', { headers: { 'Cache-Control': `max-age=${QRZ_VERIFY_TTL}` } }),
      ),
    );
  }
  return ok;
}

/**
 * Validates a QRZ XML session key by performing one lookup of the operator's
 * own callsign. Per the QRZ XML spec, a live session returns a <Key> element;
 * an expired/invalid session omits <Key> and returns <Error>Session Timeout</Error>.
 * Non-subscribers still get a <Key>, so this works for any QRZ login tier.
 * Fails closed on any QRZ/network error.
 */
async function verifyQrzSession(sessionKey: string, callsign: string): Promise<boolean> {
  const u =
    'https://xmldata.qrz.com/xml/current/' +
    `?s=${encodeURIComponent(sessionKey)}` +
    `;callsign=${encodeURIComponent(callsign)}` +
    ';agent=zeuschat';
  try {
    const res = await fetch(u, { cf: { cacheTtl: 0 } });
    if (!res.ok) return false;
    const xml = await res.text();
    const hasKey = /<Key>\s*[^<\s][^<]*<\/Key>/i.test(xml);
    const errText = /<Error>([^<]*)<\/Error>/i.exec(xml)?.[1] ?? '';
    const badSession = /session timeout|invalid session|session expired|not logged/i.test(errText);
    return hasKey && !badSession;
  } catch {
    return false; // fail closed: no proof of login => no access
  }
}

/** Hex SHA-256 of a string (for opaque cache keys; never logs the raw key). */
async function sha256Hex(input: string): Promise<string> {
  const data = new TextEncoder().encode(input);
  const buf = await crypto.subtle.digest('SHA-256', data);
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
