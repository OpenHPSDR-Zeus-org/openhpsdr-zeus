## The Panadapter & Waterfall

The panadapter and waterfall are the heart of Zeus — your window onto the radio spectrum. The **panadapter** is the live spectrum trace across the top: frequency runs left-to-right, signal strength runs bottom-to-top. The **waterfall** sits below it: the same spectrum, but scrolling downward over time so you can watch a band's history paint itself row by row. Together they let you *see* the band before you ever turn the dial — find the active spot, judge a signal's width and strength, and tune onto it with a click.

### Reading the display

Each spectrum tile carries a small label in the top-left corner telling you which receiver you're looking at and the exact tuned frequency, for example `RX1 · VFO A · 14.074000`. If you run a second receiver, its tile reads `RX2 · VFO B`, and when both halves share one stitched view the focused half is tagged `FOCUS`.

Down the **left edge** is the dB amplitude scale — labelled tick marks every 10 dB. The further up a signal climbs, the stronger it is. The center vertical line marks where the radio is tuned; your filter passband is shaded around it and stays locked to that center line as you tune.

### Tuning by clicking and dragging

You can tune from either the panadapter or the waterfall — both respond identically, so use whichever you find easier to read.

- **Click** anywhere on the spectrum to jump the dial there.
- **Drag** left or right to pan the band smoothly under a stationary cursor.
- **Scroll wheel** fine-tunes by your chosen tuning step (set with the tuning-step control in the toolbar — the wheel and the arrow keys both follow it, so they feel the same).

Ordinary click and drag tuning snaps to a friendly **500 Hz grid** so you land on round frequencies. Typed-in frequencies and band-preset buttons bypass that grid and go exactly where you ask.

**Zoom** narrows or widens how much spectrum you see. Hold **Shift** and scroll the wheel: forward zooms *out* (wider view), back zooms *in* (closer look at one signal). On a touchscreen, pinch with two fingers. Zoom runs in steps from **1× (widest) to 32× (closest)**; the trace and waterfall scale together so they never disagree mid-zoom.

> **Tip:** Hold **Alt** and scroll (or Alt-drag) to pan and zoom the background world map instead of the spectrum, when the map is open — handy for chasing DX without leaving the spectrum view.

### Smooth tuning feel

In earlier versions, spinning the dial made the spectrum jump in visible steps. Zeus now **animates the view smoothly** to the new center frequency. When you make a big jump the receiver "catches up" quickly so the display doesn't smear, and the filter passband and the dial marker stay pinned to the center line the whole time. The motion is sub-pixel-smooth and the trace and waterfall move in perfect lock-step. Tuning by CAT control, a band button, or typed entry from elsewhere also glides the display to the new spot rather than snapping. It simply feels right.

### CTUN — click-to-tune

The **CTUN** button (in the spectrum toolbar) changes what a click does. This control is desktop-only.

- **CTUN off (default behaviour):** clicking re-centers the display on the frequency you clicked — the classic "radio follows the dial" feel. The whole spectrum slides so your new frequency sits in the middle.
- **CTUN on:** the spectrum view *freezes* and your dial roams across it instead. Click or drag and the tuning marker moves off-center while the band picture holds still. This is ideal when you're working a busy stretch of band and want to hop between several signals without the display jumping around each time. Transmit still lands on the dial — the radio retunes the moment you key up.

### The dB / amplitude scale and reference level

The vertical scale sets how strong a signal has to be to show up and how much of the noise floor you see. There are two ways to drive it.

**Drag the scale directly.** Press and drag *vertically* on the dB scale column at the left edge of the panadapter. The trace slides with your finger — drag down to push signals lower in the window (revealing stronger signals above), drag up to lift the noise floor into view. Both ends of the range move together, so you're shifting the whole window, not stretching it.

**Auto vs. Fixed range.** The **dB: AUTO / dB: FIXED** button toggles how the reference level is chosen:

| Mode | What it does | When to use |
|------|--------------|-------------|
| **AUTO** | Continuously tracks the live spectrum (5th/95th-percentile of recent samples) so the trace fills the window no matter the band conditions | Casual operating, band-scanning, noisy or rapidly changing conditions |
| **FIXED** | Holds a manual window (default **−120 to −30 dBFS**) that never moves on its own | Comparing signal levels over time, weak-signal work where you want a steady reference |

Any time you manually edit the range (by dragging the scale or typing values), Zeus switches to **Fixed** so your chosen window holds.

For precise control, open **Settings → Spectrum Scale**. There you'll find separate, persisted dB windows for four cases, each with Min/Max boxes and a Reset:

- **RX Panadapter** — the trace window while receiving
- **TX Panadapter** — used automatically while you're keyed (MOX or TUNE), so watching your transmit signal never disturbs your receive noise-floor view
- **RX Waterfall** and **TX Waterfall** — the color-map windows for the waterfall rows in each state

