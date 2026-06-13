# Design: HL2 VNA (Vector Network Analyzer) mode

Pairs with `docs/references/protocol-1/hermes-lite2-protocol.md` (the C&C
register spec) and the gateware source it documents
(`softerhardware/Hermes-Lite2`, `gateware/rtl/control.v` + `radio.v`).

**Goal:** let an operator use the Hermes-Lite 2 as a swept scalar/vector
analyzer — filter response (S21) and, with an external bridge, antenna
return-loss / SWR / impedance (S11) — driven from the Zeus web UI, reusing
the existing Protocol-1 control path and RX IQ pipeline.

**Status:** design only. Implementation is gated on maintainer review
(this is a 🔴 red-light feature — new service architecture + new UI) and
on bench verification against a real HL2 (issue `zeus-096`).

## Status legend

- 🟢 **green** — agent-autonomous, low risk, self-PR.
- 🟡 **yellow** — affects an operator-felt default or surfaces UI; agent
  implements, maintainer reviews before merge.
- 🔴 **red** — architecture-class or visual-design; needs maintainer
  alignment *before* code lands. Per `CLAUDE.md`.

---

## 1. What HL2 VNA mode is (hardware)

The HL2 gateware carries a swept-measurement mode written originally by
Jim Ahlstrom (N2ADR), inherited unchanged from the original Hermes-Lite.
When the VNA bit is set:

- **TX and RX share the same NCO** — the board transmits a CW tone and
  receives at exactly the same frequency, phase-coherent. Each emitted
  RX I/Q sample is therefore the **complex ratio (magnitude + phase)** of
  the signal returning to the RX port relative to the transmitted tone.
  That coherence is what makes it *vector*, not just scalar.
- The FPGA **generates the tone itself** — the host does not stream TX IQ.
- The **PA and internal T/R relay are bypassed** (gated with `~vna` in the
  gateware): the tone leaves via the low-power TX path, not the 5 W PA.

There are two scan methods. Zeus targets the **FPGA-scan** (firmware steps
the frequency itself); the older **PC-scan** (host steps each point with
`vna_count = 0`, MOX off) is documented here only as a fallback.

## 2. Protocol-1 register mapping (confirmed against gateware)

Every register below is **already written by Zeus** in the C&C round-robin
(`Zeus.Protocol1/ControlFrame.cs`). VNA support changes only the *content*
of frames that already travel the wire — **no wire-format change, no new
register address.**

| Field | Gateware reg | Bits | Zeus seam |
|---|---|---|---|
| **VNA enable** | `0x09` | `cmd_data[23]` = **C2 bit 7** (`0x80`) | `CcRegister.DriveFilter` (wire `0x12`) |
| **VNA count** (N points) | `0x09` | `cmd_data[15:0]` = **C3:C4** | same frame |
| TX drive level | `0x09` | `cmd_data[31:24]` = C1 | already written |
| **VNA fixed RX gain** | `0x00` | bit 10 (0 = −6 dB, 1 = +6 dB) | `CcRegister.Config` |
| Duplex / 1 RX / 48 ksps | `0x00` | `[2]` / `[6:3]` / `[25:24]` | `CcRegister.Config` |
| **Start frequency** | `0x01` | uint32 Hz | `CcRegister.TxFreq` (wire `0x02`) |
| **Delta Hz / point** | `0x02` | uint32 Hz | `CcRegister.RxFreq` (wire `0x04`) |
| MOX = 1 | C0 | bit 0 | already written |

Gateware confirmation: `vna <= cmd_data[23]` at `cmd_addr == 6'h09` in
`control.v`; `vna_count <= cmd_data[15:0]` in `radio.v`; `0x00` bit 10
"VNA fixed RX Gain" per the HL2 Protocol wiki.

> ⚠️ The bit mapping above is read from source but **must be bench-verified
> on a real HL2** before any UI is built on top — see Phase 1.

## 3. The FPGA-scan sweep procedure

1. Config (`0x00`): rate = 48 ksps, 1 receiver, **Duplex = 1**, fixed RX
   gain bit as desired.
2. RX gain (`0x0a`) and TX drive (`0x09[31:24]`): set conservative levels.
3. Start frequency → TxFreq (`0x01`); delta Hz/point → RxFreq (`0x02`).
4. VNA enable (`0x09[23] = 1`) + `vna_count = N` (`0x09[15:0]`).
5. **MOX = 1** (required for FPGA-scan to emit output).
6. The FPGA zeroes both NCOs once (phase sync), sets the start frequency,
   then per point: adds the delta to the running TX phase, waits to
   stabilize, **averages 1024 CORDIC outputs**, emits one I/Q sample.
7. Before each block it emits **one zero I/Q sample as a separator**. The
   host re-syncs on the zero: the next sample is the start-frequency point,
   followed by exactly N points before the next zero.

Results arrive as ordinary **RX0 `IqFrame`s** — the existing Zeus RX
pipeline already delivers them; the VNA service consumes them instead of
the panadapter while a sweep is running.

