## Meters & Signal Readouts

Zeus gives you a full set of meters for both sides of the radio: a receive S-meter for reading signals off the air, and a cluster of transmit meters for setting your mic gain, watching your power and SWR, and keeping your audio chain clean. This chapter explains every reading, where to find it, and how to use it on the air.

A handy thing to know up front: **Zeus switches every meter between RX and TX for you.** The radio already knows whether you're transmitting (MOX or TUNE engaged), so the S-meter automatically flips to show transmit power, and the analog dial flips to its PO/SWR scales. You never have to tell a meter which side to read.

### Reading the S-meter (receive)

The S-meter is your signal-strength gauge. Zeus ships it in two styles, and you can run either or both as panels in your workspace.

- **Bar S-meter (S-Meter panel).** A horizontal "liquid-metal" bar with an LED-segment look. The fill climbs left to right, warming from amber to power-yellow to red as the signal gets stronger. A bright pip rides along as a short **peak-hold** marker so you can catch a quick peak even after it drops away. On the right is a digital readout in **dBm**, with an S-unit label (for example "S7" or "S9+20") beneath it.
- **Analog S-meter (Analog Meter panel).** A classic moving-coil dial with a swinging needle and a slowly-decaying peak-hold "ghost." The needle motion is smoothed with the same realistic attack/decay physics every Zeus meter uses, so it reads like real lab gear rather than a jumpy number.

**The dB scale.** Zeus uses the standard amateur S-unit scale: each S-unit is 6 dB, and **S9 equals −73 dBm** on HF, exactly as your transceiver's manual specifies. Below S9 the dial runs S0 through S9; above S9 it's labelled in dB over S9 — **+10, +20, +40, +60**. So a reading of "S9+20" means a strong signal 20 dB over S9. On the bar meter a faint divider marks the S9 point, and labels past S9 turn power-yellow to signal you've gone "into the red."

**SNR readout.** When the signal-strength estimator has a noise-floor measurement, the bar S-meter shows a small cyan **"SNR _n_ dB"** figure — your signal's strength above the measured display noise floor. It only appears when the estimate is valid and positive.

**Zeus mode (a bit of fun).** The analog meter's gear menu has a single toggle: **Zeus mode**. Turn it on and an image of Zeus fades in behind the dial as signals pass S9, with a blue lightning flicker once you hit S9+20. It's pure visual flair — it never changes any reading or behavior.

#### S-meter calibration by board

The raw signal level WDSP reports is trimmed per radio so different boards read consistently. You don't set this — Zeus applies it automatically based on the connected radio:

| Board | S-meter offset |
|-------|----------------|
| Hermes-Lite 2, ANAN-200D, default | +0.98 dB |
| ANAN-7000 / ANAN-8000 (Orion-class) | +4.84 dB |
| ANAN-G2 / G2 MkII (Saturn), G2-1K | −4.48 dB |

Individual units can still drift by a few tenths of a dB; this is normal.

### Transmit meters

When you key up (MOX) or hit TUNE, the meters switch to transmit. The main home for transmit metering is the **TX Stage Meters panel** — an immersive, lab-gear-style cluster in three sections. The bar/analog S-meter panels also flip to a simple **PWR** (forward watts) reading while you transmit.

All transmit meters update at about 10 readings per second from the radio, with the needles and bars smoothed to ~30 Hz so they move continuously instead of stepping.

#### Final Output: forward power and SWR

The top section shows the two readings that matter most on the air, as large arc gauges:

- **Forward Power (watts).** Your actual forward power at the antenna port, measured by the radio's RF coupler — not a guess from drive level. The arc's full scale tracks your radio automatically: your PA-panel power setting if you've set one, otherwise the board's rated wattage (a Hermes-Lite 2, for example, reads on a 0–10 W scale), falling back to 100 W. A footer strip shows your per-keydown **Peak (PEP)** watts and lights an amber "rated" LED when you're sitting right at the PA's rated power.
- **SWR.** Standing-wave ratio at the antenna. The arc runs **1.0 to 3.0+** with the **2.0 mark highlighted red** — that's the alert threshold. SWR is derived from forward and reflected power and is clamped to sensible limits so it doesn't read wild numbers at very low power. In the S-meter chip row, the SWR figure is plain at a good match, turns **power-yellow at 2:1 or worse**, and **red at 3:1 or worse**.

A high SWR is your cue to stop and check your antenna or tuner before you keep transmitting. Zeus has its own SWR-trip protection (covered in the TX chapter) that will fold back power on a bad match.

#### Signal Chain: your audio staging meters

The middle section is six vertical VU columns showing your transmit audio as it moves through the DSP, each on a **−60 to +6 dBFS** scale with peak (PK) and average (AVG) pairs:

