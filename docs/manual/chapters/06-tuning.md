## Tuning, VFOs, Bands & Memories

Everything you do to land on a frequency in Zeus happens in three small, closely related controls: the **VFO display**, the **Band** picker, and the **Step** picker. This chapter walks through each one, plus the extras you'll reach for on the air — split operation, click-tune (CTUN), band memories, and a one-button frequency calibration.

### The VFO display

The VFO display is the big numeric readout of your tuned frequency. Zeus shows it as a row of eight digits grouped the way a ham reads them — tens-of-MHz down to single Hz, with separators so `14.210.000` reads as 14.210 MHz at a glance. The label underneath tells you which VFO you're looking at (VFO A or VFO B) and reminds you of the two ways to change it.

There are three ways to move the frequency:

- **Scroll a digit.** Hover your mouse over any single digit and roll the scroll wheel. Wheel up raises that digit's place, wheel down lowers it — so put the pointer over the kHz digit to move in 1 kHz steps, over the 10 Hz digit to nudge by 10 Hz, and so on. The display tracks your wheel instantly; Zeus only sends the final resting frequency to the radio when you stop, so a fast spin won't flood the link. The pointer shows an up/down cursor over a tunable digit.
- **Type it in.** Click anywhere on the digits and the display turns into a text field. Type the frequency **in kilohertz** — for example `14200` for 14.200 MHz, or `14200.5` for 14.200500 MHz. A comma works in place of the decimal point for European keyboards. Press **Enter** to commit or **Esc** to cancel. Out-of-range or nonsense entries are simply ignored. The radio accepts anything from 0 up to 60 MHz here; whether your board can actually receive or transmit there is a separate matter.
- **Click the panadapter.** Clicking on the spectrum display tunes you there. Exactly how that behaves depends on whether CTUN is on — see *Click-tune (CTUN)* below.

### Two receivers: VFO A and VFO B

Zeus has two VFOs. **VFO A** is your main receiver (RX1) and is always active. **VFO B** belongs to the second receiver (RX2) and becomes live when you turn RX2 on.

In the VFO panel you'll find a command strip with these controls:

| Control | What it does |
|---|---|
| **RX2** | Turns the second receiver on or off. Enabling it also moves your "focus" to VFO B so your wheel and clicks act on it. |
| **A>B** | Copies VFO A's frequency to VFO B. |
| **B>A** | Copies VFO B back to VFO A (available only when RX2 is on). |
| **Swap** | Exchanges the A and B frequencies. |
| **AF slider** | Sets RX2's audio level, from −30 dB up to +12 dB. Only active when RX2 is on. |

