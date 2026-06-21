# Remote access via WebRTC — design & implementation plan

Goal: give an operator a stable address — `https://openhpsdrzeus.com/go/<callsign>` — that lets
them reach their own `Zeus.Server` from anywhere on the internet, with **extremely low latency**,
**near-zero per-user cost** to the project, through home NAT/CGNAT without router config, and with
minimal first-run friction.

Architecture decisions: [ADR-0005](../adr/0005-webrtc-data-plane.md) (WebRTC data plane),
[ADR-0006](../adr/0006-broker-signaling-turn-callsign.md) (broker = signaling + TURN + registry),
[ADR-0007](../adr/0007-callsign-identity-and-verification.md) (callsign identity).

## Non-negotiable constraints (governs every design choice below)

The feature must be all three of these simultaneously; any implementation choice that trades one
away is wrong:

1. **Hyper-performant / low-latency.** Direct P2P UDP is the default path (physical RTT only, no
   edge hop). Media never rides a reliable/ordered channel: audio = Opus media track (10–20 ms
   frames, minimal jitter buffer) with an optional raw-PCM-over-unreliable-DataChannel "hyper" mode
   to shed Opus's ~26 ms codec delay (affordable at ~96 KB/s); spectrum/IQ/display = unreliable +
   unordered DataChannel, drop-stale, reusing the existing drop-oldest backpressure; only VFO/control
   uses a reliable channel, kept off the hot media path. The primary latency lever is maximizing the
   direct-connect rate so TURN (+20–80 ms) stays the exception.

2. **Scalable.** Cost must not grow with the user count. The data plane is peer-to-peer, so peers
   carry their own bytes; Cloudflare only ever pays for the relayed minority. Signaling/identity are
   tiny and constant-per-session, not per-byte.

3. **Free.** Target $0/month at hobby scale. Cost edges and their free escape hatches:
   - **Durable Objects**: use the free SQLite-DO tier; if it ever requires the paid plan, fall back
     to **D1/KV poll-based signaling rendezvous** — SDP/ICE is exchanged once at connect, so a 1–2 s
     slower *setup* adds zero *session* latency and keeps everything on the pure free tier.
   - **TURN egress**: bounded by Cloudflare's 1,000 GB/mo free pool (~9,000 operator-hours/mo, since
     only ~20% of sessions relay). Overflow is $0.05/GB (pennies) or a community coturn node.
   - Workers (100k req/day) and D1 (5 GB / 5M reads-day) dwarf the registry + signaling load.

## One-line architecture

Browser/webview ↔ **WebRTC** (Opus media track = audio; unreliable+unordered DataChannel =
spectrum/IQ/display; reliable+ordered DataChannel = VFO/control) ↔ **SIPSorcery** peer in the
.NET 10 `Zeus.Server`. Signaling + **QRZ-verified-callsign identity** (reusing `zeuschat-relay`) +
short-lived TURN creds via a **Cloudflare Worker + hibernating Durable Object** on
`openhpsdrzeus.com`. **Cloudflare Realtime TURN** (anycast, $0.05/GB after 1,000 GB/mo free) is the
relay fallback for ~15–30% of sessions.

## How the domain produces the remote link

`openhpsdrzeus.com` does three jobs, all on Cloudflare's free tier:

1. **Serves the web client.** A `zeus-web` build is hosted on **Cloudflare Pages** at the domain.
   When a remote user opens the link on their phone, the React app loads from Cloudflare's global
   CDN — *not* from the operator's home server (which isn't reachable until WebRTC connects). Free,
   unlimited static requests, fast everywhere.
2. **The link.** `https://openhpsdrzeus.com/go/<callsign>` boots that web client into "remote mode"
   pointed at the signaling room for that callsign's online radio.
   - **Path-based (`/go/ei6lf`) is recommended** — it handles *any* callsign string safely, including
     portable (`EI6LF/P`) and contest/special calls that contain `/`.
   - Subdomain (`ei6lf.openhpsdrzeus.com`, wildcard DNS → Worker, also free) reads more personal but
     breaks on `/` in portable calls and needs sanitization. Aesthetic choice; path is the safe default.
3. **Signaling + TURN creds.** The Worker API on the same domain (ADR-0006).

**The operator never copies a URL.** Once QRZ-signed-in (the same login chat already uses) and remote
access is toggled on, Zeus *derives and displays* the permanent link + a QR from the verified callsign
("Your radio: `openhpsdrzeus.com/go/EI6LF`"). It is stable forever and unspoofable — nobody else can
QRZ-authenticate as that callsign. Availability: the home `Zeus.Server` registers "callsign online" in
the room when remote access is on; `/go/<callsign>` finds the live host, or shows a clean
"radio offline" page.

