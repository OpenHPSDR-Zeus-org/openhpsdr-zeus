# Session password — operator-set access control for remote sessions

Remote access is gated by a **session password** the operator sets in the Server menu. The QRZ
callsign ([0007](./0007-callsign-identity-and-verification.md)) is only the *address* — it is
public and shareable. The password is the *access control* that decides who may actually open a
remote session to the radio. Because Zeus drives real HF amplifiers, **remote access cannot be
enabled without a session password set** — there is no "open to the internet, no password" mode.

## Hard invariant — deny by default, nothing happens without the password

This is a full-stop rule, enforced **server-side** (the client UI prompt is not the gate):

> **A remote session begins LOCKED. While locked, `Zeus.Server` performs exactly one operation —
> the password proof. It sends no frames (audio/display/IQ/meters), accepts no control of any kind,
> reveals no radio state, and does not even confirm a radio is present. Every non-auth message on a
> locked session causes an immediate close. The session transitions to UNLOCKED only on a
> successful password proof; on failure, timeout, or too many attempts it is dropped with a generic
> error and nothing leaked.**

Concretely the locked→unlocked state machine lives in the remote transport (Phase 1's
`IRemoteTransport` starts in the LOCKED state and is wired to an auth gate):

- The WebRTC DTLS/SCTP layer may establish (it is just encrypted transport), but the application
  session stays LOCKED.
- The first and only permitted exchange on the control DataChannel is the password proof
  (PAKE/SPAKE2 target — see below). No data-plane channel egresses and no control verb is dispatched
  while LOCKED.
- Unlock arms the radio data paths and control; lock (disconnect / password cleared / idle timeout)
  disarms them again.

This invariant is independent of identity (ADR-0007): even a correctly QRZ-identified remote is
LOCKED until the password proves out. The LAN/localhost trusted path is unaffected — it is not a
remote session and never enters this state machine.

## Where the password is verified — at the radio, never at the broker

The broker ([0006](./0006-broker-signaling-turn-callsign.md)) is a third party in the signaling
path, so it must never learn the password. The `Zeus.Server` is the resource being protected and is
the sole verifier:

- **Mechanism: SPAKE2+ (RFC 9383)**, ciphersuite `P256-SHA256-HKDF-SHA256-HMAC-SHA256`. SPAKE2+ is
  the *augmented* PAKE: at registration the password runs through a memory-hard PBKDF (Argon2id) to
  `w0, w1`; the `Zeus.Server` prefs store only the **verifier** `w0` and `L = w1·P` — never the
  password, never `w1`. A stolen prefs DB therefore does not yield the password (the attacker still
  faces the PBKDF), strictly better than plain SPAKE2 where both sides share the same secret.
- The client (prover) proves knowledge over the **DTLS-encrypted DataChannel** *after* the WebRTC
  channel is up — which the broker and any TURN relay cannot read.
- **Channel-binding against a malicious broker:** a broker that tampers with SDP DTLS fingerprints
  could in principle MITM the channel. SPAKE2+ defeats this — the password authenticates both ends
  and binds the session key, so the broker cannot impersonate without the password and cannot mount
  an offline dictionary attack. (Same class of protection Magic Wormhole gets from SPAKE2.)
- **Implementation discipline:** P-256 only (cofactor 1), hardcoded RFC M/N constants, byte-exact
  transcript (8-byte little-endian length prefixes, uncompressed points), HKDF-SHA256 key schedule,
  `CryptographicOperations.FixedTimeEquals` for every confirmation-MAC check, and mandatory point
  validation (reject off-curve / identity) on every received share. Both the C# (BouncyCastle EC)
  and browser (`@noble/curves`) sides are unit-tested against the **RFC 9383 Appendix C** vectors.

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
