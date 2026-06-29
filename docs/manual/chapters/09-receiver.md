## Receiver Controls (AF, AGC, Knee, Noise Reduction, Squelch)

This chapter covers everything that shapes what you actually hear: how loud the audio is, how the receiver rides the gain as signals come and go, how it protects itself from overload, and the family of noise-reduction and "smart helper" tools. Most of these controls live on the receiver control strip across the top of the window; the fuller, labelled versions live in the DSP settings panel, and edits in one place track the other.

### AF Gain (Volume)

The **AF** slider is your master receive volume. It runs from **−50 dB to +20 dB**, with **0 dB at center** being the fresh, normal-listening level. Drag it left to quiet down, right for more punch. The audio responds in real time as you drag, so you can ride it by ear.

A couple of practical notes:

- AF is applied inside the radio's audio engine, not in your computer's soundcard, so the full range is real gain — you are not just trimming the very top of the soundcard's volume.
- If you run **two receivers** (RX2), the AF slider follows whichever receiver has focus. Click into RX2 and the slider now sets RX2's volume; the two keep independent levels.

Safe default: leave AF at 0 dB and adjust from there. If you find you are always pushing it past +10 dB, look first at attenuation and preamp before cranking AF.

### AGC — Automatic Gain Control

AGC is what keeps a booming local station and a whisper-weak DX signal both at a comfortable, roughly even volume. Zeus gives you the same AGC engine and choices Thetis uses.

**AGC mode** — a small dropdown (the labelled buttons in DSP settings) offers:

| Mode | Use it for |
|------|-----------|
| **Fixed** | No automatic action — a constant gain you set yourself (Fixed Gain field). |
| **Long** | Very slow recovery — relaxed SSB rag-chewing, steady signals. |
| **Slow** | Slow recovery — general SSB listening. A good everyday choice. |
| **Med** | Medium — a balanced default, good for mixed conditions. |
| **Fast** | Quick recovery — CW and fast-changing signals. |
| **Custom** | You set the timing yourself (see below). |

Pick the mode that matches what you're listening to. **Med** or **Slow** is the comfortable starting point for voice; **Fast** suits CW.

**Custom mode tunables** (revealed by the "⋯" button next to the mode, or always shown in DSP settings):

- **Slope** — how the gain rolls off near the top.
- **Decay (ms)** — how quickly gain recovers after a strong signal passes.
- **Hang (ms)** — how long the receiver holds gain steady before recovering.
- **Hang Thresh (%)** — the signal level below which the hang timer applies.

**Fixed mode** exposes a single **Fixed Gain (dB)** value; the AGC does nothing automatic and simply runs at that gain.

#### AGC-T (Maximum Gain) — the level cap

The **AGC-T** slider sets the *maximum gain* the AGC is allowed to apply — its ceiling, in dB (0–120). The number to the right shows the gain currently in effect. Think of AGC-T as "how loud the receiver is allowed to make weak signals." Raise it to pull faint signals up out of the noise; lower it if the band noise itself is getting amplified to an annoying level.

One thing to expect: a maximum-gain cap is non-linear by nature. Across much of the slider's travel you may hear little change, and then over a narrow zone the audio shifts sharply. That is normal behaviour for this control — for smooth, signal-relative shaping, use the Knee (below) instead.

**Auto-AGC.** Click the **AGC-T** button to toggle Auto-AGC on and off. With it on, Zeus tracks the panadapter's noise floor and sets the maximum gain for you; the slider goes inactive and the readout shows the gain the system has chosen so you can watch it ramp. This is a "set it and forget it" option that keeps AGC sensible as you move around the band. When you want manual control, turn Auto-AGC off and the slider becomes live again.

#### Knee (AGC Threshold) — smooth, signal-relative shaping

> **Note:** The Knee control is a newer addition still under maintainer review and may not be present in every build. Where it appears, it sits right beside AGC-T.

Where AGC-T sets a *ceiling*, the **Knee** sets the *floor* — the signal level (in dBm) above which AGC begins working. You set it **just above the band noise floor**. Below the knee, weak noise is left alone; above it, AGC engages smoothly. This is the control that gives the predictable, gradual feel operators expect, and it is independent of Auto-AGC — the knee works whether Auto-AGC is on or off.