## Current transport (what we are migrating) — verified

| Path | Today | Rate / size | Target WebRTC channel |
|---|---|---|---|
| RX audio (`AudioFrame` 0x02) | binary `/ws` | 48 Hz × ~2 KB ≈ **96 KB/s** | **Opus media track** |
| Mic uplink (`MicPcm` 0x20) | binary `/ws` | 48 Hz × 3841 B | Opus media track (reverse) |
| Display/panadapter (`DisplayFrame` 0x10) | binary `/ws` | ~60 Hz, 100–400 B | **DataChannel, unreliable+unordered** |
| Meters (0x14–0x19), MOX/alerts/CW/chat | binary `/ws` | ≤10 Hz, tiny | **DataChannel, reliable+ordered** |
| Stream requests (0x21/0x22) | binary `/ws` uplink | on toggle | reliable DataChannel (reverse) |
| VFO / mode / drive control | REST `/api/*` | request/response | stays REST (low volume), or reliable DataChannel |

Source of truth: `Zeus.Server.Hosting/StreamingHub.cs`, `ZeusEndpoints.cs` (`/ws` map),
`ZeusHost.cs` (`UseWebSockets`, 20 s keepalive), `Zeus.Contracts/WireFormat.cs`,
`zeus-web/src/realtime/ws-client.ts`, `zeus-web/src/serverUrl.ts`. **No auth exists on `/ws` or
`/api` today** — remote access adds it (ADR-0006).

Peak total ≈ 100 KB/s — comfortably inside a single WebRTC peer; no media SFU needed (1:1 peering).

## Build status (PR #811)

