# CAT (Kenwood TS-2000 over TCP) — command parity reference

Zeus's CAT server speaks a subset of the Kenwood TS-2000 ASCII CAT protocol over a raw TCP
socket, so loggers (N1MM+, Log4OM), digital-mode apps (WSJT-X, JTDX, fldigi), and the Hamlib
`rigctl`/`net rigctl` bridge can control the radio. It mirrors the TCI server
(`Zeus.Server.Hosting/Tci/`); the only structural difference is transport (raw TCP vs.
WebSocket-over-Kestrel). Default port **19090**, disabled by default, loopback bind.

**Board-agnostic by construction.** CAT contains zero board-specific code. Every command maps to
the same `RadioService` / `TxService` seam the TCI server and the UI already use, so it behaves
identically across every board Zeus supports (Metis / Hermes / HermesII / ANAN-10/10E/100-series /
OrionMkII family incl. G2 / HermesC10 / Hermes-Lite 2) and both Protocol 1 and Protocol 2. The only
hardware Zeus can currently bench-test on is the **ANAN-G2 (P2)**; correctness on all other boards
rests on this board-agnostic design + the test suite + CI, not on a bench.

## Wire framing

ASCII; each command is a 2-letter id + optional fixed-width args, terminated by `;`. Commands may
arrive batched (`FA;MD;`) or split across TCP segments; `CatProtocol.ExtractCommands` reassembles
both, and a never-terminated token is bounded at 256 chars. Unknown/unsupported commands reply
`?;` (Kenwood convention). Set commands have no reply; query commands reply.

## Tier-1 command coverage (implemented)

| Cmd | Form | Meaning | Zeus seam |
|-----|------|---------|-----------|
| `ID` | get | radio id → `ID019;` (TS-2000) | static — Hamlib requires this first for rig-detect |
| `PS` | get | power status → `PS1;` | static |
| `AI` | get/set | Auto-Information level; gates async pushes | per-session flag (mirrors TCI rx_sensors) |
| `FA` | get/set | VFO A frequency (11-digit Hz) | `Snapshot().VfoHz` / `SetVfo(hz, fromExternal:true)` |
| `FB` | get/set | VFO B frequency | `Snapshot().Receivers[1].VfoHz` / `SetVfoB(hz)` |
| `MD` | get/set | mode (Kenwood digit) | `Snapshot().Mode` / `SetMode(mode)` |
| `IF` | get | 35-byte transceiver status | synthesized from `Snapshot()` + `_tx.IsMoxOn` |
| `TX` | set | key MOX | `TrySetMox(true, MoxSource.Cat)` |
| `RX` | set | unkey MOX | `TrySetMox(false, MoxSource.Cat)` |
| `FR`/`FT` | get/set | RX/TX VFO (split) | report-only (no-split); accept-noop set — Zeus has no split seam yet |
| `SM` | get | S-meter (0000–0030) | cached `RxMeterUpdated` dBm → approximate Kenwood scale |
| `PC` | get/set | drive/power (0–100%) | `Snapshot().DrivePct` / `SetDrive(pct)` (clamp 50% if LimitPowerLevels) |

### Mode digit map (`MD`)
`1`=LSB `2`=USB `3`=CWU(CW, normal) `4`=FM `5`=AM `6`=DIGL(FSK) `7`=CWL(CW-R, reverse) `9`=DIGU(FSK-R)
— CW polarity per Thetis `Mode2KString` and Hamlib (3=`RIG_MODE_CW`, 7=`RIG_MODE_CWR`). Zeus-only
modes fall back: SAM→5(AM), DSB→2(USB), FreeDv→2(USB, runs as USB at WDSP). Matches Thetis
`Mode2KString` (`"2"` fallback).

### IF response field layout (taken byte-for-byte from Thetis `CATCommands.IF()`)
`IF` + 35-byte body + `;` (38 chars total):

| Field | Width | Offset | Source |
|-------|-------|--------|--------|
| P1 frequency | 11 | 0 | VFO A Hz, zero-padded |
| P2 step size | 4 | 11 | `0000` (Zeus exposes no tune-step) |
| P3 RIT/XIT value | 6 | 15 | sign + 5 digits; `+00000` (Tier-1 has no RIT) |
| P4 RIT status | 1 | 21 | `0` |
| P5 XIT status | 1 | 22 | `0` |
| P6 memory bank | 3 | 23 | `000` |
| P7 TX/RX | 1 | 26 | `1` if MOX else `0` |
| P8 mode | 1 | 27 | Kenwood mode digit |
| P9 FR/FT | 1 | 28 | `0` |
| P10 scan | 1 | 29 | `0` |
| P11 split | 1 | 30 | `0` (no split) |
| P12 balance | 4 | 31 | `0000` |

## Auto-Information (async pushes)
`AI1;`/`AI2;` sets the session's AI flag; the server then pushes `FA`/`MD`/`IF` on radio
state-change events (`RadioService.StateChanged`/`MoxChanged`), VFO rate-limited (reuses
`TciRateLimiter`). `AI0;` (default) = poll-only; such clients only get query responses. **No frame
is ever sent before an explicit client command, and a CAT connection never auto-keys TX.**

## Tier-2 (next PR)
RIT/XIT (`RT`/`XT`/`RU`/`RD`/`RC` → `SetRit`/`SetXit`), AF gain (`AG`), AGC (`GT`), filter width
(`FW`), CW keyer (`KS`/`KY`), DSP toggles (`SQ`/`NB`/`NR`), true split (needs a RadioService split seam).

## Tier-3 (out of scope)
ZZ* PowerSDR/Thetis extended set, memory channels (`MC`/`MR`/`MW`), antenna (`AN`), scan (`SC`),
band up/down (`BD`/`BU`) — no logger/digital-mode dependency.

## Client setup (example — WSJT-X)
Settings → Radio → Rig = **Kenwood TS-2000**, CAT = **Network**, Network Server = `ZeusIP:19090`,
PTT = **CAT**. Hamlib net rigctl: point `rigctld -m 2014` (TS-2000) at the same host:port.

## Safety invariants
- CAT keys ONLY via `TxService.TrySetMox(.., MoxSource.Cat)` — never arms PureSignal, never
  auto-keys on connect. A CAT-keyed MOX is releasable only by CAT or the UI master override
  (SWR/timeout trips still bypass).
- No new dependencies (raw `TcpListener` + hand-written parser). Cross-platform (sockets/ASCII only).
- Loopback default; the UI warns on a non-loopback bind (no authentication).
