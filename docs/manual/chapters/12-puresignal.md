## PureSignal (Adaptive Predistortion)

PureSignal is Zeus's adaptive predistortion system. Its job is to make your transmitted signal cleaner. Every power amplifier — the radio's internal PA and any external linear you run after it — adds distortion as you push it harder. That distortion shows up on the air as "splatter": energy spilling into the adjacent frequencies on either side of your signal. PureSignal listens to what actually comes out of the amplifier, compares it to the clean signal you sent in, and then pre-distorts your outgoing transmit signal so that the amplifier's own distortion cancels it out. The result is a transmit signal with markedly lower intermodulation distortion (IMD) — tighter shoulders, less splatter, a better-behaved station.

PureSignal is available on Protocol-2 boards (ANAN-G2, G2 MkII, Orion-class) and on the Hermes-Lite 2 over Protocol-1. A few details differ by board, and those are called out below. Two single-ADC boards — the **ANAN-10E** and **ANAN-G2E** — have **experimental** PureSignal support that is not yet switched on; see the note at the end of this chapter.

> **SAFETY FIRST — PureSignal always starts OFF.** Zeus never arms PureSignal automatically. It is off at every startup, every connect, every session — regardless of how you left it last time. **You arm it by hand, each session, on purpose.** This is a deliberate safety rule, not a missing convenience. PureSignal works by feeding a sample of your amplifier's *output* back into a receive ADC; an inadvertently-armed feedback path on a high-power external amplifier can saturate that ADC before you have made any transmit decision. Arming is always your explicit action.

### Where to find it

There are two places to drive PureSignal:

- **The PS button** on the transport bar. This is the master arm switch. The small LED on it lights when PureSignal is armed. Hovering over it pops up a compact live status panel (convergence dial, calibration state, observed/HW peak, correction in dB) so you can keep an eye on PureSignal during transmit without leaving the panadapter.
- **Settings → PURESIGNAL** (also available as its own dockable panel). This is the full dashboard: the calibration hero strip with the convergence dial, the TX → PA → coupler → feedback signal-flow diagram, live peak meters, the mode controls, and the timing/hardware grids.

### Arming and disarming

Click the **PS** button to arm. Click it again to disarm. The button updates immediately and rolls back if the radio refuses the request.

When you arm PureSignal, Zeus also turns on **PS Monitor** for you (on boards that support it — see below), so you can watch the post-amplifier signal in the transmit panadapter while it corrects. Disarming PureSignal leaves the monitor where it is, so you can keep watching the trace if you want.

Arming or disarming mid-transmit is safe and no longer wedges the correction engine.

### How calibration works, and the convergence dial

PureSignal needs to *see* your signal across its full drive range to build a correction curve. Key down (MOX, TUNE, or the built-in two-tone) and it begins sampling. The big **convergence dial** shows how "hot" the feedback signal looks to the engine, on a 0–256 scale:

- **Below 128** — too quiet; the engine can't gather enough to fit a curve.
- **128–181** — the green zone; this is what you want.
- **Above 181** — too hot; the feedback ADC is near clipping.
- **At 256** — clipping; corrections will be garbage.

The status pill cycles through **idle → running → ready → correcting**. "Correcting" means a curve is loaded and being applied to live RF *right now*. "Ready" means a good curve is loaded but you're not currently transmitting — the curve persists between overs. The bottom-line indicator is the status row reading **CALIBRATION CONVERGED · −x.xx dB**: if you see that and the dial sits in the green, PureSignal is doing its job.

#### Auto, Single, Run now, Reset

| Control | What it does |
|---|---|
| **Auto** | Continuously refine the correction while you transmit. **Recommended** for normal operating. |
| **Single** | Run one calibration pass, then freeze the curve. Use it when you've finished dialling in and want to lock the result. |
| **Run now** | Trigger one Single pass — a quick way to force a fresh measurement. |
| **Reset** | Throw the current state away and start over. |

### Observed peak, HW peak, and Correction

The right-hand peaks column is where most setup happens:

- **Observed peak** — the largest transmit-envelope sample the engine has seen recently. A live number.
- **HW peak** — the *ceiling* you tell the engine to expect. The engine sorts every sample into amplitude bins relative to this value.
- **Correction** — how much the engine is currently massaging your I/Q to cancel distortion, in dB, with a short sparkline. **Flat is good** — it means the model is stable. Movement means conditions are changing.

**The one rule that matters: HW peak should sit just slightly above Observed peak — about 5% above.** Example: Observed 0.237, HW peak 0.25.

- If **HW peak is far above** Observed, the top amplitude bin never fills, the engine can't fit the top of the curve, and it sits stuck collecting samples forever — armed but producing no correction.
- If **HW peak is below** Observed, samples get dropped and the engine only fits the bottom of the curve — a poor correction.

A small `*` next to HW peak means you've moved it off the per-board default; the **Default** button puts it back. If PureSignal stays keyed for more than five seconds without converging, a banner appears telling you to set HW peak just above your observed peak — that is almost always the fix. (Don't chase HW peak for a stalled/clipped reading caused by something else; see the desktop-mode note below.)

### The feedback tap — internal vs external

PureSignal needs a sample of the amplifier's output. The **Internal coupler / External (bypass)** selector chooses where that sample comes from:

- **Internal coupler** — the directional coupler built into the radio. Simplest; use it when your radio has one and you're running its internal PA.
- **External (bypass)** — you've tapped the antenna line yourself (e.g. a DC6NY-style sampler) and fed it back into the radio's RX2 antenna jack. Use this when correcting an external linear amplifier. Aim for roughly **−15 to −18 dBm** at the RX input at full transmit.

