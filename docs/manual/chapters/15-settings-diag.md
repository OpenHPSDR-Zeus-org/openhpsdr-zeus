## Settings, Diagnostics, Display & Getting Help

This chapter is a tour of the Settings workspace — every tab, what it does, and how to use it — plus the display and theme controls, sharing Zeus over your home network, the one-click "Report a problem" helper, the keyboard and mouse shortcuts, and a short troubleshooting reference.

### Opening Settings

Settings is a full-screen workspace, not a little pop-up. Open it from the gear/Settings control in the layout bar, and it replaces the panadapter view. Click any layout tab in the left bar to return to your normal operating screen, or press **Esc** to close. Down the left side of the Settings workspace is a column of tabs; click one to switch sections. At the top is a radio selector so you can confirm or change which radio these settings apply to.

A note on one tab in particular: **PA Settings** is the only tab with an explicit **APPLY** / **CANCEL** footer. Edits there are staged and only sent to the radio when you click APPLY (CANCEL re-reads the radio's current values and throws your edits away). Every other tab saves as you go — changes apply immediately.

### The Settings tabs at a glance

| Tab | What it covers |
| --- | --- |
| **PA Settings** | Per-band power-amplifier drive and rated-watts. Staged — remember to APPLY. |
| **PureSignal** | Adaptive predistortion arm/calibrate controls (Protocol 2 / ANAN-class). |
| **Audio Tools** | TX voice-processing chain (EQ, compressor, and friends) and plugin slots. |
| **DSP** | Receive and transmit DSP: bandwidth, filters, AGC, ADC protection, squelch, TX leveling, Signal Intelligence, Smart NR. |
| **Band Plan** | The frequency/mode band plan editor. |
| **QRZ** | Sign in to QRZ.com for callsign lookups and your home grid. |
| **Rotator** | Connect to a Hamlib `rotctld` antenna rotator. |
| **Network** | Expose Zeus to external software two ways: **TCI** (ExpertSDR3 Transceiver Control Interface) and **CAT** (Kenwood TS-2000 emulation), for logging, contest, and digital-mode programs. Also home to the direct **DX-cluster** (Telnet) client. |
| **Display** | Theme, panadapter background, trace color, spectrum scale, waterfall colormap. |
| **Plugins** | Install and manage backend/UI/audio plugins from the registry or by URL. |
| **HamClock** | Install and embed the OpenHamClock dashboard as a workspace panel. |
| **Spots** | POTA/SOTA/DX-cluster spot feed, watchlist, alerts, and click-to-tune behavior. |
| **Server** | Point a phone or wrapped app at the Zeus server on your LAN. |
| **Radio** | Board-specific firmware options. Only appears when your radio has any. |
| **Calibration** | Per-radio frequency (crystal-drift) correction against WWV. |
| **Updates** | Check for and download new Zeus releases. |
| **About** | Version, release date, credits, and a Danger Zone with **Reset & Uninstall Zeus**. |

#### DSP

The DSP tab groups the receive and transmit signal-processing controls into cards: **Bandwidth** (DDC sample rate, 48–1536 kHz), **WDSP Filter Architecture** (buffers, taps, window, cache), **AGC** (mode, max-gain, custom), **ADC Protection** (overload auto-attenuation), **RX Squelch** (mode-aware for SSB/AM/FM), **TX Leveling** (ALC, leveler, compressor), **Signal Intelligence** (peak pop/snap and markers), and **Smart NR Automation** (panadapter-driven noise-reduction policy). Each card maps to a Thetis-style Setup ▸ DSP family. These are operating controls — change them and listen; safe defaults are already loaded.

#### QRZ

Enter your QRZ.com API key to enable callsign lookups. Once connected, the panel shows your sign-in callsign, home grid and coordinates, and whether your account has an active **XML subscription** (lookups need that subscription — without it, lookups are disabled even though sign-in succeeds). QRZ data also feeds the Beam Map background described below.

#### Rotator and Network (TCI / CAT)

**Rotator** connects Zeus to a Hamlib `rotctld` daemon: tick Enabled, set the host and port (default `127.0.0.1`), and test the connection. The **Network** tab lets external software control Zeus two ways: **TCI** (the ExpertSDR3 Transceiver Control Interface, for loggers and contest programs) and **CAT** (a Kenwood TS-2000 emulation over TCP, default port **19090**, for the many programs — including digital-mode apps configured for a serial Kenwood rig — that speak Kenwood CAT). For each, set the bind address and port and test it. Both are unauthenticated and CAT grants full transmit control, so a `0.0.0.0` (LAN-visible) binding belongs only on a trusted network; if the daemon or client is on another machine, use that machine's LAN address rather than `127.0.0.1`. CAT — including connecting software that only speaks serial COM ports — is covered in full in the Accessories chapter.

#### HamClock and Spots

**HamClock** embeds the OpenHamClock dashboard (propagation, DX cluster, satellites, POTA/SOTA, space weather) as a panel inside Zeus. Click Install and Zeus downloads and builds it locally — nothing is bundled into the Zeus installer, and if your system has no Node.js it quietly fetches a private copy (~30 MB). After install you can add, switch to, or remove its workspace tab (removing the tab does not uninstall it) and stop the server process.

**Spots** drives the spotting feed: enable POTA, SOTA, and/or the DX cluster, filter by mode, hide QRT or already-worked stations, keep only the latest spot per activator, maintain a callsign watchlist with optional audible alerts, and choose whether clicking a spot also sets the mode and whether tuning is allowed only when a radio is connected.

#### Calibration

The Calibration tab hosts a one-button **frequency calibration** card. Click **Calibrate** and Zeus tunes to WWV on 10.000 MHz (the U.S. standard-frequency station from Fort Collins, Colorado, on the air 24/7), measures any offset on the panadapter, and stores a per-radio correction factor so your dial matches your actual transmit frequency. Your VFO, mode, filter, and zoom are restored when it finishes. The card shows the current correction (offset at 10 MHz and parts-per-million), and **RESET** clears it back to factory. The radio must be connected to calibrate.

#### Updates and About

**Updates** shows your installed version, the latest available version, your platform, and the download. **Check for updates** refreshes the status; **Update now** opens the correct installer/package for your platform — download it, run it, and restart Zeus. If you run Zeus from source, the panel tells you instead to use the bundled `scripts/update.sh` (macOS/Linux) or `scripts/update.ps1` (Windows) and restart. **About** shows the clean version number, the release date, and credits.

#### Radio (board options)

The **Radio** tab only appears when your connected board actually exposes a firmware option Zeus can write. On a **Hermes Lite 2** that's **Band Volts** (repurposes the fan-control PWM output so an external amp like the Xiegu XPA125B can follow band changes). On an **ANAN-G2** it's the ADC **Dither** / **Random** linearity toggles. If your radio has none of these, the tab is hidden and you'll never see an empty page.

### Display, themes, and backgrounds

The **Display** tab is where you make Zeus look the way you want. It contains:

- **Theme** — choose **Dark** (near-black chrome, the original lit-display Zeus look) or **Light** (brushed-silver chassis, a daytime-shack mode). The display surfaces — panadapter, gauges, VFO — stay dark in both themes so weak signals stay readable. Below the theme picker are per-color overrides grouped by purpose; you can fine-tune individual tokens, and a **Reset** restores any color to its default. Heed the on-screen warning on surface colors: push them too far and text can become unreadable; Reset recovers it.
- **Panadapter Background** — three modes: **Basic** (plain panadapter and waterfall), **Beam Map** (a world map behind the trace with your QRZ contacts and rotator heading overlaid — this needs QRZ configured), and **Image** (your own photo behind the panadapter). In Image mode, drop a file onto the panel or click to choose one, then pick a **Sizing** of Fit, Fill, or Stretch; **Clear Image** removes it. Large photos are automatically shrunk (longest edge capped at 1920 px, saved as JPEG) so they don't overflow the browser's storage.
- **RX Trace Color**, **Spectrum Scale** (separate dB min/max for RX/TX panadapter and RX/TX waterfall, each with a reset), and **Waterfall Colormap** (palette plus scroll speed).

### Server mode, LAN sharing, and phone access

Zeus is a web client talking to a Zeus server. When you open Zeus in a browser served by that same server (the typical desktop setup), there is nothing to configure — leave the **Server** tab blank.

The Server tab matters when Zeus is a separate app from the server — for example a phone app, or a desktop wrapper — and needs to be told where the server lives on your network. Enter the server's LAN address, such as `http://192.168.1.23:6060` (6060 is the Zeus server's port), then **Save & reload**. **Clear** returns to same-origin behavior. On a wrapped/native phone build, Zeus permits plain HTTP to home-network addresses; on first contact iOS may ask "Find devices on local network" — allow it.

