# ANAN G2 / G2-Ultra hardware front panel

The G2 "Mk2" / G2-Ultra control front is **not a screen image to port** — it's a
serial controller (push-buttons, dual-concentric encoders, status LEDs) that
speaks the **ANDROMEDA CAT dialect** over a UART. Zeus bridges it in
`Zeus.Server.Hosting/FrontPanel/`, routing panel events through the *same*
`RadioService` / `TxService` seams the web UI uses — so a physical button and an
on-screen click are indistinguishable downstream, and the UI reflects panel
actions automatically via the normal state broadcasts.

Source of truth for the protocol and the type-5 button/encoder map is
DeskHPSDR's `rigctl.c` (`andromeda_type == 5`) — see ATTRIBUTIONS.md.

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

## Mapped controls

Buttons: all HF band buttons, MOX, TUNE, two-tone, CTUN, A/B swap, A>B / B>A,
mode/filter/band stepping, NB/SNB/NR, **per-RX mute (MUTE_RX1/RX2)**,
**VFO LOCK**, **RIT/XIT select + clear**.
Encoders: AF gain (RX1/RX2), AGC top (RX1), TX drive, RX attenuation,
filter cut high/low, and the **RIT/XIT offset** knob.
LEDs driven: **1 = MOX, 2 = TUNE, 3 = PS, 6 = RIT, 7 = XIT, 9 = LOCK**.

RIT/XIT behaviour: `RITSELECT` (btn 11) cycles none → RIT → XIT → none; the
clear button (btn 12) zeroes both offsets; the `RIT/ATTN` encoder (enc 7) nudges
whichever offset is active (RIT wins when both are on) in 10 Hz steps, clamped to
±99999 Hz (Thetis udRIT/udXIT range).

## Panel functions still without a Zeus mapping (log `g2panel.unmapped`)

| Panel control | Status |
|---|---|
| Diversity enable / gain / phase (btn 41, enc 11/12) | **Backend exists** (`SetDiversity`) — not yet wired to the panel; trivial follow-up |
| ATU (btn 4, LED 4) | no ATU action wired |
| MULTI knob / button (enc 5, btn 3) | no assignable "multi" parameter |
| AGC top on RX2 (enc 2) | `SetAgcTop` is global, not per-RX |
| Long-press menus (MODE/FILTER/BAND/NOISE) | would need a UI-panel-open message |
| 2200 m / band 136 (btn 38) | outside Zeus's HF band plan |

Approximations worth noting: `FILTER+/-` widens/narrows the passband around its
centre (no preset table in the backend), `SPLIT` toggles the TX VFO A↔B, and the
main VFO knob tunes RX1 at `VfoStepHz` (10 Hz) per accelerated step.
