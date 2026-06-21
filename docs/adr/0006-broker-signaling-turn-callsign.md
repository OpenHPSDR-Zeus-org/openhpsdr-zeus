# Broker = signaling + TURN credentials, reusing the chat relay's QRZ identity

**Supersedes [0002](./0002-broker-as-identity-redirector.md) and [0003](./0003-random-slug-same-account-pairing.md).**
`openhpsdrzeus.com` runs a Cloudflare Worker + Durable Object that (a) identifies the operator by
**QRZ-verified callsign** — the exact mechanism the chat relay already uses, *not* Google OAuth —
(b) relays WebRTC signaling (SDP offer/answer + ICE candidates) between the remote client and the
operator's `Zeus.Server` through a per-callsign Durable Object room, and (c) mints short-lived
Cloudflare TURN credentials. As in 0002, the broker never carries session bytes — it is a
control-plane SPOF for *first connect* only, not for ongoing media.

## What changes from 0002 / 0003

- **Identity is QRZ, not Google OAuth.** The broker reuses `cloud/zeuschat-relay`'s model: the
  client presents `X-QRZ-Session` + `X-QRZ-Callsign`, the Worker verifies against the QRZ XML API
  (cached ~300 s), and the verified callsign is locked into the Durable Object as the authoritative
  identity. See [0007](./0007-callsign-identity-and-verification.md). The Google OAuth / sealed
  cookie / D1 user-table stack from 0002 is dropped.
- The data plane is WebRTC ([0005](./0005-webrtc-data-plane.md)), so the broker's job is
  **signaling relay + TURN cred issuance**, not `/go/<slug>` redirect-to-tunnel.
- The remote address is the operator's **QRZ callsign**: `https://openhpsdrzeus.com/go/<callsign>`
  loads the Zeus web client wired to the signaling room for that callsign's online radio.

## Reuse from `cloud/zeuschat-relay`

The remote-access signaling room is architecturally identical to a chat room: two parties (radio
host + remote client) join a room keyed by callsign and exchange messages — SDP/ICE instead of
chat text. The broker is therefore a **sibling Worker / additional Durable Object type alongside
the chat relay**, reusing:

- `verifyQrzSession` + the 300 s positive-verdict cache (`src/index.ts`).
- The per-IP connection rate limiter (`src/rate-limiter.ts`).
- The Durable Object room pattern with the verified callsign locked into the connection attachment
  (`src/chat-room.ts`) — relabel "chat message" frames as "signaling" frames.

New work over the chat relay: a signaling frame schema (offer/answer/candidate/ready), pairing the
radio-host socket with the remote-client socket in the room, and proxying Cloudflare TURN's
`generate-ice-servers` (Worker holds the TURN key as a secret; TTL ≤ 48 h; client re-mints on long
sessions).

## Signaling design

- One **Durable Object per callsign room**, using **WebSocket Hibernation** so long-lived-but-idle
  SDR sessions incur no GB-s billing between the bursty signaling exchanges. (Free escape hatch if
  DOs ever need the paid plan: D1/KV poll-based rendezvous — see the design doc; SDP/ICE is a
  one-time exchange so slower setup adds no session latency.)
- `Zeus.Server`'s WebRTC peer only accepts an offer relayed through a room it authenticated into
  with its QRZ identity, so an unauthenticated remote cannot open a peer connection even though
  `/ws` historically had no auth.

## Consequences

- QRZ-header identity keeps the broker on Cloudflare's free tier; TURN egress is the only metered
  cost and the 1,000 GB/mo free tier likely covers the user base (most sessions go direct P2P).
- Identity is unified across chat and remote access — one QRZ login, one verified callsign.
- Broker outage stops *new* connections but not *established* media sessions (unchanged from 0002).
