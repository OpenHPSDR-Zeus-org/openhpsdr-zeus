import type { Env } from './types';
import { verifyQrzSessionCached } from './qrz';
import { mintIceServers } from './turn';
import { handleAdmin } from './admin-api';

export { SignalRoom } from './signal-room';
export { RateLimiter } from './rate-limiter';
export { PresenceRoom } from './presence';

export default {
  async fetch(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === '/' || url.pathname === '/health') {
      return new Response('zeus-remote-broker ok', {
        status: 200,
        headers: { 'content-type': 'text/plain' },
      });
    }

    // /go/<callsign> — the operator's permanent remote address. Redirects to the
    // web client (Cloudflare Pages), which then signals back here as a client.
    const go = /^\/go\/([^/]+)$/.exec(url.pathname);
    if (go) {
      const callsign = decodeURIComponent(go[1]).toUpperCase();
      const origin = env.WEB_APP_ORIGIN ?? 'https://openhpsdrzeus.com';
      return Response.redirect(`${origin}/?remote=${encodeURIComponent(callsign)}`, 302);
    }

    // Credential-based admin API (login, token mint/revoke, admin CRUD,
    // presence). Self-contained: handles its own CORS, Bearer auth, and the
    // idempotent bootstrap/migration on first call. See admin-api.ts.
    //
    // Per-IP rate-limited (mirrors /turn and /signal): the unauthenticated
    // /admin/login surface must not be an unthrottled online password-guess /
    // PBKDF2 CPU-exhaustion vector. OPTIONS preflights pass through so the
    // dashboard's CORS check is never throttled. handleLogin adds a second,
    // per-callsign throttle.
    if (url.pathname === '/admin' || url.pathname.startsWith('/admin/')) {
      if (request.method !== 'OPTIONS' && (await rateLimited(env, clientIp(request)))) {
        return new Response('rate limited', { status: 429, headers: corsHeaders(env) });
      }
      return handleAdmin(request, env, ctx);
    }

    // Mint short-lived TURN credentials (clients call this before connecting).
    // The web client fetches this cross-origin (app.openhpsdrzeus.com → broker),
    // so EVERY response carries CORS headers — including the 503 fallback, so the
    // browser can read it and fall back to STUN instead of throwing a CORS error.
    if (url.pathname === '/turn') {
      const cors = corsHeaders(env);
      if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: cors });
      if (request.method !== 'POST') return new Response('method not allowed', { status: 405, headers: cors });
      if (await rateLimited(env, clientIp(request))) return new Response('rate limited', { status: 429, headers: cors });
      try {
        return Response.json(await mintIceServers(env), { headers: cors });
      } catch {
        return new Response('turn unavailable', { status: 503, headers: cors });
      }
    }

    // WebSocket signaling. Host (radio) is QRZ-gated; client (browser) is open
    // (the radio's session password is the real gate — the broker only relays).
    if (url.pathname === '/signal') {
      if (await rateLimited(env, clientIp(request))) return new Response('rate limited', { status: 429 });
      if (request.headers.get('Upgrade') !== 'websocket') {
        return new Response('expected websocket upgrade', { status: 426 });
      }

      const role = url.searchParams.get('role') === 'host' ? 'host' : 'client';
      let callsign = (url.searchParams.get('callsign') ?? '').trim().toUpperCase();
      const headers = new Headers(request.headers);

      if (role === 'host') {
        const verify = (env.QRZ_VERIFY ?? 'on').toLowerCase() !== 'off';
        const verified = (request.headers.get('X-QRZ-Callsign') ?? '').trim().toUpperCase();
        if (verify) {
          const sessionKey = request.headers.get('X-QRZ-Session') ?? '';
          if (!sessionKey || !verified) return new Response('qrz auth required', { status: 401 });
          if (!(await verifyQrzSessionCached(sessionKey, verified, ctx))) {
            return new Response('qrz session invalid or not logged in', { status: 403 });
          }
        }
        callsign = verified || callsign;
        headers.set('X-Operator-Callsign', callsign);
      }

      if (!callsign) return new Response('callsign required', { status: 400 });

      const id = env.SIGNAL_ROOM.idFromName(callsign);
      return env.SIGNAL_ROOM.get(id).fetch(new Request(request, { headers }));
    }

    return new Response('not found', { status: 404 });
  },
} satisfies ExportedHandler<Env>;

function clientIp(request: Request): string {
  return request.headers.get('cf-connecting-ip') ?? 'unknown';
}

// CORS for the browser web client's cross-origin /turn fetch. Allow exactly the
// configured web-app origin (the SPA on Cloudflare Pages); fall back to '*' only
// if WEB_APP_ORIGIN is unset. /turn returns short-lived TURN creds, no cookies,
// so no Allow-Credentials.
function corsHeaders(env: Env): Record<string, string> {
  return {
    'Access-Control-Allow-Origin': env.WEB_APP_ORIGIN ?? '*',
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Access-Control-Allow-Headers': 'content-type',
    'Access-Control-Max-Age': '86400',
    Vary: 'Origin',
  };
}

async function rateLimited(env: Env, ip: string): Promise<boolean> {
  const limiter = env.RATE_DO.get(env.RATE_DO.idFromName(ip));
  const res = await limiter.fetch('https://rl.internal/check');
  return res.status === 429;
}
