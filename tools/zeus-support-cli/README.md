# zeus-support-cli

Non-interactive admin CLI for the **Zeus remote-diagnostics** broker
(`cloud/zeus-remote-broker`, see [`cloud/ADMIN_AUTH_DESIGN.md`](../../cloud/ADMIN_AUTH_DESIGN.md)).
It lets an automated agent — or a maintainer at a terminal — authenticate with an
admin token, see which **consented** operators are online, request a **read-only**
diagnostics session, and pull a consenting operator's logs + diagnostics over
WebRTC.

This is **Phase 4b** of the remote-diagnostics epic. It is a pure *consumer* of
the broker's already-deployed HTTP + WebSocket + data-channel APIs; it changes no
backend or broker code.

> **Consent is enforced at the radio, not here.** `pull` cannot read anything
> until the operator presses **Allow** on their own radio (the broker only hands
> back a single-use ticket; the data session needs the operator's grant). The
> session is GET-only over an allowlist — no control, no transmit, never any
> PureSignal surface.

## Install

```bash
cd tools/zeus-support-cli
npm install
npm run typecheck   # green
npm test            # unit tests for the report/config shaping
```

Runs on Node 18+ via [`tsx`](https://github.com/privatenumber/tsx). WebRTC is
provided by the pure-TypeScript [`werift`](https://github.com/shinyoshiaki/werift-webrtc)
stack, so there are no native build steps.

## Configuration

| Env var | Meaning | Default |
| --- | --- | --- |
| `ZEUS_ADMIN_TOKEN` | Bearer token (`zsa_…`) for protected commands | — |
| `ZEUS_REMOTE_BROKER_URL` | broker base url | `https://remote.openhpsdrzeus.com` |
| `ZEUS_QRZ_CALLSIGN` / `ZEUS_QRZ_SESSION` / `ZEUS_ADMIN_PASSWORD` | `login` credentials | — |

Every command also accepts `--broker <url>` and `--token <tok>` flags, and a
`--json` flag for machine-readable output. **No secret is ever written to disk.**

## Commands

```
zeus-support login      authenticate (QRZ + password); print a session token
zeus-support token mint exchange a session token for a long-lived agent token
zeus-support presence   list online, support-available operators (alias: list, whoami)
zeus-support request <callsign>   ask an operator to Allow a diagnostics session
zeus-support pull <callsign>      request + connect + collect a diagnostics report
```

Run any command with `--help` for its full options.

### Agent-token flow (recommended for automation)

A session token expires in ~12h; an **agent token** never expires (it is
revocable instead). Mint one once and store it in your agent's secret store:

```bash
# 1. interactive login (proves callsign ownership via QRZ + admin password),
#    minting a long-lived agent token in the same step:
zeus-support login \
  --callsign KB2UKA \
  --qrz-session "$QRZ_SESSION_KEY" \
  --password "$ADMIN_PASSWORD" \
  --mint

# → prints the agent token ONCE. Store it:
export ZEUS_ADMIN_TOKEN=zsa_xxxxxxxxxxxxxxxxxxxx
```

You can also mint later from an existing session token:

```bash
ZEUS_ADMIN_TOKEN=<session-token> zeus-support token mint --label agent
```

### See who is online

```bash
ZEUS_ADMIN_TOKEN=zsa_… zeus-support presence
# Online operators (1):
#   N9WAR      since 2026-06-27 18:00Z  last-seen 2026-06-27 18:04Z

ZEUS_ADMIN_TOKEN=zsa_… zeus-support presence --json
```

### Request a session (operator must Allow)

```bash
ZEUS_ADMIN_TOKEN=zsa_… zeus-support request N9WAR
# requestId : 8f3c…
# ticket    : tkt_…
# N9WAR must press Allow on their radio. Then run:
#   zeus-support pull N9WAR
```

`request` exits non-zero with a clear message if the operator is offline (broker
returns `503 operator offline`).

### Pull a diagnostics report (the full flow)

```bash
ZEUS_ADMIN_TOKEN=zsa_… zeus-support pull N9WAR \
  --format json \
  --log-seconds 5 \
  --out n9war-report.json
```

`pull` does everything end-to-end:

1. `POST /admin/request` → single-use `ticket` (`503` ⇒ *operator offline*).
2. Opens `wss://<broker>/signal?role=support&callsign=<OP>&ticket=<ticket>`.
3. Waits up to `--grant-timeout` seconds (default 90) for the operator's
   `{t:"support-grant"}` (i.e. they pressed **Allow**). No grant ⇒ clean
   *denied/timeout* exit.
4. WebRTC: creates the `control` / `api` / `log` data channels, sends
   `{t:"offer", sdp}` (the broker stamps `support`+`requestId`), applies the
   `{t:"answer"}`.
5. `control` `hello` → `support-ready`; fetches `/api/version`, `/api/state`,
   `/api/diagnostics/v2` (override with `--paths a,b,c`) over the GET-only `api`
   channel; captures the `log` backlog + `--log-seconds` of live log.
6. Writes a tidy JSON or text report to `--out` (or stdout).

#### Offline shaping / dry run

The live `pull` path needs the deployed broker **and** a consenting online
operator. To exercise the report shaping without any network:

```bash
zeus-support pull N9WAR --mock              # synthetic JSON report
zeus-support pull N9WAR --mock --format text
```

## Exit codes

| code | meaning |
| --- | --- |
| 0 | success |
| 1 | generic failure (e.g. unauthorized) |
| 2 | usage / configuration error (missing token, bad flag) |
| 4 | login rate-limited |
| 5 | operator offline |
| 6 | network / WebSocket error or timeout |
| 7 | WebRTC negotiation / data-channel failure |
| 8 | operator denied or did not Allow in time |

## What needs the live broker to verify

`login`, `token mint`, `presence`, `request`, and the live `pull` path all
require the deployed broker (and, for `pull`, an online operator who presses
Allow), so they can't be fully exercised in CI — matching the project's
"live needs the deployed broker" convention. What *is* verified here:
`npm run typecheck` is green, the unit tests cover the report/config shaping, and
`pull --mock` exercises the full formatting path offline. The WS / WebRTC /
data-channel protocol is implemented exactly per the Phase-4b spec.