| Area | State | Verified by |
|---|---|---|
| WebRTC works on our stack (Phase 0) | ✅ done | 2-peer DataChannel echo test |
| Deny-by-default LOCKED session gate | ✅ done | 5 tests |
| SPAKE2+ verifier (server, C#) | ✅ done | RFC 9383 vectors, byte-exact |
| SPAKE2+ prover (browser, TS) | ✅ done | RFC 9383 vectors + cross-lang Argon2 agreement |
| Argon2id registration + password store + UI | ✅ done | killer end-to-end + store tests |
| Phase 1 server transport (auth over DataChannel, gated egress) | ✅ done | 2-peer transport test: correct→frame flows, wrong→nothing |
| Browser connect flow (`connect.ts`) | ✅ written | `tsc`; live WebRTC needs a browser bench |
| Scan-to-remote QR | ✅ done | 7 tests |
| Phase 3 broker (Worker + DO, `cloud/zeus-remote-broker`) | ✅ written | `tsc` + workers-types; needs CF deploy |
| Radio-side broker client (`RemoteBrokerClient`) | ✅ written | offer→answer bridge tested; WS-to-broker needs live broker |

**Code-complete. Remaining to go live needs hardware / a deploy (not headlessly verifiable):**
- Deploy `cloud/zeus-remote-broker` to the `openhpsdrzeus.com` Cloudflare account
  (`wrangler deploy` + TURN secrets); creates `remote.openhpsdrzeus.com`.
- StreamingHub → frames-channel bridge (real audio/IQ onto the unlocked session) + the browser
  media path (frame decode + WebAudio). The transport exposes `TrySendFrame` and the browser
  `connect.ts` surfaces `onFrame`; wiring them to the live DSP pipeline + measuring real RTT is the
  one piece deliberately left for a radio bench (it touches the real-time `/ws` hot path).

## Implementation phases

**Phase 0 — Spike (de-risk the .NET↔browser WebRTC path).**
Add SIPSorcery to `Zeus.Server`. Prove a loopback on LAN with *manual copy-paste signaling* (no
broker yet): one Opus track + one binary DataChannel between `zeus-web` and `Zeus.Server`. Validate
on Windows + linux-arm64/Pi. Confirm managed Opus encode (no native FFmpeg). **Exit criteria:** audio
plays and a DataChannel echoes a `DisplayFrame` end to end.

**Phase 1 — Server transport abstraction (deny-by-default from line one).**
Introduce an `IRemoteTransport` seam so the existing `WireFormat` frames egress over either the
current `/ws` WebSocket *or* WebRTC channels, with the per-client bounded-queue / drop-oldest
backpressure (currently `MaxBacklogPerClient=4`) preserved on the WebRTC side. Audio routes to the
Opus track; display/IQ to the unreliable DataChannel; everything else to the reliable DataChannel.
No frame-format changes. **The WebRTC transport is constructed in the LOCKED state** (ADR-0008 hard
invariant): it holds an `IRemoteAuthGate` and egresses/accepts nothing radio-related until the gate
reports UNLOCKED. The gate's real password verifier arrives in Phase 4; until then it is a
deny-everything stub, so "nothing without the password" is structural, never retrofitted.

**Phase 2 — Frontend WebRTC client.**
Mirror `ws-client.ts` with an `rtc-client.ts` that establishes `RTCPeerConnection`, reuses every
existing frame decoder, and routes the Opus track into WebAudio. `serverUrl.ts` gains a
`remote` mode alongside the existing same-origin / LAN-IP modes.

**Phase 3 — Broker: signaling + QRZ identity + TURN.**
Cloudflare Worker + Durable Object (WebSocket Hibernation), built as a **sibling of the existing
`cloud/zeuschat-relay`** — reuse its QRZ verification (`verifyQrzSession` + 300 s cache), per-IP
rate limiter, and DO-room-with-locked-callsign pattern (ADR-0006/0007). Identity is the
QRZ-verified callsign via `X-QRZ-Session`/`X-QRZ-Callsign` headers — **no Google OAuth, no claim
flow, no user table**. New over chat: a signaling frame schema (offer/answer/candidate/ready),
host↔client pairing within the room, and proxying Cloudflare TURN `generate-ice-servers`.
`GET /go/<callsign>` serves the web client wired to that callsign's room.

**Phase 4 — Auth & security.**
Two gates: (1) **identity** — the remote signaling room is reached via the QRZ-callsign address
(ADR-0006/0007); (2) **access control** — a **session password** the operator sets in the Server
menu (ADR-0008), verified **end-to-end at `Zeus.Server`** over the DTLS-encrypted DataChannel
(PAKE/SPAKE2 target so the broker never learns it), Argon2id-hashed in prefs, with per-attempt
rate-limit + backoff. **Remote access cannot be enabled without a password set** (transmitter
safety). DTLS-SRTP encrypts media. LAN/localhost keeps the historical no-auth trusted path; auth is
required only on the remote path. Remote password entry uses the in-app PromptDialog, never
`window.prompt`.

The Server menu (the existing Server tab / `ServerUrlPanel.tsx` area that the opt-in card already
lives in) gains: a **session-password field** (set/change/clear), the **remote-access toggle**
(disabled until a password exists), and the auto-derived **`/go/<callsign>` link + QR** shown once
QRZ-signed-in.

**Phase 5 — Packaging.**
SIPSorcery is managed → no native bundling; **drop the `cloudflared` bundling from ADR-0004** on the
primary path. Keep installer size flat.

**Phase 6 — Resilience & polish.**
ICE restart / reconnect, TURN cred refresh before the 48 h TTL, QR for `/go/<callsign>`, devices
page, "radio offline" page, and (optional, additive) a Quick Tunnel last-resort transport for
environments where even TURN-over-443 is blocked.

## Things to verify before committing code

1. SIPSorcery managed-Opus path on linux-arm64 / Raspberry Pi (avoid native FFmpeg).
2. SIPSorcery BSD-3 *ethical-use clause* — license review.
3. Cloudflare blessing for minting TURN creds from a Worker (technically fine; docs say "a back-end service").
4. Real TURN-fallback rate for home-host topology (CGNAT-on-both-ends may exceed the ~20% average).
5. WebRTC binary DataChannel throughput for 60 Hz display under the existing drop-oldest backpressure.
6. Whether the broker should add **per-callsign** rate limits (chat is per-IP only) given it gates
   radio access, not chat.

## Resolved decisions

- **Identity / verification**: QRZ-verified callsign, reusing the chat relay's mechanism
  (ADR-0007). No Google OAuth, no self-attestation, no registry table. This unblocks Phase 3.
- **URL identity**: callsign vanity `/go/<callsign>` (ADR-0007).
- **Access control**: operator-set **session password**, verified end-to-end at `Zeus.Server`,
  mandatory before remote access can be enabled (ADR-0008). Decouples access from identity →
  guest/"lend my shack" access without sharing QRZ creds.
- **Data plane**: WebRTC P2P + Cloudflare Realtime TURN fallback (ADR-0005).
