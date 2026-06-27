/**
 * /admin/* API: credential-based admin auth backed by the `zeus-admin` D1 store.
 *
 * Auth model (ADMIN_AUTH_DESIGN.md): QRZ proves callsign *ownership*; the admin
 * store proves *authorization*. Interactive callers POST /admin/login with a
 * password (plus QRZ headers) to mint a short-lived `session` token; agents hold
 * a non-expiring `agent` token. Protected routes require `Authorization: Bearer
 * zsa_…`. Failures return generic 401s — no oracle on which factor failed.
 */

import type { Env } from './types';
import { verifyQrzSessionCached } from './qrz';
import {
  hashPassword,
  verifyPassword,
  mintToken,
  hashToken,
  looksLikeToken,
  SESSION_TTL_MS,
  type PasswordHash,
} from './admin-auth';
import {
  getAdmin,
  listAdmins,
  insertAdmin,
  setAdminPassword,
  disableAdmin,
  insertToken,
  getTokenByHash,
  touchToken,
  revokeToken,
  listTokens,
  getTokenOwner,
  insertAudit,
  countAdmins,
  countEnabledAdmins,
} from './admin-db';

/** Resolved identity of an authenticated caller. */
export interface AuthCtx {
  callsign: string;
  tokenId: string;
  label: string;
}

/** Module-scoped guard so the (idempotent) bootstrap+migration runs at most once per isolate. */
let bootstrapped = false;

export async function handleAdmin(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
): Promise<Response> {
  const cors = corsHeaders(env);
  if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: cors });

  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, ''); // tolerate trailing slash

  // Seed bootstrap admin + migrate legacy env.ADMINS lazily on the first /admin
  // call (guarded so it never touches the signaling hot-path).
  await ensureBootstrap(env);

  try {
    // --- public: interactive login (mints a session token) -------------------
    if (path === '/admin/login' && request.method === 'POST') {
      return await handleLogin(request, env, ctx, cors);
    }

    // --- everything else is Bearer-token protected ---------------------------
    const auth = await authenticate(request, env);
    if (!auth) return json({ error: 'unauthorized' }, 401, cors);

    // tokens
    if (path === '/admin/tokens' && request.method === 'POST') {
      return await handleMintToken(request, env, auth, cors);
    }
    if (path === '/admin/tokens' && request.method === 'GET') {
      return json({ tokens: await listTokens(env.ADMIN_DB, auth.callsign) }, 200, cors);
    }
    const tokDel = /^\/admin\/tokens\/([^/]+)$/.exec(path);
    if (tokDel && request.method === 'DELETE') {
      return await handleRevokeToken(decodeURIComponent(tokDel[1]), env, auth, cors);
    }

    // admins
    if (path === '/admin/admins' && request.method === 'GET') {
      const admins = (await listAdmins(env.ADMIN_DB)).map((a) => ({
        callsign: a.callsign,
        disabled: a.disabled === 1,
        hasPassword: a.pw_hash != null,
        created_at: a.created_at,
        created_by: a.created_by,
      }));
      return json({ admins }, 200, cors);
    }
    if (path === '/admin/admins' && request.method === 'POST') {
      return await handleAddAdmin(request, env, auth, cors);
    }
    const setPw = /^\/admin\/admins\/([^/]+)\/password$/.exec(path);
    if (setPw && request.method === 'POST') {
      return await handleSetPassword(decodeURIComponent(setPw[1]), request, env, auth, cors);
    }
    const adminDel = /^\/admin\/admins\/([^/]+)$/.exec(path);
    if (adminDel && request.method === 'DELETE') {
      return await handleDisableAdmin(decodeURIComponent(adminDel[1]), env, auth, cors);
    }

    // presence
    if (path === '/admin/presence' && request.method === 'GET') {
      const id = env.PRESENCE.idFromName('global');
      const res = await env.PRESENCE.get(id).fetch('https://presence.internal/list');
      const body = await res.json<{ operators: unknown[] }>();
      return json(body, 200, cors);
    }

    // diagnostics-session request (STUB in P2; wired in P3)
    if (path === '/admin/request' && request.method === 'POST') {
      const target = norm(((await safeJson(request)).callsign as string) ?? '');
      if (!target) return json({ error: 'callsign required' }, 400, cors);
      await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'request', target });
      return json({ ok: true, status: 'stub', note: 'diagnostics session wired in P3' }, 202, cors);
    }

    return json({ error: 'not found' }, 404, cors);
  } catch {
    // Never leak internals; never reveal which step failed.
    return json({ error: 'error' }, 500, cors);
  }
}

