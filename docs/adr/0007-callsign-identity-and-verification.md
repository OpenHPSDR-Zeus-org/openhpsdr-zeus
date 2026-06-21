# Callsign identity via QRZ — verified, no separate auth

**Supersedes the random-slug decision in [0003](./0003-random-slug-same-account-pairing.md).**
The remote address brands the operator's callsign: `https://openhpsdrzeus.com/go/<callsign>`.
The callsign is **the operator's QRZ-verified home callsign**, obtained exactly the way the chat
feature already obtains it — there is no separate sign-up, no Google OAuth, and no self-attested
"my callsign" field to squat.

## Why QRZ (reversing 0003, and dropping Google OAuth)

0003 chose random slugs to dodge callsign squatting and license verification. Zeus's chat feature
already solved that problem: the backend authenticates to the operator's QRZ account, and QRZ
returns the account holder's own callsign. Logging into QRZ as the callsign holder *is* proof of
ownership, so squatting is structurally impossible — you cannot claim a callsign you cannot sign
into QRZ as. Reusing this gives the remote-access feature a verified identity for free and unifies
identity across chat and remote access.

## How it works (mirrors the chat path verbatim)

- `QrzService` acquires a QRZ session key from the QRZ XML API and looks up the operator's home
  record; `_home.Callsign` is the verified, uppercased callsign
  (`Zeus.Server.Hosting/QrzService.cs`, `GetChatIdentityAsync`).
- `Zeus.Server` connects to the broker with the same headers chat uses:
  `X-QRZ-Session: <key>`, `X-QRZ-Callsign: <callsign>` (see `ChatService.cs`).
- The broker Worker validates the session+callsign against QRZ (cached ~300 s, fail-safe on
  negative) exactly like `cloud/zeuschat-relay/src/index.ts` (`verifyQrzSession`), then locks the
  verified callsign into the signaling Durable Object as the authoritative identity — no client
  can override it (`cloud/zeuschat-relay/src/chat-room.ts`).

## Consequences

- **No squatting / verification rabbit-hole** — QRZ is the registry. A callsign resolves to whoever
  can authenticate to QRZ as that callsign and is currently online.
- **No Google OAuth, no D1 user table, no claim flow** — the old broker's identity stack
  ([0002](./0002-broker-as-identity-redirector.md)) collapses to the chat relay's QRZ-header model.
- QRZ session keys are ephemeral (~1 h); `Zeus.Server` already refreshes them on demand, so the
  broker re-verifies on reconnect with no new machinery.
- Edge cases inherited from chat (no QRZ subscription → cannot use the feature; QRZ outage → cannot
  establish *new* sessions) are accepted, identical to chat's posture.
- A claimed-but-offline callsign resolves to a "radio offline" page, not an error.
- Caveat to carry forward: chat rate-limits **per-IP, not per-callsign**; the remote-access broker
  should decide whether per-callsign limits are warranted given it gates radio access, not chat.
