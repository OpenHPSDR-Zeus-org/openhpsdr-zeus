# Session password — operator-set access control for remote sessions

Remote access is gated by a **session password** the operator sets in the Server menu. The QRZ
callsign ([0007](./0007-callsign-identity-and-verification.md)) is only the *address* — it is
public and shareable. The password is the *access control* that decides who may actually open a
remote session to the radio. Because Zeus drives real HF amplifiers, **remote access cannot be
enabled without a session password set** — there is no "open to the internet, no password" mode.

## Where the password is verified — at the radio, never at the broker

The broker ([0006](./0006-broker-signaling-turn-callsign.md)) is a third party in the signaling
path, so it must never learn the password. The `Zeus.Server` is the resource being protected and is
the sole verifier:

- Stored **Argon2id-hashed** in the `Zeus.Server` prefs (LiteDB, cross-platform; never plaintext,
  never synced to the broker or the domain).
- The remote client proves knowledge of the password to `Zeus.Server` *after* the WebRTC channel is
  up, over the **DTLS-encrypted DataChannel** — which the broker and any TURN relay cannot read.
- **Channel-binding against a malicious broker:** a broker that tampers with SDP DTLS fingerprints
  could in principle MITM the channel. To defend against a compromised broker, use a
  **PAKE (SPAKE2-style)** so the password authenticates both ends and binds to the channel — the
  broker cannot impersonate without the password and cannot mount an offline dictionary attack.
  (Magic Wormhole uses SPAKE2 for exactly this "secret over a brokered channel" problem.) A simpler
  HMAC-challenge-response over DTLS is acceptable as an interim *only* while we fully trust the
  broker; PAKE is the target because the broker is third-party infrastructure.

## Consequences

- **Decouples access from identity.** The remote user does not need to *be* the callsign holder —
  anyone the operator gives the link + password to can connect. This delivers the "lend my shack to
  a friend" case that [0003](./0003-random-slug-same-account-pairing.md) explicitly could not,
  *without* sharing QRZ credentials.
- The operator accessing their own radio uses the same password; no special-casing.
- Brute-force defense lives on `Zeus.Server`: per-attempt rate limit + exponential backoff +
  optional lockout. The broker's existing per-IP connection limit is a second, coarser layer.
- Password entry on the remote client uses an **in-app form** (PromptDialog pattern), never
  `window.prompt` — see the project's no-browser-dialogs rule.
- Changing or clearing the password is an operator action in the Server menu; clearing it while
  remote access is on forces remote access off (the no-password-no-remote invariant).
- A wrong password fails *closed*: no streams, no control, no radio state leaked — the client shows
  "incorrect password," nothing more.