The tab also shows a **Mobile browser HTTPS** box. Browsers will only grant microphone access over HTTPS (or to `localhost`), so transmitting voice from a phone browser on your LAN requires an HTTPS address. If the server was started with LAN HTTPS enabled, the secure addresses are listed here as tappable links; if the box says none were reported, restart the server with LAN HTTPS turned on. This is a LAN-only feature — Zeus does not expose your radio to the public internet.

### Report a problem

At the bottom transport bar there's a **⚠ Report a problem** button. It opens a self-diagnostic helper written for operators, not programmers:

1. Pick the closest match from the symptom list and/or describe the issue in your own words, then click **Run diagnostic**. Zeus quietly collects a snapshot of your radio, DSP, and connection state.
2. Zeus packages that into a ready-to-file bug report and shows you plain, step-by-step instructions to send it: click **Open bug report page** (it opens in your browser), sign in or make a free account if asked, and submit the pre-filled report.
3. If the page doesn't fill itself in, click **Copy report** and paste it into the description box. You can expand "This is exactly what will be sent" to read the full report before sending — nothing leaves your machine until you submit it.

The snapshot now also records your radio's **firmware version**, and behind the scenes Zeus keeps a rolling on-disk log and captures verbose diagnostics if it ever crashes, so a follow-up report has the detail a maintainer needs.

