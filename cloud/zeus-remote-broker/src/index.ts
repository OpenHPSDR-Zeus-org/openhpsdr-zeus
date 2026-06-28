import type { Env } from './types';
import { verifyQrzSessionCached } from './qrz';
import { mintIceServers } from './turn';
import { handleAdmin, verifyAdminToken, ensureAdminBootstrap } from './admin-api';
import { validateCrashBody } from './crash-validate';

export { SignalRoom } from './signal-room';
export { RateLimiter } from './rate-limiter';
export { PresenceRoom } from './presence';
export { CrashStore } from './crash-store';

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
      // /admin/crashes lives in index.ts (it reaches the CrashStore DO, not the
      // D1 admin store), but reuses the SAME credential-based admin auth as the
      // rest of /admin via verifyAdminToken. Everything else falls through to the
      // admin-api dispatcher.
      if (url.pathname === '/admin/crashes') {
        return handleAdminCrashes(request, env, ctx);
      }
      return handleAdmin(request, env, ctx);
    }

    // Operator presence: the radio's sidecar registers/heartbeats/drops here so a
    // maintainer can see it as "online" (read back via /admin/presence). Mirrors
    // the host-signaling QRZ gate in /signal: the verified callsign is the only
    // identity the broker trusts. Per-IP rate-limited like /turn and /signal.
    if (url.pathname === '/presence/register'
        || url.pathname === '/presence/heartbeat'
        || url.pathname === '/presence/drop') {
      if (request.method !== 'POST') return new Response('method not allowed', { status: 405 });
      if (await rateLimited(env, clientIp(request))) return new Response('rate limited', { status: 429 });
      const verified = await verifyOperator(request, env, ctx);
      if (!verified) return new Response('qrz auth required', { status: 401 });
      const presenceAction = url.pathname.slice('/presence/'.length); // register | heartbeat | drop
      const id = env.PRESENCE.idFromName('global');
      return env.PRESENCE.get(id).fetch(
        `https://presence.internal/${presenceAction}?callsign=${encodeURIComponent(verified)}`,
      );
    }

    // Crash auto-share upload: the sidecar POSTs a (already-redacted)
    // SupportCrashRecord here after an unexpected backend death, ONLY when the
    // operator pre-authorised auto-share. QRZ-gated as the operator; size-capped.
    if (url.pathname === '/crash') {
      if (request.method !== 'POST') return new Response('method not allowed', { status: 405 });
      if (await rateLimited(env, clientIp(request))) return new Response('rate limited', { status: 429 });
      const verified = await verifyOperator(request, env, ctx);
      if (!verified) return new Response('qrz auth required', { status: 401 });

      const body = await request.text();
      const verdict = validateCrashBody(body);
      if (!verdict.ok) return new Response(verdict.message, { status: verdict.status });

      const id = env.CRASH_STORE.idFromName('global');
      return env.CRASH_STORE.get(id).fetch(
        new Request(`https://crash.internal/put?callsign=${encodeURIComponent(verified)}`, {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body,
        }),
      );
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

      const roleParam = url.searchParams.get('role');
      const role = roleParam === 'host' ? 'host' : roleParam === 'support' ? 'support' : 'client';
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
      // role=support is NOT QRZ-gated here: the admin already proved authorisation
      // to /admin/request, which issued the single-use ticket carried on this WS.
      // The SignalRoom DO redeems + validates that ticket before joining the room,
      // so an unauthorised peer can never become a support connection. `callsign`
      // is the TARGET operator's room (supplied by the dashboard).

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

/**
 * QRZ-gate an operator-authenticated request (presence/crash), returning the
 * verified upper-cased callsign or null. Identical model to the host side of
 * /signal: when QRZ_VERIFY is on (default), the X-QRZ-Session must validate the
 * X-QRZ-Callsign via the cached QRZ lookup; with QRZ_VERIFY off (local dev) the
 * asserted callsign is trusted as-is. Fails closed on any missing field.
 */
async function verifyOperator(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
): Promise<string | null> {
  const callsign = (request.headers.get('X-QRZ-Callsign') ?? '').trim().toUpperCase();
  if (!callsign) return null;
  const verify = (env.QRZ_VERIFY ?? 'on').toLowerCase() !== 'off';
  if (!verify) return callsign;
  const sessionKey = request.headers.get('X-QRZ-Session') ?? '';
  if (!sessionKey) return null;
  if (!(await verifyQrzSessionCached(sessionKey, callsign, ctx))) return null;
  return callsign;
}

/**
 * GET /admin/crashes?callsign=<cs> — maintainer view of a consented operator's
 * auto-shared crash records. Admin-only: same Bearer-token credential auth as the
 * rest of /admin (reused via verifyAdminToken), NOT the operator's QRZ identity.
 * The operator opted in by enabling auto-share; the admin store proves the caller
 * is authorised to read it.
 */
async function handleAdminCrashes(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
): Promise<Response> {
  const cors = adminCorsHeaders(env);
  if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: cors });
  if (request.method !== 'GET') {
    return Response.json({ error: 'method not allowed' }, { status: 405, headers: cors });
  }

  // Match handleAdmin's contract: bootstrap the admin store, then Bearer-gate.
  await ensureAdminBootstrap(env);
  const auth = await verifyAdminToken(request, env);
  if (!auth) return Response.json({ error: 'unauthorized' }, { status: 401, headers: cors });

  const callsign = (new URL(request.url).searchParams.get('callsign') ?? '').trim().toUpperCase();
  if (!callsign) return Response.json({ error: 'callsign required' }, { status: 400, headers: cors });

  void ctx; // reserved (audit logging could go here as the admin-api routes do)
  const id = env.CRASH_STORE.idFromName('global');
  const res = await env.CRASH_STORE.get(id).fetch(
    `https://crash.internal/list?callsign=${encodeURIComponent(callsign)}`,
  );
  const body = await res.json<unknown>();
  return Response.json(body, { status: res.status, headers: cors });
}

/**
 * CORS for the dashboard's cross-origin /admin/crashes fetch. Mirrors
 * admin-api's fail-closed policy: name exactly WEB_APP_ORIGIN, never '*', and
 * omit the header entirely if it is unset so a misconfigured deploy can't expose
 * the admin surface to every site.
 */
function adminCorsHeaders(env: Env): Record<string, string> {
  const headers: Record<string, string> = {
    'Access-Control-Allow-Methods': 'GET, OPTIONS',
    'Access-Control-Allow-Headers': 'content-type, authorization, x-qrz-session, x-qrz-callsign',
    'Access-Control-Max-Age': '86400',
    Vary: 'Origin',
  };
  if (env.WEB_APP_ORIGIN) headers['Access-Control-Allow-Origin'] = env.WEB_APP_ORIGIN;
  return headers;
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
