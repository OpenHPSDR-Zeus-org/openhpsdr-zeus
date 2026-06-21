# Remote wake launcher — bringing an offline station online on demand

Remote access ([0005](./0005-webrtc-data-plane.md)–[0008](./0008-session-password-access-control.md))
requires the operator's `Zeus.Server` to already be **running** so it can register with the broker
([0006](./0006-broker-signaling-turn-callsign.md)) and answer the SPAKE2+ password proof
([0008](./0008-session-password-access-control.md)). If the app is closed, a remote operator is
stuck: there is no host to pair with, and the viewer page simply times out.

This ADR proposes an **optional, operator-installed wake launcher** — a lightweight always-on OS
agent that can launch `OpenhpsdrZeus` on the station PC on demand, so the operator does not have to
leave the full app running 24/7. The operator installs/removes it from the **Server menu**.

> Status: **Proposed.** This is the architecture + security model only. Implementation is phased
> (below) and gated on maintainer sign-off, because waking a station is a security-sensitive
> action (remote-triggered process launch on a machine attached to a real HF amplifier).

## The chicken-and-egg, and why the session password cannot gate the wake

The session password ([0008](./0008-session-password-access-control.md)) is verified **at the
radio, by the running app** via SPAKE2+. The whole reason we need a wake launcher is that the app
is **not running** — so it cannot verify the session password to authorise its own launch. The wake
trigger therefore needs a **separate authentication path** that does not depend on the app being up,
and that **never weakens ADR-0008**.

Two hard separations make this safe:

1. **Wake ≠ access.** A successful wake does *exactly one thing*: it launches the `OpenhpsdrZeus`
   process. It grants **no** radio access, **no** control, **no** state, and absolutely **no TX**.
   Once the app is up it begins LOCKED and the full ADR-0008 deny-by-default state machine applies —
   the remote operator must still prove the session password to do anything at all. The wake agent
   is not in the data or control path and has no radio capability of its own.

2. **The wake credential is distinct from the session password.** The session password is never
   exposed outside the running app (ADR-0008) and cannot be checked while the app is down. The wake
   path uses its own operator-set **wake credential**, proven to the **agent** (not the broker), so
   the broker never learns it (consistent with ADR-0006/0008: the broker is an untrusted relay).

## Proposed architecture

```
 viewer (browser)                 broker (CF Worker)              station PC
 ----------------                 ------------------              ----------
 GET /go/<call> ... offline? ----> wake-route ---- WS push -----> wake agent (OS service)
                                   (QRZ-gated)                    │  verify wake proof
                                                                  │  rate-limit
                                                                  ▼
                                                            launch OpenhpsdrZeus
                                                                  │
 normal remote flow (0005-0008): viewer ── SPAKE2+ password ──► Zeus.Server (LOCKED→UNLOCKED)
```

- **Wake agent** — a minimal long-lived OS service, *not* the full app. It holds a persistent
  authenticated connection to the broker as the **wake host** for the operator's QRZ callsign
  (QRZ-gated exactly like host registration in ADR-0006/0007). It does nothing but listen for a
  wake request and launch the app. It opens no radio sockets and loads no WDSP.
- **Broker wake route** — when a viewer requests a callsign whose real `Zeus.Server` host is
  offline but a wake agent is connected, the broker relays a **wake request** to the agent. The
  broker QRZ-gates agent registration (it cannot register a wake host for a callsign it has not
  proven). The broker never sees the wake credential.
- **Wake proof** — the request carries a proof of the **wake credential** that the agent verifies
  locally (PAKE or HMAC challenge/response; never plaintext, never to the broker). On success the
  agent launches the app **as the logged-in user**, then drops back to listening. On failure /
  rate-limit it does nothing and leaks nothing.
- **Hand-off** — once `OpenhpsdrZeus` is up it registers with the broker normally and the standard
  ADR-0005–0008 flow proceeds. The viewer's existing "radio offline" retry simply succeeds on the
  next attempt.

## Security invariants (full-stop rules)

- **Wake launches only the app — never RF.** No TX, no tune, no control, no PureSignal arm. The app
  starts LOCKED (ADR-0008) and PS still initialises to `false` (project hard rule). The agent has
  no radio capability.
- **Authenticated wake only, deny-by-default.** An unauthenticated or failed wake request does
  nothing. The wake credential is verified at the agent, never exposed to the broker.
- **Rate-limited.** The broker and the agent both throttle wake requests per callsign / per IP to
  prevent wake storms or using wake as a DoS / "flap the station" lever.
- **Explicit operator lifecycle.** Install and remove are explicit Server-menu actions. Uninstall
  fully removes the OS service/agent and its persisted wake credential — no orphaned listener.
- **Least privilege.** The agent runs with the minimum privilege needed to start a user process;
  it does not run as root/SYSTEM beyond what the OS service model requires, and it launches Zeus in
  the operator's own session.
- **Auditable.** Each wake attempt (success/failure, source) is logged locally so the operator can
  see who woke (or tried to wake) the station.

## Cross-platform service mechanism

Zeus is cross-platform (hard requirement), so the agent must install cleanly on each:

- **Windows** — a Windows Service (or a Scheduled Task triggered at logon with a persistent
  listener), launching `OpenhpsdrZeus.exe` in the interactive user session.
- **macOS** — a per-user `launchd` LaunchAgent (`~/Library/LaunchAgents/…`), so the app launches in
  the user's GUI session.
- **Linux** — a `systemd --user` unit.

The install/remove UI lives in the Server menu beside the existing remote-access controls; the
backend writes/removes the platform-appropriate unit and stores the wake credential verifier (never
the credential) in the prefs DB.

## Phased delivery

Because the on-demand authenticated wake carries the most security surface, deliver in phases:

- **Phase 1 — Autostart (low risk, ships first).** A Server-menu toggle to install/remove an
  OS autostart entry so `OpenhpsdrZeus` launches at login/boot and the station is simply *always
  available* for remote access. No wake credential, no broker wake route — it just keeps the host
  up. This alone solves the common "I forgot to leave it running" case.
- **Phase 2 — On-demand authenticated wake.** The wake agent + broker wake route + wake credential
  described above, for operators who do not want the app running continuously.

## Decisions the maintainer must make before implementation

1. **Wake credential model** — a dedicated operator-set **wake PIN** verified by the agent
   (recommended: keeps ADR-0008 untouched, broker stays blind), versus QRZ-login-only wake (simpler
   UX, but ties wake authorisation to QRZ identity rather than an operator secret).
2. **Whether Phase 1 (autostart) is wanted** as a shipped feature, or whether to go straight to
   Phase 2.
3. **Windows mechanism** — Windows Service vs Scheduled-Task-at-logon (interactive-session launch
   is the deciding constraint).
4. **Default posture** — install defaults to *off*; confirm the agent ships disabled and is opt-in
   per machine.

Until these are settled this remains **Proposed**; no wake agent code lands without sign-off.