Each VFO sits in its own lane. Click a lane (or its **A**/**B** focus key) to point your tuning controls at that receiver. The lane also shows the current ham band next to the frequency, and is tinted with that receiver's panadapter trace color so you can tell the two apart at a glance.

### Choosing which VFO transmits — and split operation

Each VFO lane has a small **TX** button. Whichever one is lit is the VFO your radio will transmit on. This is how you run **split**: receive on one VFO and transmit on the other.

To work a station listening up:

1. Turn on **RX2** so VFO B is live.
2. Tune VFO A to the DX station you're hearing.
3. Tune VFO B to the station's listening (transmit) frequency.
4. Click the **TX** button on the VFO B lane.

Now you listen on A and transmit on B. Use **A>B** first if you want B to start from where A is and then nudge it up. To go back to simplex, select TX on the VFO you're listening to. Zeus does not have a separate "Split" button or a classic RIT/XIT offset dial — choosing the TX VFO is the split mechanism, and it's both simpler and harder to get wrong.

### Tuning step

The **Step** picker sets how far one notch of coarse tuning moves you. The available steps are:

> 1 Hz · 10 Hz · 50 Hz · 100 Hz · 500 Hz · 1 kHz · 5 kHz

The highlighted button is the active step. There's no huge-step option on purpose — anything bigger than 5 kHz is one Band-button click away. Your chosen step survives a restart, so Zeus remembers how you like to tune.

A practical habit: leave it on **100 Hz or 10 Hz** for casual SSB tuning, drop to **1 Hz or 10 Hz** for zero-beating a CW signal, and bump to **1 kHz** when scanning across a busy band. Remember the wheel-over-a-digit trick on the VFO display is always available regardless of the Step setting — the digit you point at chooses the size of that move directly.

### Band selection

The **Band** picker is a row of buttons for the HF bands: **160 · 80 · 60 · 40 · 30 · 20 · 17 · 15 · 12 · 11 · 10 · 6** meters. (**11 m** is the CB / 27 MHz segment, provided for export/freeband operation where you're licensed for it, and **6 m** reaches up to 54 MHz — both new in this release.) The button for the band you're currently on is highlighted. (On a narrow window or phone the buttons collapse into a dropdown.)

Click a band button and Zeus jumps you to that band. Where it lands is the clever part — it uses your **band memory** (next section). The first time you visit a band it drops you on a sensible default near the popular activity in that band's segment.

There is also a **Band Favorites** picker in the top control strip — three quick-access band slots plus a "⋯" dropdown of every band. You can **drag** any band button (or a Step button) onto a favorite slot to pin the ones you use most, so they're always one click away.

### Band memories — your favorites, saved automatically

You don't have to manually save band memories in Zeus — it does it for you. As you tune around and change mode on a band, Zeus quietly remembers the last frequency and mode you were using **on each band**. The next time you click that band's button, it puts you right back where you left off, in the right mode.

How it behaves:

- Memory is stored **per band** — one remembered spot for each band from 160 m through 6 m (including 11 m).
- It captures both **frequency and mode** (e.g. 14.074 MHz in DIGU on 20 m, 7.180 MHz in LSB on 40 m).
- The save is debounced, so spinning the dial doesn't constantly hammer the radio; it settles a moment after you stop.
- Memories are kept on the server and **persist across restarts**, so your favorite spots are still there next session.
- If you've enabled RX2, the band memory follows whichever receiver currently has focus — so VFO B builds its own per-band history too.

In short: just operate normally, and your band buttons turn into a personal bandplan over time.

### Click-tune (CTUN)

**CTUN** (click-tune, also called centred tuning) is a toggle button on the control strip that changes what happens when you click or drag on the panadapter.

- **CTUN off (the classic feel):** clicking the spectrum recentres the display on the frequency you clicked — the radio "follows the dial" and the display re-centres around your new frequency.
- **CTUN on:** the display window stays put and your tuned frequency roams **within** it. The receiver's hardware tuning is frozen in place, and a crosshair marks where you are inside the frozen window. This lets you pick signals off a fixed slice of spectrum without the whole waterfall jumping every time you click.

Either way, **transmit always lands on your dial frequency** — keying down retunes the radio to where the VFO actually sits, so CTUN never causes you to transmit somewhere unexpected. CTUN is a desktop/spectrum-display feature (the button is hidden on mobile layouts). Try it when you're chasing several signals inside one pile-up; turn it off for normal band-cruising.

### Frequency calibration

Every radio's reference crystal drifts a little, which can leave your displayed frequency a few hertz off from reality. Zeus corrects this with a one-button **Frequency Calibration**, found under **Settings → Calibration**.

It works by listening to **WWV on 10.000 MHz** — the U.S. time-and-frequency standard station broadcast around the clock from Fort Collins, Colorado:

1. Connect your radio.
2. Open **Settings → Calibration** and click **Calibrate**.
3. Zeus tunes to 10 MHz, measures how far WWV lands off the mark on the panadapter, and computes a correction.
4. Your VFO, mode, filter, and zoom are all restored automatically when it finishes.

The card then shows your current correction as both an offset (e.g. "+1.2 Hz @ 10 MHz") and in parts-per-million. The correction is **stored per radio**, so each rig you connect keeps its own value, and it's reapplied automatically every session — you only need to run it once (or occasionally, to re-check). A **Reset** button clears it back to factory if you ever want to start over.

Two things to know:

- The correction is applied purely in software at the moment Zeus sends the frequency to the radio; it does **not** touch any clock or sample-rate register on the hardware, and it's clamped to a sane ±100 ppm.
- Calibration needs to actually **hear** WWV. If propagation to Colorado is poor from your location, the procedure reports "no signal" and leaves your existing setting untouched — nothing is changed for the worse. Operators far from North America may need to wait for a good 10 MHz opening. (Support for other reference frequencies is planned but not yet available.)

A good first-session checklist: connect, run frequency calibration once on a day with decent propagation, set your tuning step to taste, and let band memory build itself as you operate.
