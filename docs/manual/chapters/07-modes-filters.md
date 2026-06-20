## Modes & Filters

Choosing the right mode and shaping the receive passband around it are the two most basic things you do on any radio. Zeus splits this work across three small panels — **Mode**, **Bandwidth Filter**, and **Filter Presets** — backed by the live passband rectangle you can grab and drag right on the panadapter. This chapter walks through each one, the manual notch filter, and the DDC sample-rate (spectrum width) control.

### Receive modes

The **Mode** panel is a single row of buttons. Tap one to put the active receiver into that mode; the selected button highlights. On a narrow tile the row wraps onto a second line, and on a phone-sized screen it collapses into a drop-down.

Zeus offers the full set of operating modes:

| Button | Mode | Notes |
|--------|------|-------|
| **LSB** | Lower sideband | Voice on 160/80/40 m. Passband sits below the carrier. |
| **USB** | Upper sideband | Voice on 20 m and up. Passband sits above the carrier. |
| **CWL** | CW, lower | Morse, passband below the dial. Centered on the 600 Hz CW pitch. |
| **CWU** | CW, upper | Morse, passband above the dial. |
| **AM** | Amplitude modulation | Symmetric passband around the carrier. |
| **SAM** | Synchronous AM | Like AM but the radio locks to the carrier for steadier audio on fading signals. Uses the same filter widths as AM. |
| **DSB** | Double sideband | Both sidebands, no carrier suppression. |
| **FM** | Frequency modulation | Wide-band voice (repeaters, 10 m FM). FM has no filter preset table — the deviation, not a passband chip, sets its bandwidth. |
| **DIGL / DIGU** | Digital, lower / upper | For FT8, RTTY, PSK and other data modes. Symmetric narrow passband centered on the dial. |

A handy detail: **each mode remembers its own filter.** Switch from AM down to SSB and back, and Zeus recalls the last width you used in each mode rather than forcing you to re-pick it. The mode you choose is also remembered per band, so returning to a band brings back the way you last had it set up.

> **Tip — pin your favourites.** You can drag any mode button onto a toolbar favourite slot to keep it one click away, exactly like band and step favourites.

When a second receiver (RX2) is enabled, the Mode panel acts on whichever receiver currently has focus, so set focus first if you mean to change RX2.

### The filter presets — F1…F10 and the two variables

The **Filter Presets** panel holds the classic filter chip grid: ten fixed presets (**F1** through **F10**) plus two variable slots, **VAR1** and **VAR2**. The fixed presets are the long-standing default widths inherited from Thetis, laid out for the active mode and sorted narrow-to-wide so the labels read naturally from left to right. For SSB they run from a roomy 5.0 kHz down to a tight 1.0 kHz; for CW they step from 1.0 kHz down to a razor-thin 25 Hz around the pitch; AM offers up to 20 kHz of audio.

Click any chip to load that width instantly — the passband rectangle on the panadapter repaints to match, and so does the mini-pan in the Bandwidth Filter panel, because all of these views share one filter state.

Below the chips is a **Custom** row with **Lo** and **Hi** entry fields in Hz. Type a number and press Enter (or click away) to commit it. Two rules keep you out of trouble:

- The fields always *mirror* whatever is playing, so clicking a preset fills them in with that slot's real edges.
- Edits never overwrite the fixed F-presets. When an F-slot is active, your custom edit lands in **VAR1** instead. If VAR1 or VAR2 is already active, the edit updates that variable slot. The row header shows which VAR slot will receive the edit. Your custom variable widths are saved on the server, so they survive a restart.

In symmetric modes (AM, SAM, DSB, FM) the **Lo** field is disabled — the passband is mirrored around the carrier, so only the half-width matters.

> **Tip — pin a custom width.** Set up VAR1 the way you like it, then drag the VAR1 chip onto one of the filter favourite buttons in the control strip to keep your personal width a single click away.

### The Bandwidth Filter panel — see and drag the passband

The **Bandwidth Filter** panel is a miniature panadapter ("mini-pan") focused on the area around your signal. It draws a live, peak-holding spectrum trace of just the few kHz around the dial, the noise floor riding underneath, and the passband walls you can grab.

You can shape the filter directly here:

- **Drag a wall.** Grab the left or right passband edge and slide it to set the low-cut or high-cut. The real DSP filter follows your drag in near-real-time.
- **Fine-tune with the keyboard.** With the panel in focus, the arrow keys nudge the high edge by the per-mode step — 10 Hz for SSB and CW, 50 Hz for digital, 100 Hz for AM/SAM/DSB. Hold **Shift** for a ten-times-larger jump.
- **Scroll to nudge, Ctrl/⌘-scroll to zoom.** The wheel nudges the edge under the pointer; holding Ctrl (or ⌘ on a Mac) zooms the visible span in or out.
- **Magnetic snap.** When the global Snap feature is engaged, a dragged edge gently snaps onto a nearby detected carrier so you can fit the filter to a signal precisely. Hold **Alt** to place the edge freely and ignore the snap.

The mini-pan also shows the same flattened, signal-aware view the main panadapter uses, with faint amber ticks marking detected carriers — carriers inside the passband read brighter than those outside, so you can literally see what your filter is letting through. A small width readout in the corner can be clicked to type an exact bandwidth that keeps the centre fixed.

Because the panel is "split" from the presets, you can dock and size it independently. Note that whichever way you adjust the filter — chip, custom field, mini-pan drag, or arrow keys — they all drive the one filter; changes show up everywhere at once.

### Dragging the passband on the main panadapter

You do not have to open the Bandwidth Filter panel to reshape the filter. The translucent passband rectangle drawn over the main panadapter has the same grab-to-resize edges: pull the left edge for low-cut, the right edge for high-cut. As with the mini-pan, if a fixed F-preset is active your drag is diverted into VAR1 so your adjustment is never silently thrown away. During a smooth tuning glide the passband stays pinned to the centre line while the spectrum slides beneath it.

### Manual notch filters

A **notch** is a narrow band you carve out of the receive audio to kill a tuning whistle, a carrier, or other interference. Find the **NOTCH** button in the spectrum controls.

To place one:

1. Click **NOTCH** to arm it (the button highlights).
2. Drag across the offending signal on the panadapter or waterfall. A red-edged band appears where the audio is being cut.

A notch is locked to an *absolute* frequency, so it stays glued to the interference even as you tune around. While the notch tool is armed you can grab either edge of an existing band to widen or narrow it, and a small **✕** on each band deletes it. A count badge next to the button (e.g. **✕3**) clears all notches at once.

Notches are saved on the server and reapplied automatically, so the birdies you killed last session stay killed. The narrowest a notch can be is 20 Hz — fine enough for a single carrier, while voice-wide notches sit at the upper end of normal use.

### Spectrum width — the DDC sample rate

How much spectrum the panadapter shows is set by the radio's DDC **sample rate**, found in the DSP settings as **Sample Rate (DDC spectrum width)**. The ladder runs 48, 96, 192, 384, 768, and 1536 kHz. You can change it live without reconnecting; the active value and your radio's maximum are shown just below the buttons.

Two limits apply, and Zeus greys out rungs you cannot use, with a tooltip explaining why:

- **768 and 1536 kHz are Protocol-2 only.** Protocol-1 radios (Hermes, Hermes-Lite 2, and similar) top out at 384 kHz because the control frame can only carry the lower rates.
- **Your board's own maximum** caps the list further, so a rung above what your radio supports is locked even on Protocol 2.

A wider rate shows more band at once but spreads CPU and bandwidth thinner; a narrower rate concentrates resolution where you are listening. For most operating, 192 kHz is a comfortable default.

### The DSP filter architecture (diagnostics only)

Deep in the diagnostics area is a **DSP filter architecture** read-out. This is an *information* panel, not a set of knobs — it reports how WDSP, the DSP engine, is currently set up: the active RX and TX filter type and tap count, the FFT window in use, buffer sizes, the per-mode filter matrix, FFTW "wisdom" cache status, and how much of your DDC bandwidth is actually being used. It refreshes every couple of seconds.

For nearly all operators this is something to glance at, not change — the DSP filter geometry is fixed and tuned to match Thetis behaviour. Of the values shown, the only one you actually set is the sample rate, which the panel flags as "rate writable." Everything else is there so you (or someone helping you troubleshoot) can confirm the receive and transmit chains are configured the way they should be.