// --- login -----------------------------------------------------------------

async function handleLogin(
  request: Request,
  env: Env,
  ctx: ExecutionContext,
  cors: Record<string, string>,
): Promise<Response> {
  const body = await safeJson(request);
  const password = typeof body.password === 'string' ? body.password : '';
  const callsign = norm(request.headers.get('X-QRZ-Callsign') ?? '');
  const sessionKey = request.headers.get('X-QRZ-Session') ?? '';

  // Generic failure for ALL of: missing inputs, bad QRZ, unknown/disabled admin,
  // wrong/absent password. No oracle on which factor failed.
  const fail = () => json({ error: 'unauthorized' }, 401, cors);

  if (!password || !callsign || !sessionKey) return fail();

  // Per-callsign throttle on top of the per-IP gate in index.ts, so a botnet
  // spread across many IPs still can't brute-force one admin's password (each
  // PBKDF2 verify is also real server CPU). Soft limit (429), not a hard lock.
  if (await loginRateLimited(env, callsign)) {
    return json({ error: 'rate limited' }, 429, cors);
  }

  // 1) QRZ: caller owns the callsign.
  const verify = (env.QRZ_VERIFY ?? 'on').toLowerCase() !== 'off';
  if (verify && !(await verifyQrzSessionCached(sessionKey, callsign, ctx))) return fail();

  // 2) Admin store: callsign is an enabled admin with a password set, and it matches.
  const admin = await getAdmin(env.ADMIN_DB, callsign);
  if (!admin || admin.disabled === 1 || !admin.pw_hash || !admin.pw_salt || admin.pw_iter == null) {
    return fail();
  }
  const stored: PasswordHash = { hash: admin.pw_hash, salt: admin.pw_salt, iter: admin.pw_iter };
  if (!(await verifyPassword(password, stored))) return fail();

  // Success: mint a short-lived session token. Expiry is persisted (expires_at)
  // so the data layer — not one app-level if — enforces it for every consumer.
  const minted = await mintToken();
  const id = crypto.randomUUID();
  const expiresAt = Date.now() + SESSION_TTL_MS;
  await insertToken(env.ADMIN_DB, {
    id,
    token_hash: minted.hash,
    callsign,
    label: 'session',
    expires_at: expiresAt,
  });
  await insertAudit(env.ADMIN_DB, { actor: callsign, action: 'login', target: callsign });
  // Secret in the body — never let an intermediary cache it.
  return json({ token: minted.token, callsign, expiresAt }, 200, cors, { 'Cache-Control': 'no-store' });
}

// --- token endpoints -------------------------------------------------------

async function handleMintToken(
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const body = await safeJson(request);
  const label = (typeof body.label === 'string' && body.label.trim() ? body.label.trim() : 'agent').slice(0, 64);
  const minted = await mintToken();
  const id = crypto.randomUUID();
  // Agent/API tokens do not expire (expires_at null); they are revocable instead.
  await insertToken(env.ADMIN_DB, { id, token_hash: minted.hash, callsign: auth.callsign, label, expires_at: null });
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'token.mint', target: id, detail: label });
  // token shown once — never cache the secret-bearing body.
  return json({ id, token: minted.token }, 201, cors, { 'Cache-Control': 'no-store' });
}

async function handleRevokeToken(
  id: string,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const owner = await getTokenOwner(env.ADMIN_DB, id);
  // Any admin may revoke any token; but if the id is unknown, respond the same
  // way (idempotent revoke) rather than 404 — no enumeration oracle.
  await revokeToken(env.ADMIN_DB, id);
  await insertAudit(env.ADMIN_DB, {
    actor: auth.callsign,
    action: 'token.revoke',
    target: id,
    detail: owner?.callsign ?? null,
  });
  return json({ ok: true }, 200, cors);
}