- The **Knee label is a toggle.** Click it to *engage* the knee; it lights up when active. The numeric readout shows "—" until you have actually set a value.
- Drag the slider to position the threshold. Set it a few dB above where the noise floor sits on your panadapter.
- Click the lit **Knee** label again to *disengage*. This honestly returns AGC to the radio's normal per-mode default — it does not leave a stale setting behind.

Practical use: turn on the knee, then nudge it up until the background hiss settles but signals still pop through cleanly. It is the smoother companion to the AGC-T cap.

### Attenuator, Auto-ATT, and ADC Overload Protection

The receiver's front end can only handle so much signal before the analog-to-digital converter (ADC) overloads and the whole band gets ugly. The attenuator is your defense.

**S-ATT (Step Attenuator)** — a slider from **0 to 31 dB**. More attenuation means a cleaner, less-overloaded front end at the cost of hearing the very weakest signals. On a quiet band, leave it at 0. On a crowded, strong-signal band (or with a big antenna), dial in attenuation until the noise floor drops and stays steady. The number on the right is the attenuation actually in effect.

**Auto-ATT** — click the **S-ATT** button to let Zeus manage attenuation automatically. When the ADC starts to overload, it adds attenuation; as the band calms, it backs off. The button turns red and the readout turns yellow when an overload is detected, so you get a clear visual warning. With Auto-ATT on, the slider goes inactive and shows the level the system has chosen.

**ADC Overload Protection** (in DSP settings) is the engine behind Auto-ATT, and you can tune how aggressively it reacts:

- **Enabled / Disabled** toggle, with a live status of **OFF / ARMED / PROTECT**.
- **Attack (ms)** and **Release (ms)** — how fast it adds attenuation when overload hits, and how slowly it lets go afterward.
- **Attack Step / Release Step (dB)** — how big each adjustment is.
- **Max Offset (dB)** — the most extra attenuation it is allowed to stack on top of your baseline (0–31 dB).
- **Warn Level** — how sensitive the overload warning is.
- **P2 Magnitude Limit** — a soft limit used on Protocol-2 radios (ANAN-class).

The panel also shows a live **RX Fit** readout with a plain-language recommendation ("optimize" or "protect"), ADC headroom, and the current WDSP AGC gain — handy for seeing at a glance whether your front end is set well. A **Reset** button restores the defaults.

Safe default: leave ADC Overload Protection enabled and let Auto-ATT ride. Reach for the manual S-ATT slider only when you want deliberate control.

### Preamp

The **PRE** button toggles the radio's hardware preamp for a bit of extra front-end gain on quiet bands or weak antennas. It lights up when on.

Board note: the **Hermes-Lite 2** has no hardware preamp — its gain is handled through the attenuator path — so the PRE button is hidden entirely on HL2. If you don't see it, that's why.

### Noise Reduction, Noise Blanker, and Auto-Notch

A row of buttons handles interference. Each cycles or toggles, and lights up when active:

- **NB** — Noise Blanker, cycles **Off → NB1 → NB2**. These knock down impulse noise (ignition, power-line ticks). NB1 and NB2 are two different time-domain blankers; try both and keep the one that helps.
- **NR** — Noise Reduction, cycles **Off → NR → NR2 → NR4**. NR (NR1) is a time-domain reducer, NR2 is spectral, and NR4 is a newer spectral reducer. Higher numbers are generally more aggressive. For each active NR mode a **gear (⚙) button** appears to open its tunables.
- **ANF** — adaptive auto-notch. Automatically nulls steady carriers/heterodynes (that whistle from a tuner-upper). Great on a phone band.
- **SNB** — spectral noise blanker, another approach to impulse noise.
- **NBP** — notch-filter auto-notch, an alternative auto-notch path.

Practical use: start with everything off, then add **NR2** for steady hiss and **ANF** for whistles. Back off if voices start to sound watery or hollow — noise reduction always trades some naturalness for quiet.

### Squelch

**SQL** mutes the audio until a signal is strong enough to be worth hearing — invaluable on FM and for leaving a frequency monitored without constant hiss.

