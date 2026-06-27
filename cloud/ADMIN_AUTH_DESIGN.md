# Server-side admin auth (D1) — Phase 2 design

Part of the remote-diagnostics epic (beads `zeus-87ca`, issue `zeus-d0yr`). Replaces
the hardcoded `env.ADMINS` callsign list with a credential-based, server-side admin
store shared by the broker and the chat relay, so "admin" has one secure source of
truth and an automated agent can authenticate non-interactively.

## Threat model
- QRZ login proves a caller **owns** a callsign. It does NOT prove **authorization**.
  Today admin = "your verified callsign is on a static env list" — anyone who can be
  added to that list (or any future bug that lets a callsign be asserted) is admin.
- New rule: admin = **owns the callsign (QRZ)** AND **holds an admin credential**
  (password for interactive login, or a minted API token for non-interactive/agent
  use). Both verified server-side. Nothing about admin status is client-asserted.

## Store: one D1 database `zeus-admin`, bound to BOTH Workers
```sql
CREATE TABLE admins (
  callsign     TEXT PRIMARY KEY,        -- uppercased
  pw_hash      TEXT,                    -- PBKDF2 derived key, base64; NULL = no interactive login yet
  pw_salt      TEXT,                    -- base64; NULL iff pw_hash NULL
  pw_iter      INTEGER,                 -- PBKDF2 iterations used (for future cost bumps)
  disabled     INTEGER NOT NULL DEFAULT 0,
  created_at   INTEGER NOT NULL,        -- unix ms
  created_by   TEXT                     -- callsign of the admin who added them, or 'bootstrap'
);
CREATE TABLE admin_tokens (
  id           TEXT PRIMARY KEY,        -- random public id (safe to list)
  token_hash   TEXT NOT NULL,           -- SHA-256 hex of the secret token; the secret is shown once
  callsign     TEXT NOT NULL,
  label        TEXT,                    -- e.g. 'agent', 'dashboard'
  created_at   INTEGER NOT NULL,
  last_used_at INTEGER,
  revoked      INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE admin_audit (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  ts           INTEGER NOT NULL,
  actor        TEXT,                    -- who did it
  action       TEXT NOT NULL,           -- login | token.mint | token.revoke | admin.add | admin.disable | ...
  target       TEXT,                    -- subject callsign / token id
  detail       TEXT
);
CREATE INDEX admin_tokens_callsign ON admin_tokens(callsign);
```

## Crypto (WebCrypto only — runs in Workers AND Node, no WASM)
- **Password**: PBKDF2-HMAC-SHA256, 210_000 iterations, 16-byte random salt, 32-byte
  derived key. Store `pw_hash`/`pw_salt` base64 + `pw_iter`. Verify with a
  constant-time compare of derived bytes.
- **API token**: 32 random bytes → base64url, presented to the caller as
  `zsa_<base64url>` exactly once. Persist only `SHA-256(token)` hex. Verify by
  hashing the presented token and constant-time-comparing to stored hash; on match,
  bump `last_used_at`. Tokens do not expire but are revocable.
- **Interactive session token**: same token mechanism, label `session`, short TTL
  enforced by the caller storing `created_at` and the API rejecting old session
  tokens (TTL ~12h). (Agent tokens, label `agent`, never expire.)

## Auth surfaces (broker, all under `/admin/*`, JSON)
- `POST /admin/login` — body `{password}`, plus `X-QRZ-Session`+`X-QRZ-Callsign`
  headers. Verify QRZ (owns callsign) AND password for that callsign. On success
  mint a `session` token → `{token, callsign, expiresAt}`. Audit `login`.
- `POST /admin/tokens` — auth required. body `{label}`. Mint an API token (e.g.
  `agent`). Returns `{id, token}` (token shown once). Audit `token.mint`.
- `GET /admin/tokens` — auth. List token metadata (id/label/created/last_used/revoked) — never the secret.
- `DELETE /admin/tokens/:id` — auth. Revoke. Audit `token.revoke`.
- `GET /admin/admins` — auth. List admins (callsign/disabled/created).
- `POST /admin/admins` — auth. body `{callsign, password?}`. Add/seed an admin. Audit `admin.add`.
- `POST /admin/admins/:callsign/password` — auth (self, or any admin). Set/replace password. Audit `admin.setpw`.
- `DELETE /admin/admins/:callsign` — auth. Disable (never hard-delete; keep audit trail). Audit `admin.disable`.
- `GET /admin/presence` — auth. List online support-available users (callsigns) from the PRESENCE DO.
- `POST /admin/request` — auth. body `{callsign}`. Request a diagnostics session (STUB in P2; wired in P3).

**Auth on protected routes:** `Authorization: Bearer zsa_…`. Resolve token → admin
callsign; reject if revoked / unknown / (session) expired / admin disabled.
Constant-time everywhere; generic 401s (no oracle on which factor failed).

## Bootstrap & migration (idempotent, on first admin request / a setup call)
1. If `admins` is empty AND `ADMIN_BOOTSTRAP_CALLSIGN`+`ADMIN_BOOTSTRAP_PASSWORD`
   secrets are set, create that admin (created_by `bootstrap`).