// --- admin endpoints -------------------------------------------------------

async function handleAddAdmin(
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const body = await safeJson(request);
  const callsign = norm((body.callsign as string) ?? '');
  if (!callsign) return json({ error: 'callsign required' }, 400, cors);

  let pw: PasswordHash | null = null;
  if (typeof body.password === 'string' && body.password) pw = await hashPassword(body.password);

  await insertAdmin(env.ADMIN_DB, {
    callsign,
    pw_hash: pw?.hash ?? null,
    pw_salt: pw?.salt ?? null,
    pw_iter: pw?.iter ?? null,
    created_by: auth.callsign,
  });
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'admin.add', target: callsign });
  return json({ ok: true, callsign }, 201, cors);
}

async function handleSetPassword(
  target: string,
  request: Request,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const callsign = norm(target);
  const body = await safeJson(request);
  const password = typeof body.password === 'string' ? body.password : '';
  if (!password) return json({ error: 'password required' }, 400, cors);

  const admin = await getAdmin(env.ADMIN_DB, callsign);
  if (!admin) return json({ error: 'not found' }, 404, cors);

  // An admin may set a password on themselves, or ONBOARD a password-less admin
  // (e.g. a legacy-seeded callsign), but may NOT reset a peer's EXISTING
  // password — that would be silent account takeover between equals.
  if (callsign !== auth.callsign && admin.pw_hash) {
    return json({ error: 'forbidden' }, 403, cors);
  }

  const pw = await hashPassword(password);
  await setAdminPassword(env.ADMIN_DB, callsign, pw.hash, pw.salt, pw.iter);
  await insertAudit(env.ADMIN_DB, { actor: auth.callsign, action: 'admin.setpw', target: callsign });
  return json({ ok: true }, 200, cors);
}

async function handleDisableAdmin(
  target: string,
  env: Env,
  auth: AuthCtx,
  cors: Record<string, string>,
): Promise<Response> {
  const callsign = norm(target);

  // Never let the system be locked out: refuse to disable the last enabled
  // admin. (This also stops a single rogue/compromised admin from disabling
  // everyone else AND themselves into an unrecoverable state.)
  const admin = await getAdmin(env.ADMIN_DB, callsign);
  if (admin && admin.disabled === 0 && (await countEnabledAdmins(env.ADMIN_DB)) <= 1) {
    return json({ error: 'cannot disable the last enabled admin' }, 409, cors);
  }

  await disableAdmin(env.ADMIN_DB, callsign);
  await insertAudit(env.ADMIN_DB, {
    actor: auth.callsign,
    action: 'admin.disable',
    target: callsign,
    detail: admin ? 'existed' : 'absent',
  });
  return json({ ok: true }, 200, cors);
}

// --- auth -------------------------------------------------------------------

/**
 * Resolve a `Authorization: Bearer zsa_…` header to an admin identity, or null.
 * Rejects (as null) on: missing/malformed token, unknown/revoked token, expired
 * session token, or disabled admin. Bumps last_used_at on success.
 *
 * Exported as {@link verifyAdminToken} so sibling routes (e.g. /admin/crashes,
 * which lives outside this module's path-dispatch) can gate on the SAME
 * credential-based admin auth without re-implementing the token-hash/expiry/
 * disabled-admin checks. Callers that do their own bootstrap-independent routing
 * should call {@link ensureAdminBootstrap} once first.
 */
export { authenticate as verifyAdminToken, ensureBootstrap as ensureAdminBootstrap };

async function authenticate(request: Request, env: Env): Promise<AuthCtx | null> {
  const header = request.headers.get('Authorization') ?? '';
  const m = /^Bearer\s+(.+)$/i.exec(header.trim());
  if (!m) return null;
  const token = m[1].trim();
  if (!looksLikeToken(token)) return null;

  const tokenHash = await hashToken(token);
  // getTokenByHash enforces revoked=0 AND not-expired at the data layer, so
  // expiry holds for every consumer of the shared store, not just this call.
  const row = await getTokenByHash(env.ADMIN_DB, tokenHash, Date.now());
  if (!row) return null;

  // The owning admin must still be enabled.
  const admin = await getAdmin(env.ADMIN_DB, row.callsign);
  if (!admin || admin.disabled === 1) return null;

  await touchToken(env.ADMIN_DB, row.id);
  return { callsign: row.callsign, tokenId: row.id, label: row.label ?? '' };
}

