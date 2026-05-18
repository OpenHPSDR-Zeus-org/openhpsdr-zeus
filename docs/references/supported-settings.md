# Supported Settings Per Radio

A capability matrix across the radios Zeus targets, modeled after Thetis's per-model tables. This describes **what the protocol + hardware allow** — for Zeus's current implementation status, see the code paths cited per row and the `Zeus.Protocol1` / `Zeus.Protocol2` projects.

## Radios covered

| Column | Board | Protocol | Zeus class/branch |
|--------|-------|----------|-------------------|
| **HL2** | Hermes Lite 2 (`HpsdrBoardKind.HermesLite2 = 0x06`) | Protocol 1 + HL2 extensions | `Zeus.Protocol1`, HL2 client |
| **G2 (P1)** | ANAN G2 MkII / Orion MkII (`HpsdrBoardKind.OrionMkII = 0x0A`) | Protocol 1 | `Zeus.Protocol1` |
| **G2 (P2)** | Same hardware, P2 firmware | Protocol 2 | `Zeus.Protocol2` (kb2uka) |

Legend: ✅ supported · ⚠️ partial / with caveat · ✗ not supported · — not applicable

---

## Identity & discovery

| Setting | HL2 | G2 (P1) | G2 (P2) | Spec reference |
|---|:---:|:---:|:---:|---|
| Board ID byte | `0x06` | `0x0A` | `0x0A` | `hermes-lite2-protocol.md`; `USB_protocol_V1.60.doc` §Discovery; P2 v4.4 §General Packet |
| Discovery magic (P1) | `0xEF 0xFE 0x02` | `0xEF 0xFE 0x02` | — | `Metis-How_it_works_V1.33.pdf` |
| Discovery broadcast port | 1024 | 1024 | 1024 (P2 discovery) | Metis + P2 v4.4 §Discovery |
| Extended discovery reply | ✅ (HL2 specific fields) | ⚠️ (standard reply) | ✅ (P2 reply, NumReceivers byte) | `hermes-lite2-protocol.md` §Discovery Reply; P2 v4.4 §Discovery Reply |
| Zeus parser | `Zeus.Protocol1/Discovery/ReplyParser*` | same | `Zeus.Protocol2/Discovery/ReplyParser.cs` | — |

---

## Streaming / sample rates

| Setting | HL2 | G2 (P1) | G2 (P2) | Notes |
|---|:---:|:---:|:---:|---|
| 48 kHz | ✅ | ✅ | ✅ | C0 `0x00[25:24] = 00` |
| 96 kHz | ✅ | ✅ | ✅ | `01` |
| 192 kHz | ✅ | ✅ | ✅ | `10` |
| 384 kHz | ✗ (ADC limited) | ✅ | ✅ | `11` — HL2 AD9866 limited |
| Max simultaneous RX | up to 12 (HL2 gateware dependent) | 7 | 7 | HL2 wiki §Base Memory Map; P2 v4.4 §Receiver specifics |
| Wideband ADC stream | ✅ (76.8 MHz ADC raw) | ✅ | ✅ | HL2 wiki §Wideband; P2 v4.4 §Wideband |
| Duplex | ✅ | ✅ | ✅ | C0 `0x00[2]` |

---

## TX / PA

| Setting | HL2 | G2 (P1) | G2 (P2) | Notes / Zeus source |
|---|:---:|:---:|:---:|---|
| Max rated TX power (W) | 5 | 100 | 100 | `Zeus.Server.Hosting/PaDefaults.cs` (per-board max) |
| PA gain defaults (dB) | 40.5 flat | 44.6–50.9 per band | *TODO — P2 tables not yet seeded* | PaDefaults.cs lines ~30–110 |
| Drive level | ✅ C0 `0x09[31:28]` (4-bit) | ✅ | ✅ (P2 equivalent register) | |
| PureSignal | ✅ (`0x0A[22]`) | ✅ | ✅ | |
| CWX internal keyer | ✅ (`0x0F[24]`, `0x10` hang time) | ✅ | ✅ | |
| ATU tune request | ✅ (`0x09[20]`, `0x09[17]` bypass) | ✅ | ✅ | |
| Onboard PA on/off | ✅ (`0x09[19]`) | ✅ (always on for G2 MkII) | ✅ | |
| T/R relay disable when PA off | ✅ (`0x09[18]`) | — | — | HL2 specific |
| Mic-path IQ (voice TX) | ✗ (no mic on HL2 by default) | ✅ | ✅ (landed in `969a38a` kb2uka branch) | `Zeus.Dsp` TX pipeline |
| Operator-editable TX bandpass + ALC/Leveler | ✅ | ✅ | ✅ | `028caea` on current branch |

