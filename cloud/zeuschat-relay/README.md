# zeuschat-relay

Cloudflare Worker + Durable Object relay for **ZeusChat** — operator-to-operator
chat for Zeus.

Every operator runs their own local Zeus backend, so there is no shared server.
This relay is the central meeting point: each Zeus backend opens one outbound
WebSocket here, asserts its QRZ-verified callsign, publishes live presence
(frequency / mode / status), and exchanges chat messages. The relay fans
messages and roster updates out to all connected operators.

It runs on the project's own Cloudflare account.

## Design

- **Backend is the chat node, not the browser.** The Zeus backend holds the
  QRZ-verified callsign and the live VFO frequency, so the relay connection
  stays server-side and the identity token never reaches a browser. The browser
  ChatPanel talks to its own backend over the existing StreamingHub/REST.
- **WebSocket Hibernation.** The `ChatRoom` Durable Object uses the hibernation
  API (`acceptWebSocket` + `webSocket*` handlers), so it can be evicted from
  memory while connections stay open. Roster is reconstructed from per-socket
  attachments. Keepalive `ping`/`pong` is handled by `setWebSocketAutoResponse`
  and does not wake the DO.
- **One room in P0** (`lobby`). Band-derived rooms arrive in P3.

## Wire protocol

Text WebSocket frames, one JSON object per frame, discriminated by `t`. The
authoritative definition lives in [`src/protocol.ts`](src/protocol.ts); the C#
side mirrors it.

Backend → relay: `hello`, `presence`, `msg`, `ping`
Relay → backend: `welcome`, `roster`, `msg`, `error`, `pong`

Connect to `wss://<host>/chat`. Auth is at the HTTP upgrade via headers:

- `Authorization: Bearer <secret>` (or `?token=<secret>`) if `RELAY_SHARED_SECRET` is set.
- `X-QRZ-Session: <live QRZ session key>` and `X-QRZ-Callsign: <own callsign>`
  when `QRZ_VERIFY` is on (the default). The relay validates the session against
  the QRZ XML API and locks the verified callsign for the connection.

The first frame is `hello` (presence: grid/freq/mode/status). Its `callsign` is
only used in local dev where `QRZ_VERIFY=off`.

## Develop locally

```bash
cd cloud/zeuschat-relay
npm install
npm run typecheck      # tsc --noEmit
npm run dev            # wrangler dev — serves on http://localhost:8787
```

Smoke test the health route:

```bash
curl http://localhost:8787/health      # -> "zeuschat-relay ok"
```

Smoke test the socket (any WS client), e.g. with `websocat`:

```bash
websocat ws://localhost:8787/chat
> {"t":"hello","callsign":"W1ABC","freq":14074000,"mode":"FT8"}
< {"t":"welcome", ...}
< {"t":"roster", ...}
```

## Deploy (project owner)

Requires a Cloudflare account with Workers + Durable Objects.

```bash
npm install
wrangler login
npm run deploy
```

`new_sqlite_classes` is used for the DO namespaces, available on the free
Workers plan. No secret is required — access is gated by QRZ verification, so
shipped Zeus builds connect with no baked-in credential.

## Security notes

- **QRZ-login required (the access gate).** With `QRZ_VERIFY` on (default),
  every connection must present a live QRZ session key, which the relay
  validates against the QRZ XML API before admitting. Only operators logged
  into QRZ can use chat. Works for any QRZ login tier (subscription not
  required). Fails closed if QRZ is unreachable. Positive verdicts are cached
  ~5 min (edge cache) to spare the QRZ API on reconnects.
- **Rate limiting.** Per-IP connection limit (30 / 60s, via the `RateLimiter`
  Durable Object) guards the QRZ-verify path from connection-spam; per-connection
  message limit (6 / 5s, in `ChatRoom`) guards the room from message-spam.
- `RELAY_SHARED_SECRET` is an *optional* extra gate (Bearer / `?token=`). Unset
  in production — QRZ verification is the real gate, and requiring a secret
  would mean shipping a key in every Zeus build.
- **Callsign↔account binding is best-effort (known limitation).** The QRZ
  session proves a valid login; QRZ XML does not cleanly expose which callsign
  owns that session, so a determined *modified* client could present a valid
  session under a different callsign. The UI discloses that chat broadcasts the
  operator's callsign + frequency. Cryptographic ownership binding would require
  QRZ support that doesn't currently exist.
- Messages are capped at `MAX_MESSAGE_LEN` (2000 chars).
