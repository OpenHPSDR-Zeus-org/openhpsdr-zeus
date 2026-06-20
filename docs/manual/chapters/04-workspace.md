## The Zeus Workspace & Interface

Zeus puts your whole station on one screen. Instead of fixed windows, you get a **workspace** built from movable, resizable **panels** — a panadapter, an S-meter, a VFO, a DSP rack, a logbook, and many more — arranged the way *you* like to operate. This chapter walks the screen from edge to edge: the top bar, the layout bar down the left, the panel grid in the middle, and the transport bar pinned across the bottom. It also covers saving and switching layouts, popping panels out into their own windows, and the differences between the desktop app, a browser on your LAN, and your phone.

### The screen at a glance

A connected Zeus session has four regions:

| Region | Where | What lives there |
|---|---|---|
| **Top bar** | Across the top | Brand mark, the always-visible radio controls (mode, filter, band, step, front-end, AGC, squelch, AF), and the connect/disconnect control |
| **Layout bar** | Down the left edge | One tab per saved layout, an **Add** tab, and the **Settings** gear at the bottom |
| **Workspace** | The large center area | Your panels, laid out on a grid |
| **Transport bar** | Pinned across the bottom | MOX / TUNE / PS, audio + mic meter, secondary controls, and live status chips |

When no radio is connected, the workspace dims and a **Connect** panel floats in the center so it's the first thing your eye lands on. The top bar stays live underneath it, so you can still sign in to QRZ or open Settings before a radio comes up.

### The top bar

On the far left is the **brand mark** — the lightning-bolt logo — with "OpenHpsdr Zeus" beside it and, just below, a small label showing the radio Zeus actually sees on the air (for example "ANAN G2" or "Hermes-Lite 2", or "Not Connected"). That label reflects what discovery found on the wire, not a preference you set, so it's a quick way to confirm you're talking to the radio you think you are.

The middle of the top bar holds your **primary radio controls**, always visible so they're reachable mid-QSO without hunting through the workspace: **mode** favorites, the **filter** strip, **band** favorites, tuning **step**, the **FRONT-END** group (preamp button + attenuator slider), and the **AGC**, **SQL** (squelch), and **AF** (audio gain) sliders. If the window is narrow and the controls don't all fit, small **chevron arrows** appear at each end of the strip to scroll through them.

When a radio is connected, the right end of the top bar shows a compact **Disconnect** control. Settings is *not* up here — it lives at the bottom of the left layout bar (see below).

### The left layout bar

The vertical bar down the left edge is your **layout switcher**. Each tab is a saved arrangement of panels — an emoji icon with a short label beneath it. Click a tab to switch to that layout instantly.