## 4. Measurement & calibration model

Each returned `M(f) = I + jQ` is the **raw** vector ratio — it includes
the fixed path delay/gain/phase of the TX→RX route, so it is never a
calibrated S-parameter by itself. Calibration removes the systematic error.

### S21 — transmission (filters, chokes) — **no bridge needed**

TX → (attenuator) → DUT → (attenuator) → RX. Calibrate with a **thru**
(DUT replaced by a barrel). Then `S21(f) = M_dut(f) / M_thru(f)`; magnitude
in dB, phase directly. This is the **first practical target** because it
needs no special RF hardware beyond a couple of attenuators.

### S11 — reflection (antenna, SWR) — **needs an external bridge**

TX → resistive return-loss bridge → RX, DUT on the bridge port, with
6–10 dB attenuators on both legs. Open/Short/Load (OSL) calibration, then
solve the standard 3-term one-port error model for Γ = S11:

- `SWR = (1 + |Γ|) / (1 − |Γ|)`
- `Return loss (dB) = −20·log₁₀|Γ|`
- `Z = 50·(1 + Γ) / (1 − Γ)`

## 5. Caveats (load-bearing)

- **Measures at the low-power TX/RX ports, not the antenna through the PA.**
  Without an external bridge there is no directionality → only S21 is
  meaningful with bare cabling.
- **Never connect TX output directly to RX input without attenuation** —
  it saturates and can damage the ADC. Watch the ADC-overload flags.
- **Phase is only stable while VNA mode is held.** It is undefined at
  power-up and after any normal transmit. → recalibrate after each power
  cycle, and keep VNA mode on for the whole measurement session.
- **Clear the VNA bit on disconnect.** Leaving it set can block the next
  connection until a power cycle (a known bug in older VNA software).
- `0x09[15:8]/[7:0]` is "Alex filter *or* VNA count" — while VNA is on the
  gateware reads the full 16 bits as the point count, so don't multiplex
  Alex/filter data through `0x09` during a sweep.

## 6. Proposed Zeus seams

### `Zeus.Protocol1` 🔴
- `RadioControlState` / `CcState`: add `VnaEnabled`, `VnaPoints`,
  `VnaStepHz` (start frequency reuses `VfoAHz` → TxFreq).
- `ControlFrame.WriteCcBytes`: set Config bit 10 + duplex; set
  DriveFilter C2 bit 7 + count in C3:C4.
- `Protocol1Client`: a sweep mode that diverts `IqFrame`s to a VNA
  consumer (instead of the panadapter channel) and resyncs on the
  zero-separator.

### `Zeus.Server.Hosting` 🔴
- `VnaService`: orchestrates a sweep (set registers → MOX on → collect N
  points → MOX off), applies thru/OSL calibration, computes
  S21 / S11 / SWR / RL / Z.
- DTOs in `Zeus.Contracts`; results pushed via a new WS frame or a REST
  endpoint (`GET /api/vna/sweep`, `POST /api/vna/calibrate`).

### Frontend 🔴
- S21 magnitude/phase plot (first), then SWR / return-loss plot.
- Smith chart for S11.
- O/S/L (and thru) calibration capture flow.

## 7. Rollout — phased

| Phase | Scope | Risk | Hardware |
|---|---|---|---|
| **0** | This design doc | 🟢 | — |
| **1** | HL2-verified spike: enable VNA + run a sweep + dump raw N points to a log. Confirm bit mapping + zero-separator on real hardware. Backend only, no UI. | 🔴 | HL2 + loopback w/ attenuators |
| **2** | S21 path: `VnaService` sweep + thru cal + DTOs + minimal magnitude/phase plot. | 🔴 | attenuators only |
| **3** | S11 path: OSL cal + SWR/RL/Z + Smith chart. | 🔴 | resistive bridge + attenuators |

Phase 1 is the gate: the bit mapping and separator behaviour **must** be
confirmed against a real HL2 before phases 2–3 build on them. Phase 3 is
deferred until the external bridge hardware is on hand.

## 8. References

- HL2 Protocol wiki — register map, VNA mode bit, fixed RX gain, NCO in Hz:
  <https://github.com/softerhardware/Hermes-Lite2/wiki/Protocol>
- HL1 Protocol-Coverage — PC-scan vs firmware-scan recipe:
  <https://github.com/softerhardware/Hermes-Lite/wiki/Protocol-Coverage>
- Quisk `SetVNA()` reference implementation (byte placement, delta-as-phase):
  <https://github.com/IW0HDV/quisk/blob/master/hermes/quisk_hardware.py>
- VNA reflection-mode + phase-stability gotcha (N2ADR):
  <https://groups.google.com/g/hermes-lite/c/3gWoeGuBeUo>
- S21 + impedance math worked example (EA4GPZ):
  <https://destevez.net/2017/09/measuring-a-mains-choke-with-hermes-lite-vna/>
