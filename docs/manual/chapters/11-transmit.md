## Transmitting: MOX, Drive, PA & TX Audio

This chapter covers everything between your voice (or a tuning carrier) and the RF leaving your antenna: keying the radio, setting power, telling Zeus how your amplifier behaves, shaping your transmit bandwidth, and dialing in clean, punchy audio. Read the safe-power notes at the end before you key into anything that matters.

### Going on the air: MOX and TUNE

Two buttons put you on the air, and they live together in the transmit controls.

- **MOX** — your manual transmit switch. Click it once to key the radio; the button turns red and reads **TX** while you are transmitting. Click again to drop back to receive. You can also hold the **spacebar** as a push-to-talk key — the MOX button lights up to match, so the on-screen state always tells the truth. MOX sends your processed microphone audio (SSB/AM/FM) or your keyed signal, depending on mode.
- **TUNE** — keys a single, steady tone (a carrier) for tuning an antenna tuner or amplifier. TUNE and MOX are mutually exclusive: turning one on turns the other off, so you can never accidentally key voice and a tune carrier at the same time.

A safety detail worth knowing: while TUNE is engaged, Zeus automatically holds your tune power down. The tune carrier is continuous and full-duty, so it stresses a final much harder than speech — keeping it modest protects your radio and amp.

> **External amplifier tip.** If you run a linear amp with a transmit/receive relay, set the **TX delay** control (in the PURESIGNAL → Timing panel) to a few milliseconds — operators have found around 30 ms ideal. Zeus then keys the radio, waits, and only then lets RF out, giving the relay time to finish switching so you never "hot-switch" it. The default is 0 ms, and it deliberately does not apply to CW or digital modes.

### CTUN (click-tune)

**CTUN** changes what a click on the panadapter does. With CTUN **off**, clicking recenters the display on the frequency you clicked — the classic "radio follows the dial." With CTUN **on**, the hardware tuning stays frozen and your crosshair roams across the visible spectrum instead; transmit still lands exactly on the dial frequency. CTUN is handy for working a pile-up or watching a fixed window while you tune around inside it. (Desktop and LAN browsers; hidden on narrow mobile layouts.)

### Setting power: the Drive and TUN sliders

Zeus has two power sliders, both reading **0–100 %**:

| Slider | Label | What it sets |
|--------|-------|--------------|
| **DRV** | DRV | Transmit drive for normal MOX (voice/CW/digital) |
| **TUN** | TUN | Drive for the TUNE carrier |

Both sliders show a live **target-watts readout** beside the percentage (for example `~35 W`) whenever you have told Zeus your rated PA output. That number is simply *Rated PA Output × slider %*, so 35 % of a 100 W radio reads "~35 W." It is a planning aid, not a measurement — your **forward-power meter** is the real number on the air. If Rated PA Output is set to 0 (raw drive-byte mode, described below), the watts readout disappears because watts have no meaning in that mode.

Both sliders stream to the radio live as you drag, so power tracks the thumb in real time during transmit. Your last drive and tune settings are remembered by the radio and survive a restart.

### PA Settings: teaching Zeus about your amplifier

Open the **PA Settings** panel to tell Zeus how your radio's power amplifier behaves. This is where the drive percentage gets translated into the actual signal level on the wire, so it is worth setting correctly once.

**Global section:**

- **PA Enabled** — master switch for the PA stage.
- **Rated PA Output (W)** — the wattage your 100 % slider targets. Zeus seeds this from the radio it detects: Hermes-Lite 2 = 5 W, Hermes-class boards = 10 W, ANAN / Orion / G2 = 100 W. Set it to **0** to fall back to **raw drive-byte mode**, where the slider drives the radio directly and the per-band PA Gain field is ignored.
- **Reset to <radio> defaults** — re-seeds the per-band gain figures and rated output to the factory values for your board. Your open-collector pin settings and per-band Disable-PA checkboxes are left alone. Remember to press **APPLY** to save.

**Per-band section** has three tabs: **OC TX**, **OC RX**, and (on Hermes-Lite 2 only) **AUTO N2ADR**. Each row is one HF band and carries:

- A **PA Gain / PA Output** slider (see the two models below).
- A **Dis PA** checkbox that forces drive to zero on that band — useful for a band you never transmit on.
- The **open-collector** pin bars (covered in the accessory-control chapter).

#### Two different power models — know which one your radio uses

This is the single most important thing in this chapter, because the same on-screen field means two completely different things depending on your radio.

**Most radios (Hermes, ANAN, Orion, ANAN-G2 / G2 MkII) use a decibel model.** The per-band field is labelled **PA Gain (dB)** and represents your amplifier's forward gain — how much louder the PA makes the radio's tiny drive signal on its way to the antenna (a G2-class board is roughly 48–51 dB on HF). Zeus combines this gain with the Rated PA Output watts to work out exactly how hard to drive the radio. Lower gain here means Zeus pushes more drive to reach the same output. Two facts keep you out of trouble:

- This is **not** a power trim — it describes the hardware, so leave it at the seeded value unless you know your amplifier differs.
- **Rated PA Output is a calibration figure, not a power cap.** On Hermes-class radios it should be **100 W even though the radio only makes 10 W.** The gain figures Zeus uses are calibrated against a 100 W reference; if you "correct" the rated output down to 10 W, the math asks the radio for far too little signal and you get roughly **1 watt out at full slider**. If a Hermes / ANAN-10 / Brick2 is making about 1 W at full TUNE, this is almost always the cause — hit **Reset to defaults**.

