# Broker is identity + redirector, not the data plane

`openhpsdrzeus.com` runs a Cloudflare Worker that holds Google OAuth identity, issues short-lived Ed25519-signed JWTs, and serves `GET /go/<slug>` as a 302 to the current Quick Tunnel URL reported by the desktop. The broker never sees tunnel bytes — only registration metadata and auth — which keeps the maintainer's hosting cost on Cloudflare's free tier indefinitely.

## Considered options

- **Broker provisions tunnels under the maintainer CF account** (original 16-commit `feature_tunnel_broker` design) — superseded by this ADR. Data-plane cost on the maintainer's account is the rejection driver. Roughly 3 commits of the existing broker (`e57294f` CF Tunnel API client, the DNS provisioning code paths inside `77be37b`) become dead code.
- **No broker; QR encodes the raw Quick Tunnel URL** — rejected. The URL changes on every desktop restart, so mobile would have to re-scan every session, breaking "scan once, work forever."
- **Broker also hosts a WebSocket relay** — rejected for the same data-plane-cost reason as the original design.

## Consequences

- The broker is a SPOF for *connection establishment* (first /go redirect of a new session) but not for *ongoing session data*. Once mobile holds a JWT and the current tunnel URL, broker outage does not interrupt an established session.
- Approximately 70% of the existing 16-commit broker work survives intact: Google OAuth, sealed cookies, session middleware, Ed25519 / JWKS signing, `/go` redirect, `/settings/devices`, daily GC cron, D1 schema.
- The "punch" endpoint reshapes from "create CF tunnel via API" to "register the current Quick Tunnel URL." `mine`, `heartbeat`, and `delete` mostly survive.
- The Zeus desktop validates JWTs against the broker's JWKS endpoint, fetched once at startup. This means desktop ↔ broker have a startup-time coupling but no per-request dependency.

## Glossary

- **Slug** — a random per-install identifier issued by the broker on first sign-in (e.g. `blue-falcon-742`). Stable for the lifetime of the Zeus install. See [0003](./0003-random-slug-same-account-pairing.md).
- **Broker** — the Cloudflare Worker hosted at `openhpsdrzeus.com` that handles Google OAuth, slug allocation, JWT minting, and the `/go/<slug>` redirect.
- **Quick Tunnel** — an anonymous Cloudflare Tunnel session created by `cloudflared` with no account, ephemeral URL on `trycloudflare.com`. See [0001](./0001-data-plane-cloudflare-quick-tunnels.md).
- **Device pairing** — the act of associating a mobile install with a Zeus desktop install via shared Google account on the broker.
- **Redirect JWT** — the short-lived Ed25519-signed token the broker mints into the `/go/<slug>` 302 redirect; validated by Zeus desktop against the broker's JWKS on each WebSocket connect.
