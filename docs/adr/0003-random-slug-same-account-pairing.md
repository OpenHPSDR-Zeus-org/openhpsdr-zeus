# Random slugs, same-Google-account pairing

The broker assigns each Zeus install a random slug like `blue-falcon-742` rather than a callsign. Mobile binds to a desktop by signing into the broker with the same Google account; the QR encodes only the slug URL, with no embedded pairing token in v1.

## Considered options

- **Callsign slugs (`/go/ei6lf`)** — rejected. Callsign squatting is a real problem (callsigns get reassigned, and first-signup-wins lets users claim names they don't hold), and verification against FCC / Ofcom / equivalent is rabbit-hole work for v1. URLs should brand the tunnel, not assert amateur identity.
- **Vanity callsign as a second alias on top of a random slug** — deferred, not rejected. Easy to add later as one column, one endpoint, with both `/go/blue-falcon-742` and `/go/ei6lf` redirecting to the same tunnel.
- **One-time pairing token in the QR** — deferred. Would enable "share my shack with a friend without sharing my Google credentials," but adds first-run friction to the common case (the user's own phone). Implementable later as an additive `?pair=…` query parameter on the existing endpoint with no breaking change.

## Consequences

- A shoulder-surfer who photographs the QR cannot pair to the radio, because pairing requires an active session on the slug owner's Google account.
- "Lend my shack to a friend" is not supported in v1; both endpoints must use the same Google account.
- Callsign-shaped vanity URLs (a natural community-facing feature) are deferred but not architecturally blocked.
- The broker's `c7b8064` random slug generator and `a2a9b3d` `/settings/devices` page already implement the model this ADR locks in; no broker-side work changes from this decision.