A minimum span of 20 dB is enforced so the window can never collapse. All four survive a restart. **Reset All** returns everything to factory defaults.

### Spectrum color and signal-strength shading

The panadapter trace renders in a single amber hue by default — the classic lit-instrument look, with brightness following signal strength. You can pick your own RX trace color in the display settings; the choice persists across restarts. (On the waterfall, the dB window from the Spectrum Scale panel maps weak-to-strong onto the color gradient.)

### Signal Pop — weak-signal enhancement

The **POP** button turns on Zeus's signal-enhancement view. With Pop on, Zeus estimates the noise floor in each frequency bin and subtracts it, so weak signals that were buried near the grass stand up brightly while the background flattens out. It's a receive-only aid — Pop automatically steps aside while you transmit so your TX trace stays on its true calibrated scale, and resumes the instant you unkey.

Use it to pull faint CW or digital signals out of the noise on a quiet band. Turn it off when you want an honest, unprocessed look at relative signal levels. The enhancement strength and its underlying parameters can be tuned in the signal-enhance settings; the panadapter repaints immediately when you change them.

### Snap-to-signal and peak markers

The **SNAP** button makes clicks intelligent. With Snap on, clicking near a signal tunes *exactly onto its carrier* rather than the nearest 500 Hz grid point — and it's mode-aware, so it lands where you'd actually want to listen. While Snap is engaged, short **peak-marker ticks** rise from the noise floor at every detected carrier, so you can see in advance exactly where a click will land. The brighter the tick, the stronger that signal stands above the noise.

Snap reaches out about 80 pixels from your cursor; click farther than that — out in empty spectrum — and the click tunes normally to where you clicked. A bit of built-in hysteresis keeps the target from flickering between two closely spaced signals. On the main receiver, a snap onto a live signal also arms a gentle self-correcting lock so you stay centered as the signal drifts.

### Licence-class band overlay

If you have a band plan loaded (Settings → **Band Plan** → pick your country / licence
class), the panadapter draws a translucent strip along the bottom showing every band
segment you can see, each tinted by what it means for your current mode:

- **Green** — you're licensed here and your current mode is permitted.
- **Amber** — amateur allocation, but your current mode is *not* permitted in this
  segment (for example, SSB tuned into the CW-only sub-band). Move dial *or* change
  mode to put yourself back in the green.
- **Red** — not an amateur allocation at all (the gap between bands, or a SWL /
  broadcast segment your plan covers).

Each segment shows a small label along the bottom — `80M Phone`, `40M CW/Digital`,
`30M CW/Digital`, and so on — so you can read the band layout at a glance.

The overlay is on by default. Toggle it from Settings → Band Plan → **Panadapter
overlay** if you'd rather have an unobstructed view.

### Audible band-edge alert

The same band plan can play a short two-tone beep when you tune past the edge of
your privileges — handy if you're sweeping a band quickly and the dial drifts past
the General phone edge into the Extra-only sub-band. The beep is local to your
browser (it doesn't go on the air) and won't fire faster than about twice a second
even if you sweep across several gaps. Toggle from Settings → Band Plan → **Tone at
band edge**.

### Notch — masking interference

The **NOTCH** button arms manual notching. Once armed, drag across an interfering signal — a birdie, carrier, or burst of EMF — on the spectrum, and Zeus drops a notch over that frequency band. A quick click (or a very narrow drag) drops a default-width notch right on the carrier under the cursor. A small **✕** button appears showing how many notches are active; click it to clear them all at once.

### The panadapter / waterfall split

The two surfaces share one vertical stack, and you control how the height is divided. **Drag the divider** between the panadapter and the waterfall to give one more room than the other. Out of the box the waterfall gets the larger share (about 60% of the height). Your chosen split is remembered between sessions in the same browser, and a split you set on the desktop layout follows you to the mobile layout and back. You can drag it anywhere from a thin sliver up to about 85% for either surface.

### Practical tips and safe defaults

- **Starting out?** Leave the dB scale on **AUTO** and the split near default. The display will keep itself readable while you learn the band.
- **Working weak signals?** Switch to **FIXED** dB range for a steady reference, turn on **POP** to lift the weak ones, and turn on **SNAP** so clicks land dead-on the carrier.
- **Busy band, many signals?** Turn on **CTUN** so the display holds still while you hop between stations.
- **Watching your own transmit?** You don't need to touch anything — Zeus automatically swaps to the separate TX dB window the moment you key, then restores your receive view on unkey.
- If a click "feels backwards" or the waterfall seems to scroll the wrong way, it's almost always a tuning-direction expectation rather than a fault — the spectrum frequency axis always runs low-on-the-left, high-on-the-right.