- **SQL** button — turns squelch on/off.
- **FIX / DYN** button — switches between **Fixed** squelch (you set the threshold) and **Dynamic** squelch, which tracks the band noise floor automatically. Greyed out while SQL is OFF.
- **Threshold slider** (0–100) — in Fixed mode, higher = tighter (harder for a signal to open it). In DYN mode the slider is inactive and the readout shows **AUTO**. Also greyed out while SQL is OFF.

Squelch is **mode-aware**: the radio automatically routes it to the right stage for what you're listening to — a voice/syllabic gate for SSB/CW, a carrier threshold for AM/SAM, and a noise-gate for FM. The DSP settings panel shows which stage is active and includes a separate **Sensitivity** control for the fixed-squelch path.

Safe default: leave SQL off for SSB/CW where you want to hear everything; turn it on for FM and set the threshold just above the open-channel noise.

### Smart NR and Signal Intelligence (Automatic Helpers)

Zeus adds two optional "assistant" panels (in DSP settings) that watch the band and either suggest or apply settings for you.

**Smart NR** automates noise reduction and blanking. It has three modes:

- **Manual** — off; you drive NR yourself.
- **Suggest** — it analyzes the band and proposes an NR profile, showing its reasoning (measured SNR, band occupancy, impulsive content). An **Apply** button lets you accept the suggestion.
- **Auto** — it applies the chosen profile for you as conditions change.

Toggles let you allow it to drive the **Blanker** and the **Notch Helpers**, and sliders set **Aggression**, **Max NB Threshold**, and **Dwell** (how many frames it waits before reacting). It also surfaces a DSP-capability note if your build can't run a recommended mode.

**Signal Intelligence** is a visualization-and-enhancement layer. It offers profiles (**Balanced, DX, CW, Digital, Voice, Contest**) plus an **Auto Profile** that picks one to match the band. Its switches include **Signal Pop** (lifts real signals out of the display noise), **Snap** (snaps your tuning to a nearby signal), **Visual AGC** (steadies the display), and **Spike Reject** (suppresses display spikes). A large set of fine-tuning sliders lets you shape the panadapter/waterfall look. These controls affect detection, tuning aids, and how signals are *displayed* — they make weak signals easier to see and click, on top of the audio-path tools above.

Safe default: leave Smart NR on **Suggest** and Signal Intelligence on a **Balanced** profile until you're comfortable, then experiment. Nothing here changes your transmit; it's all about hearing and seeing the band more clearly.

### Multiple receivers — running RX1 through RX8

A second receiver (RX2) has long been part of Zeus. On **Protocol-2 radios** (ANAN-G2 class) Zeus now goes much further: it can run **up to eight genuine hardware receivers — RX1 through RX8** — at the same time. Each is a real, independent receiver with its own band, tuning, filter, and audio — not just another view of the same signal. You might park RX1 on a 20 m phone net, hunt DX on RX2, watch a 40 m FT8 sub-band on RX3, and so on.

How many you can run depends on your radio and the sample rate (wider spectrum costs receiver slots), so Zeus offers the receiver count your board actually supports.

**Turning receivers on.** The **MULTI RX** control adds and removes receivers. Each one you enable gets its own panadapter/waterfall pane and its own slot in the controls. Turning MULTI RX off collapses cleanly back to a single RX1 — nothing is left stranded.

**Focus.** The receiver controls (AF, AGC, mode, filter, and so on) act on whichever receiver currently has **focus**. Click into a pane to give it focus; the strip then drives that receiver. This is how each receiver keeps its own volume, mode, and filter independently.

**Ganged band selection.** When you want several receivers to move together — say, to sweep a contest across three panes — **gang** them: select the receivers you want linked and a band change moves all of them at once. Leave a receiver out of the gang to let it sit on its own band.

**A consistent picture across panes.** With many waterfalls stacked, Zeus keeps them readable: each receiver carries its own **dB scale** with **noise-floor normalization**, so a quiet pane and a busy pane line up visually instead of one washing out. Per-receiver **filter splits and colors** keep it clear which controls and which trace belong to which receiver.

**Transmit.** You can transmit on any receiver, not just RX1 — the focused receiver's VFO is what goes out when you key up. As always, confirm which receiver has focus before you transmit so you key up on the frequency you intend.

