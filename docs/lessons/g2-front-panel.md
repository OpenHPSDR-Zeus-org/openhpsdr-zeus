# ANAN G2 / G2-Ultra hardware front panel

The G2 "Mk2" / G2-Ultra control front is **not a screen image to port** — it's a
serial controller (push-buttons, dual-concentric encoders, status LEDs) that
speaks the **ANDROMEDA CAT dialect** over a UART. Zeus bridges it in
`Zeus.Server.Hosting/FrontPanel/`, routing panel events through the *same*
`RadioService` / `TxService` seams the web UI uses — so a physical button and an
on-screen click are indistinguishable downstream, and the UI reflects panel
actions automatically via the normal state broadcasts.

The authoritative default map is Thetis's `MakeNewG2PanelDataset`
(`Andromeda.cs`, MW0LGE/G8NJJ); the ANDROMEDA wire framing and the type-5 button
numbering are cross-checked against DeskHPSDR's `rigctl.c` — see ATTRIBUTIONS.md.

## Wire protocol (ANDROMEDA)

- **Panel → host:** `ZZZSxxyyzzz;` announces console type (`xx`; **5 = G2-Ultra**),
  `ZZZPxxy;` push-button (`xx` id, `y` = 0 release / 1 press / 2 long-hold),
  `ZZZExxy;` encoder (`xx` id, +50 = counter-clockwise; `y` = ticks),
  `ZZZUxx;` / `ZZZDxx;` main-VFO up / down (accelerated step count).
- **Host → panel:** `ZZZIxxy;` sets LED `xx` on/off.

Short vs long press is derived from the `v = 0→1→2→0` sequence, tracked with a
single shared "last v" (`G2PanelActionRouter._lastV`), exactly as the firmware
expects. Actions are only routed once `ZZZS` confirms **type 5** — a wrong map
could mis-key the transmitter.

## Deployment & config

The panel is wired to the host running Zeus (on a stock G2, the internal Pi).
Install the udev rule so the line gets a stable name:

```
sudo cp scripts/udev/61-g2-front-panel.rules /etc/udev/rules.d/
sudo udevadm control --reload && sudo udevadm trigger
```

`G2FrontPanelService` then auto-detects `/dev/serial/by-id/g2-front-9600`
(or `-115200`). No panel present → it idles and re-probes; safe to leave
registered on every host. Override via configuration section `G2FrontPanel`:

```jsonc
"G2FrontPanel": { "Enabled": true, "DevicePath": "/dev/ttyACM0", "Baud": 9600 }
```

(or env: `G2FrontPanel__DevicePath=COM5` for a USB panel on a Windows desktop).

## PureSignal safety

The G2-Ultra panel has **no PS push-button** (only a status LED), so the router
has *no* code path that arms PureSignal. The KB2UKA no-auto-arm invariant is
preserved structurally — `SetPs` is never reachable from panel input.

## Hardware PTT / footswitch & rear-panel I/O (NOT the front panel)

A footswitch, mic PTT, or grounded rear KEY jack is **not** an ANDROMEDA panel
event — it is wired into the *radio*, which echoes the level back to the host.
This path is independent of `G2FrontPanelService` and already complete:

- **Protocol 1** — `Protocol1Client` reads the EP6 `C0[0]` bit-0 echo
  (`PacketParser.ExtractHardwarePtt`) and raises `HardwarePttChanged`. The shaped
  CW keyer output (`C0[2]`) is read separately (`ExtractCwKeyDown`) to drive the
  local sidetone per dit/dah.
- **Protocol 2** (G2 / G2-Ultra / OrionMkII family) — `Protocol2Client` decodes
  the UDP-1025 hi-priority status byte 0 (bit 0 PTT-IN, bit 1 Dot, bit 2 Dash;
  offsets mirror Thetis `network.c:689-716`) and raises `TelemetryReceived`.
- **`ExternalPttService`** promotes either edge to MOX as `MoxSource.Hardware`
  with a 250 ms CW hang, and drives the PTT-IN status lamp regardless of the
  enable gate. `RadioSettingsPanel` exposes the live KEYED lamp, the
  **Hardware PTT → MOX** opt-in toggle (`PttSettingsStore`, persisted), and the
  hang readout.