2. Seed any callsigns in legacy `env.ADMINS` as **password-less** admin rows
   (chat-admin keeps working immediately; they set a password later to use the
   diagnostics API). One-time; never demotes/removes.

## Relay migration (chat-admin → same store)
- Add the same `ADMIN_DB` D1 binding to the relay.
- `ChatRoom` keeps its in-memory `this.admins: Set<string>` (no call-site churn) but
  repopulates it from `SELECT callsign FROM admins WHERE disabled=0` during
  `ensureLoaded()`, refreshing on a ~60s TTL so dashboard add/remove propagates
  without redeploy. Fall back to the `env.ADMINS` seed when D1 is empty/unreachable
  (nothing breaks pre-migration). All existing admin behavior (gold render, see-all,
  moderation) is unchanged — only the SOURCE of the set changes.

## Presence (broker)
- `PresenceRoom` single Durable Object: a map of `callsign → {since, lastSeen}` for
  operators whose sidecar has registered as support-available (operator L1 switch on).
  Endpoints (internal): `register`/`heartbeat`/`drop` (sidecar, wired in P3) and
  `list` (admin API). Entries expire if `lastSeen` is older than ~90s.

## Security hardening applied (post independent review)
- **Rate limiting**: all `/admin/*` is per-IP rate-limited (shared RateLimiter DO, mirrors `/turn`); `/admin/login` adds a second per-callsign throttle. Closes the unthrottled online password-guess / PBKDF2 CPU-exhaustion vector.
- **CORS fails closed**: the admin API never emits `Access-Control-Allow-Origin: *`. If `WEB_APP_ORIGIN` is unset, no allow-origin header is sent (browsers can't read `/admin` cross-origin). Only `/turn` (public) keeps the `*` fallback.
- **Token expiry at the data layer**: `admin_tokens.expires_at` column; `getTokenByHash` filters `revoked=0 AND (expires_at IS NULL OR expires_at>now)`. Session tokens get a 12h `expires_at`; agent tokens get NULL (revoke-only). Expiry no longer depends on a single app-level `if`.
- **No peer account takeover**: an admin may set their own password or onboard a *password-less* admin, but may NOT reset a peer's existing password.
- **No self-lockout**: disabling the last enabled admin is refused (409).
- **No hardcoded admins in source**: the relay's `env.ADMINS` default is now `''` (was `'N9WAR,KB2UKA'`). Set the seed as a wrangler var, never in code.
- **No secret caching**: `Cache-Control: no-store` on the login + token-mint responses (the only bodies carrying a secret). Token `label` capped at 64 chars.

## Out of scope here (later phases)
- Sidecar↔broker presence registration wiring and the actual diagnostics session
  relay (P3). `POST /admin/request` is a stub. The agent CLI is `zeus-azji` (P4b).

## Deploy (maintainer runs)
Order matters: provision + seed D1 BEFORE the relay starts trusting it (the relay
no longer has a hardcoded admin default — until D1 is seeded or `ADMINS` is set as
a var, there are simply no admins, which is the intended fail-closed posture).
```bash
cd cloud/zeus-remote-broker
wrangler d1 create zeus-admin            # copy database_id into BOTH wrangler.toml files
wrangler d1 migrations apply zeus-admin  # applies migrations/0001_admin.sql
# First admin (REQUIRED — there is no baked-in admin anymore):
wrangler secret put ADMIN_BOOTSTRAP_CALLSIGN
wrangler secret put ADMIN_BOOTSTRAP_PASSWORD
# OPTIONAL legacy chat-admins seeded as password-less rows — set as a VAR, not in
# source, on BOTH workers if you want them immediately (e.g. ADMINS="N9WAR,KB2UKA"):
#   wrangler deploy --var ADMINS:"N9WAR,KB2UKA"   (or add to [vars])
npm run deploy
# then in cloud/zeuschat-relay: add the same [[d1_databases]] binding + deploy
# First /admin call seeds bootstrap+legacy into D1. Then mint an agent token:
#   curl -sX POST https://remote.openhpsdrzeus.com/admin/login \
#     -H 'X-QRZ-Session: <key>' -H 'X-QRZ-Callsign: <CALL>' \
#     -d '{"password":"<bootstrap-pw>"}'                       # -> session token
#   curl -sX POST .../admin/tokens -H 'Authorization: Bearer <session>' \
#     -d '{"label":"agent"}'                                   # -> agent token (shown once)
```

## Agent-facing consumer: `tools/zeus-support-cli` (Phase 4b)

The non-interactive CLI in [`tools/zeus-support-cli`](../tools/zeus-support-cli)
is the reference *consumer* of this admin surface. It wraps the curl flow above
(`login` → `token mint`), lists consented online operators (`presence`), requests
a read-only diagnostics session (`request`), and runs the full
request → support-WS → WebRTC → `api`/`log` data-channel `pull`. It reads its
Bearer token from `ZEUS_ADMIN_TOKEN` and writes no secret to disk. It changes no
broker code — see that tool's README for usage and the agent-token flow.