> **Feedback-tap caution.** The external tap shares the RX2 input. Make sure your sampler/attenuator delivers a level in the target range *before* you transmit at full power — too much level clips the feedback ADC (and gives you bad corrections or none), and a missing/wrong pad can put far more RF into the receive path than it can take. Set the level conservatively, verify the dial sits in the green at low drive, then bring power up.

### Auto-attenuate and manual feedback attenuation

**Auto-attenuate** (Hardware card, on by default) watches the feedback level and automatically adjusts the transmit step attenuator to push the feedback back into the green window when it drifts. Leave it on — it keeps the dial healthy as drive and band change without you fiddling.

If you'd rather set it by hand, the **Feedback atten** control (in dB) is a manual alternative — it's available when Auto-attenuate is off. Dial it so Observed sits mid-scale; the value is then saved and restored per band so you don't redo it every session.

### Other controls

**Timing card** (defaults are correct for most radios):

- **TX delay** — keys the radio first, waits, *then* lets RF out, giving an external linear's transmit/receive relay time to finish switching before any RF arrives (prevents hot-switching). Default 0 ms; operators with external amps often use around 30 ms. Deliberately not applied in CW or digital modes.
- **MOX delay** — hold-off after key-down before sampling begins (lets PA bias and relays settle).
- **Cal delay** — gap between calibration passes (0 = back-to-back).
- **Amp delay** — compensation for propagation delay through your PA and filters. Don't touch unless you actually know your chain's group delay.

**Hardware card** (advanced — most operators never change these): **Ints / Spi** sets the correction-curve resolution (16/256 is the standard 0.5 dB resolution); **Relax phase tolerance** loosens the engine's quality bar — only enable it if the engine refuses to produce a curve on a weak or noisy PA, and be aware it raises the risk of accepting a bad curve.

**Two-tone test signal** — a built-in generator that injects two pure tones (you pick the offsets, e.g. 700 Hz and 1900 Hz) instead of mic audio. It's the standard way to make a clean, repeatable signal for checking IMD on a spectrum analyser or monitor receiver, and a convenient excitation for PureSignal to calibrate against. Keep magnitude around 0.49 per tone so the combined peak stays under full scale.

**PS Monitor** (Display card) shows the post-amplifier, corrected signal in the transmit panadapter so you can watch PureSignal work. It's hidden on the **Hermes-Lite 2**, which has no internal feedback-loopback display path. (HL2 still has PureSignal and an internal coupler — only the monitor display is unavailable.)

### Step-by-step: using PureSignal safely

1. Set your drive and band as you normally would, with PureSignal still **off**.
2. Open **Settings → PURESIGNAL**. Choose your feedback source — **Internal coupler** for the radio's own PA, or **External (bypass)** for an external linear (verify your sampler level first).
3. Select **Auto** mode and leave **Auto-attenuate** enabled.
4. Click **PS** to arm. The LED lights.
5. Key down with a known excitation — **two-tone** is ideal — and hold it for a few seconds.
6. Watch the dial and the **Observed peak**. Read off the Observed value, then set **HW peak** to about 5% above it (Tab/Enter to commit).
7. Within a second or two you should see the dial settle in the green and the status row read **CONVERGED · −x.xx dB**, with the Correction sparkline going flat.
8. Resume normal operating. PureSignal keeps refining in Auto mode. When you change band or drive significantly, glance at the dial — Auto-attenuate handles most of it, but re-check HW peak if the engine ever stalls.
9. Disarm with the **PS** button when you're done. It will be off again next session — arm it deliberately each time.

### A note on desktop vs. web mode

On the desktop build, PureSignal shares CPU with the on-screen panadapter/waterfall rendering. Under heavy local display load this can make corrections jittery — two-tone or voice can look dirtier than they should, and calibration can stall. If you see this on the desktop app, the cleanest workaround today is to run Zeus in **web mode** (backend on the radio's host, browser on another machine), where the display load lives on a separate machine and PureSignal runs unobstructed. This is a known architectural limitation, not a setting you need to adjust — and the "HW peak too low / Observed clipping" banner that can appear during a desktop stall is a symptom of that stall, not a real HW-peak problem, so don't chase the HW-peak number to clear it.

### Experimental: single-ADC boards (ANAN-10E and ANAN-G2E)

The **ANAN-10E (HermesII)** and **ANAN-G2E (HermesC10)** have only **one** receive ADC. PureSignal normally needs a second receiver to listen to the amplifier's output, which is why these boards have never offered it. This release adds an **experimental** approach that *time-multiplexes* the single ADC — briefly borrowing it for the feedback sample during transmit — so these radios can, in principle, run predistortion for the first time.

> **This is validated in simulation, not yet on real hardware — and it is OFF.** The single-ADC feedback path has been exercised end-to-end against Zeus's virtual-radio test harness (a software radio that speaks the real HPSDR wire protocol), where the feedback frames, ADC-overload protection, and correction loop all behave correctly. It has **not** been confirmed on an actual 10E or G2E. Because PureSignal drives real power amplifiers, this path ships **disabled behind a safety interlock** — there is no operator switch to turn it on, and PureSignal still always starts OFF on every board, every session, exactly as described at the top of this chapter.
>
> **We want your help.** If you own an ANAN-10E or ANAN-G2E and would like to help validate this on the air, please reach out through the [Zeus Discord or GitHub](https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus). We'll enable and bench it together, confirm the feedback levels are safe on your hardware, and only then make it available to everyone. Until that real-radio confirmation exists, treat single-ADC PureSignal as a preview, not a feature you can use yet.