### Maintainer support sessions (opt-in, read-only)

When a problem is hard to pin down over text, you can let a Zeus maintainer look at your running station directly — but **only with your explicit permission, every time**. A support session is **read-only**: the maintainer can observe your diagnostics and state to help you troubleshoot, and cannot operate your radio.

You grant access from the diagnostics controls; nothing connects until you do, and the session ends when you close it. If you've opted in, a crash can be **auto-shared** with the maintainer to speed up a fix. This is strictly opt-in plumbing — by default Zeus shares nothing, and there is no way for anyone to start a session without your action.

### Reset & Uninstall Zeus

The **About** tab includes a **Danger Zone** with a **Reset & Uninstall Zeus** tool for when you want to start completely fresh or remove Zeus cleanly. It wipes Zeus's settings and data across the whole system — and, on a desktop install, removes the application itself the proper way for your platform (the Windows uninstaller, the macOS app bundle, or the Linux AppImage).

Because this is destructive, Zeus first writes an **inline backup** of your settings to your **Downloads** folder, so you can recover or move your configuration if you change your mind. As with any wipe, read the confirmation carefully before you proceed.

### Keyboard and mouse shortcuts

Zeus has a small set of always-on shortcuts. They only fire when a radio is connected, and they're ignored while you're typing in a text box.

| Input | Action |
| --- | --- |
| **← / →** | Nudge the VFO down / up by your selected tuning step. |
| **↑ / ↓** | Zoom the panadapter in / out one level. |
| **Option/Alt + ↑ / ↓** | Zoom the world-map background in / out (when the Beam Map is shown). |
| **Space (hold)** | Key the transmitter (MOX on) while held; release to drop back to receive. |

For the mouse: click on the panadapter or waterfall to tune there, drag to pan, and use the scroll wheel/zoom controls to change span. (A full click-to-tune and waterfall walkthrough lives in the Panadapter chapter and on the wiki.)

### Troubleshooting quick reference

**Brief disconnects while a developer is editing the UI** (dev mode only) are normal — the display reconnects within a few seconds and your radio link stays up the whole time.

**"Radio gone" / display freezes:** this means the radio stopped sending packets for about a second. Check that the radio is powered and reachable (ping its IP). If it pings but Zeus still times out, you're losing UDP packets on the network — Protocol 1 runs a heavy packet stream, so prefer **wired Gigabit Ethernet over Wi-Fi**, and pause big downloads or other heavy network use. Click **Disconnect**, then **Discover** and reconnect — Zeus does not auto-reconnect after a network change.

**UI says "Connected" but nothing updates:** click **Disconnect** manually to force a clean reconnect, then reconnect.

**Reconnection keeps failing entirely:** the server process likely stopped or crashed; restart it. On the very first run after install, give Zeus time to build its DSP "wisdom" before expecting audio.

**No microphone on a phone browser:** you need an HTTPS LAN address (see the Server tab's Mobile browser HTTPS box). On a wrapped phone app, allow the local-network prompt.

When in doubt, use **Report a problem** — it captures the right diagnostic state automatically, and the wiki's Troubleshooting page covers known quirks and missing-library issues.

### Transmit band guard (Band Plan)

Zeus can stop you from transmitting outside an amateur band. The **Band Plan** tab
defines the licensed segments, and by default, if you try to key up (MOX or TUNE)
with the dial outside a permitted segment — for example just past a band edge —
Zeus inhibits the transmission as a safety guard. The Band Plan editor includes an
**ignore / override** toggle for this guard: leave it on for protection, or turn
it off deliberately if you operate where the stock band plan doesn't apply (such as
MARS/CAP, or a region with different allocations). The same band-plan lookup drives
the in-band / out-of-band status Zeus shows you, which is why transmit can be held
off when you tune right to the very edge of a band.