// --- bootstrap & migration --------------------------------------------------

/**
 * Idempotent: when `admins` is empty, seed the bootstrap admin (if the secrets
 * are set) and migrate legacy env.ADMINS callsigns to password-less rows. Guarded
 * by a module flag AND a count check so it costs at most one COUNT(*) per isolate
 * after the first call.
 */
async function ensureBootstrap(env: Env): Promise<void> {
  if (bootstrapped) return;
  bootstrapped = true;
  try {
    if ((await countAdmins(env.ADMIN_DB)) > 0) return; // already provisioned

    if (env.ADMIN_BOOTSTRAP_CALLSIGN && env.ADMIN_BOOTSTRAP_PASSWORD) {
      const callsign = norm(env.ADMIN_BOOTSTRAP_CALLSIGN);
      const pw = await hashPassword(env.ADMIN_BOOTSTRAP_PASSWORD);
      await insertAdmin(env.ADMIN_DB, {
        callsign,
        pw_hash: pw.hash,
        pw_salt: pw.salt,
        pw_iter: pw.iter,
        created_by: 'bootstrap',
      });
      await insertAudit(env.ADMIN_DB, { actor: 'bootstrap', action: 'admin.add', target: callsign });
    }

    // Seed legacy ADMINS list as password-less rows (never demote/remove).
    for (const callsign of (env.ADMINS ?? '').split(',').map(norm).filter(Boolean)) {
      await insertAdmin(env.ADMIN_DB, {
        callsign,
        pw_hash: null,
        pw_salt: null,
        pw_iter: null,
        created_by: 'bootstrap',
      });
    }
  } catch {
    // If bootstrap fails (e.g. schema not yet applied), allow a later retry.
    bootstrapped = false;
  }
}

// --- helpers ----------------------------------------------------------------

function norm(callsign: string): string {
  return callsign.trim().toUpperCase();
}

async function safeJson(request: Request): Promise<Record<string, unknown>> {
  try {
    const v = await request.json();
    return v && typeof v === 'object' ? (v as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

function json(
  body: unknown,
  status: number,
  cors: Record<string, string>,
  extra?: Record<string, string>,
): Response {
  return Response.json(body, { status, headers: extra ? { ...cors, ...extra } : cors });
}

/**
 * Per-callsign login throttle, reusing the shared per-IP RateLimiter DO keyed by
 * `login:<callsign>`. Soft-limits password attempts against a single admin even
 * when an attacker rotates source IPs. Fails OPEN (returns false) if the limiter
 * is somehow unreachable — availability of login beats a hard dependency.
 */
async function loginRateLimited(env: Env, callsign: string): Promise<boolean> {
  try {
    const id = env.RATE_DO.idFromName(`login:${callsign}`);
    const res = await env.RATE_DO.get(id).fetch('https://rl.internal/check');
    return res.status === 429;
  } catch {
    return false;
  }
}

/**
 * CORS for the dashboard's cross-origin /admin fetches. The admin surface fails
 * CLOSED: we name exactly the configured web-app origin and NEVER fall back to
 * '*' (unlike /turn, which is public). If WEB_APP_ORIGIN is unset, no
 * Allow-Origin header is emitted, so a browser cannot read /admin responses
 * cross-origin — a misconfigured deploy can't silently expose the admin API to
 * every site. Authorization + QRZ headers are allowed; no cookies, so no
 * Allow-Credentials.
 */
function corsHeaders(env: Env): Record<string, string> {
  const headers: Record<string, string> = {
    'Access-Control-Allow-Methods': 'GET, POST, DELETE, OPTIONS',
    'Access-Control-Allow-Headers': 'content-type, authorization, x-qrz-session, x-qrz-callsign',
    'Access-Control-Max-Age': '86400',
    Vary: 'Origin',
  };
  if (env.WEB_APP_ORIGIN) headers['Access-Control-Allow-Origin'] = env.WEB_APP_ORIGIN;
  return headers;
}
