# Cloudflare Tunnel — Zeus app side (sub-project B)

**Status:** Design 2026-05-24
**Pair:** Broker side is `brianbruff/openhpsdrzeus.com#1` (branch `feature/tunnel-broker`)
**Worktree:** `feature/tunnel`

## Problem

Once the broker at openhpsdrzeus.com is live, Zeus operators need three
things inside the app:

1. A clear UI on the Settings / Server tab to opt into a tunnel and
   punch one with one click.
2. A backend that talks to the broker (Google sign-in, fetch tunnel
   credentials), downloads + spawns `cloudflared`, and reports status.
3. Middleware on Zeus.Server that accepts the broker's signed redirect
   JWT, sets a `zeus_session` cookie, and gates traffic that arrives via
   the tunnel hostname (while leaving direct LAN access untouched).

This spec covers all three but is implemented in stages — Stage 1 is
visible UI only, Stages 2–4 wire the backend.

## Default value changes (operator-visible)

- The Server URL panel hint and placeholder change from
  `http://192.168.1.23:6060` → `https://192.168.1.23:6443`.
  Reason: HTTPS on `:6443` is the recommended path now that Zeus.Server
  binds both. The HTTP port stays available; we just stop advertising
  it. This is a one-place copy change and per repo conventions counts as
  a low-risk visual default, not a behavior change.

## Stages

### Stage 1 — Server tab UI (this PR)

- Change example URL hint + placeholder to `https://192.168.1.23:6443`.
- Add a "Cloudflare Tunnel" card below the existing Base URL controls,
  containing:
  - Opt-in checkbox: **"Enable Cloudflare tunnel"** (persists to
    localStorage `zeus.tunnel.optIn`).
  - **"Punch tunnel"** button — enabled only when opted in.
  - Status line. While the backend is not wired (Stages 2-4 below),
    clicking "Punch tunnel" surfaces a static notice pointing the
    operator at the broker landing page.
- No backend changes in this stage. The button calls a local
  function only; nothing is persisted server-side.

### Stage 2 — Google sign-in (backend)

- New ASP.NET controller `BrokerAuthController`:
  - `POST /api/broker/login/start` — opens the operator's system browser
    to `https://openhpsdrzeus.com/api/auth/google/start?client=zeus&return=<loopback>`.
  - `GET /<loopback>/oauth-done?token=...` — captures the bearer token
    from the broker's redirect, stores it in LiteDB
    (`broker_session.token`) with file permissions 600, replies with a
    short "you can close this window" HTML.
- Frontend learns sign-in state via `GET /api/broker/me` (proxied to
  broker `/api/auth/me` with the stored bearer).

### Stage 3 — Cloudflared lifecycle (backend)

- `TunnelService`:
  - On first punch: downloads platform-appropriate `cloudflared` binary
    from the official GitHub releases into `<app-data>/cloudflared/<version>/`.
    Verifies SHA-256 against the published checksum.
  - Calls broker `POST /api/tunnels/punch` with stored bearer.
  - Spawns `cloudflared tunnel run --token <T>` as a child process.
  - Captures stderr, exposes liveness over `GET /api/broker/tunnel/status`.
  - Heartbeat ticker (5 min) calls broker `POST /api/tunnels/heartbeat`.
  - Kills the subprocess on app shutdown.
  - Persists last-known PID to LiteDB; on startup, if the PID is alive
    and looks like `cloudflared`, adopt it; otherwise kill it.
- Frontend Server tab status panel polls
  `GET /api/broker/tunnel/status` and renders the live URL +
  "Stop tunnel" button.

### Stage 4 — Tunnel-side auth on Zeus.Server

- New `TunnelAuthMiddleware` in `Zeus.Server.Hosting`:
  - Detects whether the request arrived via the tunnel by checking
    `Host` against the slug persisted to LiteDB (`tunnel.slug`).
  - LAN / loopback hosts: skip entirely (preserves today's UX).
  - Tunnel host:
    - If `?t=<jwt>` is present: verify against broker JWKS
      (`<jwks_url>` returned by `/api/tunnels/punch`, cached 24h).
      Verify `iss=https://openhpsdrzeus.com`, `aud=<our slug>`, `exp`
      not passed, `jti` not in replay-cache (in-process LRU, 10-minute
      retention). On success: set `zeus_session` cookie (random session
      id keyed in LiteDB to `(sub, email, exp+8h)`), 302 to same URL
      with `?t` stripped.
    - Else if `zeus_session` cookie is present and valid: attach
      session, pass through.
    - Else: 401 with an HTML page pointing back to
      `https://openhpsdrzeus.com`.

`zeus_session` cookie spec:
- Name `zeus_session`
- Domain `<slug>.openhpsdrzeus.com` (scoped, no leading dot)
- Path `/`, `Secure; HttpOnly; SameSite=Lax`
- `Max-Age=28800` (8 h)

## Storage (LiteDB additions)

Single new collection `broker_state` with one document:
```jsonc
{
  "_id": 1,
  "optIn": true,
  "googleEmail": "x@y.z",
  "bearerToken": "…",         // opaque from broker
  "tunnelSlug": "rkkkxppmnz",
  "tunnelHostname": "rkkkxppmnz.openhpsdrzeus.com",
  "jwksUrl": "https://openhpsdrzeus.com/.well-known/jwks.json",
  "cloudflaredPath": "/Users/bek/Library/.../cloudflared",
  "lastPunchAt": 1748097600,
  "lastKnownPid": 12345
}
```

Cookie sessions (zeus_session) are in-process only for v1 — they don't
need to survive a Zeus restart (users re-enter via openhpsdrzeus.com).

## Open questions deferred

- Multi-user sessions on one Zeus instance (current model: any logged-in
  Google user can take over the tunnel since the bearer is single-token
  per box). Acceptable for v1 since each Zeus is a single-operator
  station.
- Cloudflared auto-update — for v1 we pin a known version and require
  reinstall to bump.