- **Switching layouts** — click any tab. The workspace swaps to that layout's panels.
- **Adding a layout** — click the dashed **+ Add** tab at the bottom of the list. A small dialog lets you set a **label**, an **icon** (pick from the emoji palette or paste your own), and an optional **description** that shows as a hover tooltip. There's also a **Lock panel positions** checkbox — tick it to create a layout whose panels stay pinned (handy for a "show" or contest layout you don't want to nudge by accident).
- **Editing a layout** — click the small **gear** on a tab to reopen that same dialog and change its label, icon, description, or lock state.
- **Deleting a layout** — the active tab shows a small **✕** (only when you have more than one layout; Zeus won't let you delete your last one). You'll be asked to confirm.

Layouts are **per radio.** Each radio model gets its own collection of layouts, so the panel set you build for your G2 doesn't follow you onto your Hermes-Lite 2 — switch radios and the layout bar repopulates with that radio's layouts.

At the very bottom, below a divider, is the **Settings gear.** Clicking it replaces the workspace with the Settings view; clicking any layout tab brings the workspace back.

### Building a layout: adding, moving, and resizing panels

The workspace is a grid. Panels snap to it and reflow around each other.

**Add a panel.** Click the round **+** button in the bottom-right corner of the workspace. The **Add Panel** window opens with a category rail on the left (Spectrum, VFO, Meters, DSP, Log, Tools, Controls, Plugins, and more) and a searchable list of panels on the right. Click a category to filter, or type in the search box. Click a panel card to drop it in and close the window. Available panels include the Panadapter/World-Map, Frequency · VFO, S-Meter, QRZ Lookup, rotator panels, the DSP rack, CW console and decoder, the Logbook, TX meters and fidelity, a filter panel, the PureSignal panel, band/mode/step pickers, the WAV tape-deck recorder, analog and grouped meters, DX spots, chat, HamClock, space weather, and a general web-embed panel. The **Meter Group** panel is multi-instance — you can add several, and its card shows "+ Add another."

**Move a panel.** Grab a panel by its **header strip** (the grip icon and title at the top) and drag. The other panels reflow to make room. Clicks inside a panel's body — on its sliders, canvas, or buttons — do *not* start a drag, so your controls keep working.

**Resize a panel.** Drag the handle at a panel's lower-right corner. Each panel has sensible minimum and maximum sizes so it never shrinks below the point where it's readable; right-column panels (VFO, S-meter, DSP, TX meters) are capped in width so they grow taller without sprawling into the panadapter.

**The workspace never scrolls.** It's designed to fit the screen like a hardware front panel. As you add panels or the window gets shorter, Zeus shrinks the grid to keep everything in view rather than showing a scrollbar.

**Remove a panel.** Click the **✕** at the right end of a panel's header. You'll be asked to confirm — and you can always add the panel back later from Add Panel.

**Lock a single panel.** Each panel header also has a small **padlock** button. Click it to pin that one panel in place: it keeps its exact size and position while you rearrange everything around it. Click again to unlock. (If you locked the *whole* layout from the Add/Edit dialog, every panel shows as locked.)

If a layout references a panel that isn't installed — for example a plugin panel after you've removed the plugin — Zeus shows a placeholder tile explaining the panel is unavailable, with shortcuts to the **Plugins** settings or to remove the tile. Your layout is never silently broken.

### Saving and resetting layouts

You don't have to save manually — **layout changes persist automatically.** As you move, resize, add, or remove panels, Zeus writes the arrangement back to the server (and makes a best effort to save again if you close the tab mid-edit). Reopen Zeus and your layout is exactly as you left it.

To start a layout over, use the **⟳ Default** button at the right end of the bottom transport bar. It resets the *active* layout's panel arrangement to its default while keeping the layout tab itself (and its name and icon). You'll be asked to confirm. This is also the recover action if a layout ever renders badly — resetting clears whatever state caused it.

### Detaching a panel layout into its own window

You can pop a whole layout out into a **separate window** — useful for a second monitor. Grab a layout tab in the left bar and **drag it off the bar**, then release. The layout opens in its own window (a native window in the desktop app, or a browser popup window in a browser). The detached window shows just that layout's panels, live with the same radio data as the main window. This is a desktop/browser feature; it isn't available on mobile.

### The bottom transport bar

The transport bar is pinned across the bottom and is where transmit, audio, and status live. From left to right:

- **MOX** — keys the transmitter (manual transmit). Click to go to transmit; click again to drop back to receive.
- **TUNE** — emits a steady carrier at your tune power for tuning an antenna or amplifier.
- **PS** — the PureSignal master arm toggle. *PureSignal always starts disarmed at launch and must be armed by hand each session* — a deliberate safety measure for stations running external amplifiers.
- **Audio toggle + mic meter** — turn receive audio on/off, with a live microphone-level meter beside it so you can see your mic is being heard.
- **Secondary controls** — CTUN (click-tune), and SPLIT / RIT / SAVE MEM buttons (some hidden on narrow/mobile widths).
- **Status chips** (right side): **PA temperature**, **PRE** (preamp on/off), **RADIO** (the connected radio's address, lit when connected), and live **rotator** and **QRZ** pills. There's also a **Report a problem** button and the **⟳ Default** layout-reset button described above.

The mobile view shows live **SWR** and **MIC dBfs** chips in the S-meter header while you're transmitting, so you can keep an eye on your match and mic level from a phone.

### Desktop vs. web vs. mobile

Zeus runs the same backend everywhere; the *interface* adapts to where you're viewing it.

- **Desktop app** — Zeus running as its own application on the same computer as the backend. This is the full experience: native detached windows, native audio, and the complete panel set. Microphone access works without extra steps.
- **Web (LAN browser)** — point a browser on another computer at the Zeus server's address and you get the same desktop-style workspace. One thing to know: microphone access in a browser requires a **secure (HTTPS)** connection. The Zeus server prints an `https://` LAN address at startup — use that one if you intend to transmit from a browser. The first visit warns that the certificate is self-signed; accept it to proceed.
- **Mobile** — open Zeus on a phone (or any screen about 900 px wide or narrower) and you get a **single-column touch layout** built from the same panels and the same live data. It has two tabs at the top: **Radio** (frequency/VFO, S-meter, panadapter+waterfall with a drag divider, PTT and tune, mode and band) and **Tools** (tiles for TX Controls, Rotator, QRZ Lookup, and DSP, each opening the full desktop panel scaled to the column). The mobile shell **inherits** layout and display choices you made on the desktop; you build and arrange panels on the desktop, and they appear on the phone. On mobile, pinch to zoom the spectrum and use the on-screen trackpad and **lock** button to tune.

### URL Embed panel

A Tools-category panel that pins any web page inside your workspace as a tile,
with its own small address bar across the top. Type or paste a web address and the
page loads right there — handy for a logging site, a live band-conditions page, a
club or net page, or a dashboard sitting next to your radio. You can add as many
URL Embed tiles as you like, each pointing somewhere different, and they save and
restore with your layout like any other panel.