**Hermes-Lite 2 uses a percentage model instead.** On an HL2 the field is relabelled **PA Output (%)**, range 0–100, and it is **not decibels**. Here 100 % means full rated output with no attenuation, and lower values soft-cap a band (the stock HL2 PA is weaker at 6 m, so its 6 m default is about 38.8 %). This matches the way the HL2 community firmware actually drives the board; the older decibel approach silently capped an HL2 at roughly 20 % of its rated power.

> **HL2 owners upgrading from an early build:** if you had hand-tuned "gain" numbers from before this change, they are now read as percentages and will under-power you (a stored `40.5` now means 40.5 % output). The fix is one click: **Reset to Hermes Lite 2 defaults** in the PA Settings panel.

The **AUTO N2ADR** tab (HL2 only) is a read-only display showing which low-pass filter bank the HL2 firmware switches in for each band. It is informational — the firmware decides this, not you.

### TX bandpass filter

In the TX panel, the **FILTER** row sets your transmit bandwidth as a **CUSTOM** low/high edge pair in Hz. Type a low edge, a high edge, press Enter or click away, and Zeus applies it. The edges follow your mode automatically: SSB is one-sided (and follows USB/LSB), while AM/FM/DSB/SAM are symmetric around the carrier, so the low box is disabled and only the width matters. The readout to the right shows the active range.

Narrower (for example 300–2700 Hz) concentrates your power for DX and weak-signal work; wider (down to 40 Hz and up past 3 kHz) gives fuller, more natural "rag-chew" or eSSB audio — at the cost of bandwidth. Stay inside the bandwidth that is courteous and legal for the band you are on.

### Microphone gain

The **MIC** slider sets microphone input level, in decibels, over a range of **−40 to +10 dB** with a default of **0 dB** (unity). The negative half is the important half: browser microphones are often hot, and pushing a hot mic harder just produces splatter. If your transmit audio sounds distorted or your ALC is slammed, **turn the mic gain down**, not up. Watch the **MIC** meter while you talk — peaks around −10 to −6 dBFS on the loudest syllables is a healthy target. You can set it against the live meter before you key, since the gain persists across transmit on/off.

### TX Leveler

The **LVLR** slider sets the **Leveler maximum gain** — how much the leveler is allowed to lift quiet speech up toward a consistent level before the ALC steps in. Range **0–20 dB**, default **+5 dB**, which is the community-recommended SSB starting point. Higher values even out your audio more but push the ALC into harder limiting; if the LVLR meter rises far above your MIC peaks, you have it too aggressive. The leveler acts only during transmit.

### CFC — Continuous Frequency Compressor

The **CFC** panel is a 10-band, frequency-aware compressor for shaping and thickening your transmit audio. It is **off by default**, and turning it on with neutral settings is audibly transparent — so nothing changes until you choose to use it.

- The big graph shows two curves: **compression** (how hard each band is squeezed, dragged downward) and **post-gain** (a per-band ±dB trim, centred). Drag the knots, or type exact values into the numeric strip below.
- **Pre-comp** and **Pre-peq** are master input trims feeding the band stages.
- Preset chips give instant starting points: **Flat**, **Voice**, **Studio**, **ESSB**, and **DX**. You can also **Save** your own named CFC presets and recall them from the dropdown.

CFC is processed inside the radio's DSP and is separate from the plugin-based Audio Suite chain — the Audio Suite master bypass does not touch it.

### TX Audio Profiles — save your ESSB / DX / Studio setups

The **TX Audio Profile** bar (shown in the TX Fidelity panel and the TX Audio Suite window — both stay in sync) lets you snapshot your entire transmit-audio setup under a name and recall it instantly. A saved profile captures your **mic gain, leveler, CFC, TX filter, audio chain, and plugin settings** together.

- The **dropdown** lists your saved profiles; selecting one applies it live.
- **+** captures the current live settings as a **new** named profile.
- **Save** overwrites the currently selected profile in place (with nothing selected, it prompts for a new name).
- **Delete** removes the selected profile — your live settings are untouched.

This is the practical way to keep, say, a tight **DX** voice for chasing weak ones and a wide **eSSB** profile for the local net, and flip between them in one click. (Profile save/recall is a desktop / LAN feature.)

### Safe-power guidance

- **Set PA Settings correctly first.** Rated PA Output is a *calibration* value (100 W on Hermes-class boards), not your radio's real ceiling. Use **Reset to defaults** if power looks wrong before hand-tweaking anything.
- **Tune gently.** TUNE is a full-duty carrier; keep the TUN slider low (Zeus already caps it) and tune briefly. Never tune into an antenna or amplifier you have not confirmed is connected and matched.
- **Sequence your amp.** With an external linear, set the TX delay so the radio's T/R relay finishes switching before RF appears.
- **Watch SWR.** Zeus protects you with automatic SWR trips — roughly 2.5:1 during MOX and a more forgiving 6:1 during TUNE, each with a brief grace period. A trip drops you to receive and raises an on-screen alert. Treat a trip as a real problem to fix (antenna, feedline, tuner), not a nuisance to work around.
- **Clean audio beats loud audio.** If you hear distortion or see wide splatter on the panadapter, back the **mic gain** down and ease the **leveler** before reaching for more drive. Reading the MIC → LVLR → ALC → OUT meters top-to-bottom tells you exactly where the chain is overdriven.