---

## RX front end

| Setting | HL2 | G2 (P1) | G2 (P2) |
|---|:---:|:---:|:---:|
| Attenuator implementation | Firmware RX gain reduction (`0x40 \| (60 − dB)`) | Hardware attenuator (`0x20 \| (dB & 0x1F)`) | P2 equivalent HW attn |
| Attenuator range | 0–31 dB (equivalent) | 0–31 dB | 0–31 dB |
| LNA gain range (dB) | –12 to +48 via `0x0A[5:0]` | preamp toggle | preamp toggle |
| Per-TX LNA gain | ✅ (`0x0E[15:8]`) | — | — |
| VNA mode | ✅ (`0x09[23]`, `0x00[10]` fixed +6/−6 dB) | — | — |

Zeus mapping: `Zeus.Protocol1/HpsdrEnums.cs` lines 65–73.

---

## Band / filter / I/O

| Setting | HL2 | G2 (P1) | G2 (P2) |
|---|:---:|:---:|:---:|
| Filter board style | N2ADR filter via 7 OC bits (`0x00[23:17]`) | Onboard ALEX filters + OC | Onboard ALEX filters + OC |
| OC outputs per band | ✅ (`OcTx` / `OcRx` per band) | ✅ | ✅ |
| OC on tune | identical to `OcTx` (no separate override) | identical to `OcTx` | identical to `OcTx` |
| RX antenna select | ✅ (`0x00[13]`) | ✅ (ALEX RX ants 1–3) | ✅ |
| Alex manual filter mode | ⚠️ not yet implemented (`0x09[22]`) | ✅ | ✅ |

Zeus contracts: `Zeus.Contracts/Dtos.cs` (`OcTx`, `OcRx`). The piHPSDR-style global `OcTune` override was removed in #124 for hardware-safety: OC during TUN follows the per-band `OcTx`, identical to TX.

---

## Telemetry / meters

| Setting | HL2 | G2 (P1) | G2 (P2) |
|---|:---:|:---:|:---:|
| ADC overload flag | ✅ | ✅ | ✅ |
| Forward power | ✅ (via RQST response) | ✅ | ✅ |
| Reverse power / SWR | ✅ | ✅ | ✅ |
| PA temperature | ✅ | ✅ | ✅ |
| Supply voltage | ✅ | ✅ | ✅ |
| PA current | ✅ | ✅ | ✅ |
| HL2 extended diagnostics | ✅ (I²C of auxiliary sensors) | — | — |

---

## HL2-only extensions (no G2 equivalent)

These live in the HL2 extended memory map (`0x2B`–`0x3F`). If Zeus gains "generic P1 radio" paths, **do not** emit these on non-HL2 boards.

| Setting | Address | Purpose |
|---|---|---|
| Predistortion config | `0x2B` | PureSignal predistortion subindex |
| Misc commands (watchdog enable/disable, NCO sync, master mode) | `0x39` | Clock + sync + watchdog control |
| HL2 reset on disconnect | `0x3A[0]` | |
| AD9866 SPI write | `0x3B` | Low-level ADC config |
| I²C1 / I²C2 pass-through | `0x3C`, `0x3D` | External board control |
| Response error code | `0x3F` | |

All documented in `protocol-1/hermes-lite2-protocol.md`.

---

## P2-only capabilities (no P1 equivalent for G2)

| Setting | Spec reference | Status in Zeus |
|---|---|---|
| Separate TX / RX / command / high-priority-status endpoints | P2 v4.4 §Packet structure | Implemented in `Zeus.Protocol2` |
| Per-receiver variable sample rate | P2 v4.4 §Receiver Specific | 48 kHz only today (kb2uka) |
| 48 kHz mic stream from host | P2 v4.4 §Mic/Line Samples | ✅ (`969a38a`) |
| Extended PTT / CW keyer command channel | P2 v4.4 §High Priority Status | ⚠️ partial |

---

## Gaps / TODO (known unknowns)

- **P2 per-board PA gain tables.** `PaDefaults.cs` does not seed G2-over-P2 yet. Calibration is operator-side via UI (`PaSettingsStore`), but the first-connect defaults still fall back to P1 Orion-class values.
- **HL2 under P2.** Not supported by HL2 firmware; confirm with `hermes-lite2-protocol.md` before attempting.
- **Master/slave multi-radio** (HL2 wiki mentions Master Commands at `0x39[11:8]`). Unclear if G2 P2 has an equivalent — check P2 v4.4 §High Priority before implementing.
- **ATU behavior parity** between HL2 (internal ATU absent — tune bit is for external ATU) and G2 (onboard ATU). Zeus UI should disambiguate.

When you close a gap, update the relevant row with a PR reference.
