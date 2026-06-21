# zeus-remote-broker

Cloudflare Worker + Durable Object that brokers **remote access** to a Zeus radio
(ADR-0005/0006). It relays WebRTC signaling and mints TURN credentials — it never
sees media and never sees the session password.

## Why a broker

The browser cannot reach the operator's radio directly over the internet (home
NAT/CGNAT). So the radio keeps a persistent `host` WebSocket here, the browser
connects as a `client`, and the broker relays SDP/ICE between them until a direct
WebRTC peer-to-peer path is established (TURN-relayed only as fallback). Once the
peer connection is up, **media flows peer-to-peer and never touches this Worker.**

Security: the **host** side is QRZ-gated (only the operator who can prove the
callsign on QRZ may register as that radio). The **client** side is open — actual
radio access is gated end-to-end by the SPAKE2+ session password at the radio
(ADR-0008), so the broker is a pure, untrusted relay.

## Endpoints

- `GET /health` — liveness.
- `GET /go/<callsign>` — the operator's permanent address; 302-redirects to the
  web client (`WEB_APP_ORIGIN`) with `?remote=<callsign>`.
- `POST /turn` — mint short-lived Cloudflare Realtime TURN credentials
  (`{ iceServers }`); 503 if TURN is not configured (clients then use STUN only).
- `GET /signal?role=host` (WS, QRZ headers) — the radio registers for its callsign.
- `GET /signal?role=client&callsign=<X>` (WS) — a browser joins radio `<X>`'s room.

Signaling messages (JSON): client→host `offer`/`candidate`/`bye`; host→client
`answer`/`candidate`/`bye` (addressed by the broker-assigned `clientId`); `ping`/
`pong` keepalive auto-answered without waking the DO.

## Deploy

```bash
npm install
npm run typecheck
wrangler secret put TURN_KEY_ID        # from Cloudflare Realtime
wrangler secret put TURN_API_TOKEN
npm run deploy                          # creates remote.openhpsdrzeus.com
```

Requires the `openhpsdrzeus.com` zone on the deploying Cloudflare account. DOs are
SQLite-backed (free-plan compatible). For local dev: `wrangler dev --var QRZ_VERIFY:off`.

## Radio side (TODO — needs this broker live to test)

The radio needs a `RemoteBrokerClient` (.NET) that keeps a `host` WebSocket open
here, and on each relayed `offer` calls `RemoteWebRtcService.ConnectAsync(offer)`
to produce the `answer` (gated by the SPAKE2+ password) and relays it back with
the `clientId`. The server transport it drives already exists
(`Zeus.Server.Hosting/Remote/RemoteWebRtcSession.cs`); only the broker-client glue
remains, and it can only be verified against a live broker.