| Column | What it shows |
|--------|---------------|
| **MIC PK / AVG** | Microphone level entering the DSP |
| **LEV PK / AVG** | Level after the Leveler (the slow gain-riding stage) |
| **ALC PK / AVG** | Level after the ALC (Automatic Level Control), the final stage before the PA |

Reading PK and AVG side by side lets you judge both your peaks (for clipping) and your sustained level (for talk power) at the same time. If a stage isn't engaged it sits at a "silent" floor and reads as inactive rather than showing a bogus level.

#### Gain Reduction: how hard your processing is working

The bottom section has two "pull-down" arcs reading **0 to −20 dB** of gain reduction:

- **Leveler GR** — how much the Leveler is cutting.
- **ALC GR** — how much the ALC is pulling the signal down.

These read as positive "how much am I cutting" figures. A little gain reduction is healthy and means your processing is doing its job; constant heavy reduction on every syllable usually means your mic gain or drive is too hot.

### The mic meter (setting your audio before you key up)

The **MIC meter** sits in the transport strip and is the one meter you should look at *before* transmitting. It's a small horizontal bar with a permanent red zone painted across the **last 3 dB** of the scale, so you always have a "target just below the top" reference.

Crucially, it shows the **effective** level — your raw microphone level **plus** your mic-gain setting — because that combined value is what actually reaches the ALC and can clip. Adjust your mic-gain knob so your voice peaks live just under that red zone; the bar turns red and the number turns red when you're peaking into the last 3 dB. Hover the meter and Zeus shows the math (raw dBFS + gain = effective dBFS at ALC).

A note on where the mic level comes from: in the **web/browser** setup Zeus measures your mic directly in the browser; in the **desktop app** it reads the level the server publishes. Either way the meter shows whichever path is live. If your microphone permission is denied, the meter reads "mic unavailable" — grant browser microphone access to fix it.

### Two-tone IMD readout (transmit purity)

When you engage the **two-tone test** on transmit, an **IMD overlay** appears in the upper-left of the panadapter. This is the objective tool for judging your transmit cleanliness — and especially for tuning PureSignal — instead of eyeballing the trace. It reports:

- **IMD3** and **IMD5** — your third- and fifth-order intermodulation products, shown as suppression in **dBc** (decibels below the test tones; more negative is cleaner).
- **OIP3** — output third-order intercept point in dBm.
- **Δf** — the spacing between the two tones, and **f0** — the two fundamental tone levels.

The overlay measures whatever the panadapter is currently showing, so to read your *amplifier's* purity, view the post-PA feedback or a monitor receiver. If it can't find the peaks it tells you so — make sure both tones and their IMD products are actually in view. (Lower IMD3/IMD5 numbers and a flat-shouldered two-tone are the goal; this is exactly what PureSignal is correcting for.)

### PA temperature chip (Hermes-Lite 2)

If you're running a **Hermes-Lite 2**, the transport bar shows a **PA TEMP** chip reading the board's Q6 power-amplifier temperature sensor (updated about twice a second). This matters because the HL2's gateware **auto-shuts the PA at 55 °C**, so you want headroom at a glance:

| Reading | Meaning |
|---------|---------|
| Below 50 °C | Normal (shown to one decimal) |
| 50 to 55 °C | Warning (amber) |
| 55 °C and above | Danger (red, with a soft glow) |

If you see the chip creeping into amber during long transmissions, back off your duty cycle or power before the radio shuts itself down. A dash ("— °C") means no reading yet or the radio is disconnected. This chip is HL2-specific and stays hidden on the mobile layout.

### Quick reference

| Meter | Where | Reads | Good practice |
|-------|-------|-------|---------------|
| S-meter (bar / analog) | S-Meter / Analog Meter panels | Signal strength, dBm + S-units; SNR | S9 = −73 dBm; calibrated per board |
| Forward Power | TX Stage Meters / PWR bar | Watts at the antenna | Scale tracks your radio's rated power |
| SWR | TX Stage Meters | Antenna match ratio | Watch the red 2:1 mark; stop if high |
| MIC | Transport strip | Effective mic level (raw + gain) | Peak just under the red top 3 dB |
| Signal Chain VU | TX Stage Meters | MIC / LEV / ALC, peak & average | Keep peaks healthy, avoid constant clipping |
| Gain Reduction | TX Stage Meters | Leveler / ALC cut, dB | A little is good; constant heavy cut is too hot |
| 2-Tone IMD | Panadapter overlay | IMD3/IMD5 dBc, OIP3 | Engage two-tone; view post-PA for amp purity |
| PA Temp | Transport strip (HL2) | PA temperature °C | Mind the 55 °C auto-shutdown |