The enable gate **defaults ON** (Thetis-faithful): the footswitch keys MOX out
of the box. This is safe because promotion is **edge-triggered** — arming the
gate never auto-keys on connect or restart; the radio only transmits on a real
footswitch edge. An operator who wants UI-only keying turns the gate OFF in
Radio Settings, and that choice persists. The `feat/hardware-ptt-works-e2e`
change flipped this default from the original opt-in OFF.

**User I/O (J16 / accessory lines)** is decoded read-only: the P2 hi-priority
user ADCs (`UserAdc0..3`) and digital input byte are surfaced at
`/api/radio/user-io/labels` and `/api/radio/dig-in` (G2 TX-disable = IO5,
active-low). The **output / automation** side — driving open-collector outputs to
key an external amp/sequencer on TX, or actually *blocking* TX on the Dig-In
line — is intentionally **unarmed** (`ActionBindingsConfigured: false`,
`TxInhibitBehaviorArmed: false`). That surface touches real PA-keying and TX
interlocks, so it stays read-only until an explicit hardware-inhibit policy is
enabled. See the follow-up issue rather than arming it from here.

## Mapped controls (full G2-Ultra parity)

Default assignments follow Thetis's `MakeNewG2PanelDataset`.

Buttons: all HF band buttons, **LF/MF (btn 38 → 2200 m / 630 m toggle)**, MOX,
TUNE, two-tone, CTUN, A/B swap, A>B / B>A, SPLIT, mode/filter/band stepping,
filter reset, NB/SNB/NR (F1–F3), **per-RX mute (MUTE_RX1/RX2)**, **VFO LOCK**,
**RIT/XIT select + clear**, **ATU tune (btn 4)**, **diversity on/off (btn 41)**,
**MULTI select (btn 3)**.
Encoders: AF gain (RX1/RX2), AGC top (RX1), TX drive, RX attenuation,
filter cut high/low, **RIT/XIT offset**, **diversity gain / phase**, and the
**MULTI** encoder (enc 5).
LEDs driven: **1 = MOX, 2 = TUNE, 3 = PS, 6 = RIT, 7 = XIT, 9 = LOCK**.

RIT/XIT behaviour: `RITSELECT` (btn 11) cycles none → RIT → XIT → none; the
clear button (btn 12) zeroes both offsets; the `RIT/ATTN` encoder (enc 7) nudges
whichever offset is active (RIT wins when both are on) in 10 Hz steps, clamped to
±99999 Hz (Thetis udRIT/udXIT range).

MULTI encoder: the MULTI push-button (btn 3) cycles the MULTI encoder (enc 5)
through the Zeus-backed subset of Thetis's multi-function list — RX1/RX2 AF gain,
RX1 AGC, attenuation, filter high/low cut, RIT, XIT, TX drive, diversity
gain/phase. The selected function is logged (`g2panel.multi.select`).

ATU: btn 4 fires a `RequestAtuTune` pulse (Protocol-1 auto-tune-start bit).
Thetis leaves this button unassigned by default and reaches ATU from a softkey
menu; Zeus binds the silk-labelled button directly. The tune-request bit is
spec-correct but bench-verification against a radio with an ATU is still pending.

## Panel functions still without a Zeus mapping (log `g2panel.unmapped`)

| Panel control | Status |
|---|---|
| AGC top on RX2 (enc 2) | `SetAgcTop` is global, not per-RX — no per-RX AGC backend |
| Long-press softkey menus (MODE/FILTER/BAND/NOISE) | **Intentional no-op** — Thetis pops an on-panel menu; Zeus uses its web UI instead |
| ATU-ready / active-VFO LEDs (4/5, 8) | no Zeus state to drive them |

Approximations worth noting: `FILTER+/-` widens/narrows the passband around its
centre (no preset table in the backend), `SPLIT` toggles the TX VFO A↔B, the A/B
button (btn 10) swaps VFO frequencies (Zeus has no active-VFO focus), and the
main VFO knob tunes RX1 at `VfoStepHz` (10 Hz) per accelerated step.
