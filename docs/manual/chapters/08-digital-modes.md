## Digital Modes — FreeDV, RADE & FT8/FT4

Zeus runs the popular digital modes **natively** — there is no second program to launch, no virtual audio cable to wire up, and no CAT loop-back to configure. Two families live here: **FreeDV** (and the new neural **RADE** waveform) for digital *voice*, and **FT8 / FT4** for the weak-signal *data* modes that dominate today's HF bands. FreeDV is built into every Zeus download; FT8 and FT4 are delivered by the free **Zeus Digital plugin**, installed in a minute from **Settings → Plugins** — and once it is in, they run natively just the same. This chapter covers installing and selecting them, the panels that appear, and how Zeus logs and reports your contacts.

The classic data modes you drive with *external* software — RTTY, PSK31, JS8 — still use the **DIGL / DIGU** modes from the previous chapter together with CAT control (see *Connecting*). FreeDV and FT8/FT4 are different: Zeus decodes and transmits them itself.

### FreeDV digital voice

**FreeDV** sends voice as data — your speech is compressed by a codec, modulated into a narrow SSB-width signal, and reconstructed at the far end. The result is clean, noise-free audio that either decodes perfectly or not at all, in the same bandwidth as an SSB phone signal.

To use it, pick **FreeDV** from the Mode panel like any other mode. Zeus opens a dedicated **FreeDV** panel (and, if you prefer, a pop-out window) holding everything in one place:

- the **mode selector** for the FreeDV waveform (see below),
- a **Stations / Reporter** list of who is active,
- the **decoded text** scrolling as stations identify, and
- your own **transmit text** field for the call sign and short messages FreeDV carries alongside the voice.

The passband rectangle and crosshair draw on the correct band-convention sideband, so what you see on the panadapter matches where the signal actually sits. Decoded FreeDV audio follows your normal **AF gain**, and the receiver behaves like any other mode otherwise — filters, AGC, and the waterfall all work as usual.

#### RADE V1 — the neural codec

The headline waveform is **RADE (RADAE)** — a new machine-learning-based FreeDV mode that delivers markedly higher voice quality than the classic codecs at the same bandwidth. Zeus does full **receive *and* transmit** on RADE V1, including the **LDPC end-of-over** burst that carries your call sign reliably even at the edge of decode.

You do not have to know in advance what the other station is sending. The **AUTO** setting watches the incoming signal and switches between RADE and classic FreeDV for you, so a net with a mix of waveforms just works.

| Setting | What it does |
|---------|--------------|
| **AUTO** | Detects RADE vs. classic FreeDV on receive and follows it. The usual choice. |
| **RADE** | Forces the new neural waveform — highest quality, for stations you know run RADE. |
| **700D / 700E / 1600** | The classic FreeDV waveforms, for compatibility with existing FreeDV users. |

#### Installing the modem

FreeDV's underlying **codec2** modem installs **from inside the app** — there is a one-click button in the FreeDV panel. No terminal, no separate download, nothing to compile. Zeus fetches and sets up the modem, and from then on FreeDV is just another mode. If the modem is missing when you select FreeDV, Zeus prompts you to install it first.

#### Transmitting

Key up the way you normally would (MOX, PTT, or a footswitch). Zeus encodes your microphone audio through the selected waveform and sends it. When you unkey, an **end-of-over TX tail** lets the far end finish decoding cleanly, and on RADE the LDPC call-sign burst goes out automatically. A wider speech-band resampler keeps the encoded audio natural.

> **Tip — watch the Stations list.** FreeDV Reporter shows active stations and spots *you* when you transmit, so it doubles as a quick check that your signal is getting out and being decoded.

### FT8 and FT4 — the Zeus Digital suite

**FT8** and **FT4** are the weak-signal data modes you hear all over the bands — fixed-length transmissions on a strict timing cycle that decode signals far below the noise floor. Zeus's **complete FT8/FT4 client** ships as the installable **Zeus Digital** plugin, so you can work the band without WSJT-X or any companion program.

#### Installing the Zeus Digital plugin

The **FT8** and **FT4** buttons are always present on the Mode panel (and in the toolbar mode drop-down), but they stay **greyed out** until the plugin is active. Hover a greyed button and its tooltip tells you exactly what to do next:

1. Open **Settings → Plugins**, find **Zeus Digital · FT8/FT4** in the plugin browser, and click **Install**.
2. **Restart Zeus once** when prompted — plugins register when the server starts, so until then the buttons stay greyed with a *"Restart Zeus to activate"* tooltip.
3. After the restart, **FT8** and **FT4** light up and behave like any other mode.

That's it — nothing to unpack and nothing to configure. Because Zeus Digital is a plugin, it also updates independently of Zeus itself, from the same Plugins page. If you later uninstall it, the mode buttons simply grey out again; your call sign, grid, and digital settings are kept on the server for the next install.

Select **FT8** or **FT4** from the Mode panel, and the **Zeus Digital** workspace appears. It can pop out into its own window, where it reuses the main panadapter, your QRZ look-ups, and the Zeus logbook — so there is nothing redundant to learn.

#### Decoding the band

The digital window shows a **waterfall of the audio passband** with every decode from the current cycle listed beside it. Each decode shows the calling station, its grid or report, and signal strength. **Click a signal on the waterfall** to tune your transmit slot onto it — the same click-to-tune feel as the main panadapter, scoped to the FT8 passband.

#### Working a station — armed auto-sequencing

You can answer a CQ by hand, or let Zeus run the contact:

1. **Arm** a QSO (click the station, or call CQ yourself and arm).
2. Zeus walks the standard FT8 exchange automatically — signal reports, **R**-reports, and the **73** — keying and unkeying on the cycle for you.
3. When the exchange completes, Zeus sends the terminal **RR73** and **stands down**, so you don't keep transmitting into a finished contact.

Set your **call sign and grid** once in the digital settings; they are saved on the server and persist across restarts, so you are ready to operate the next time you open the mode.

#### Logging and spotting

Every completed contact **logs automatically** to the Zeus logbook. Beyond that, Zeus connects you to the wider digital ecosystem — all of it **off by default**, so nothing leaves your station until you opt in:

- **PSK Reporter** and **WSPRnet** — spot the stations you decode to the reporting networks.
- **WSJT-X UDP broadcast** — Zeus emits the same UDP stream WSJT-X does, the universal "what I just worked" contract. Point any compatible tool at it and your QSOs appear live:

| Tool | What it gets |
|------|--------------|
| **GridTracker** | Live map of decodes and worked grids. |
| **QRZ** | Automatic log upload / look-up. |
| **Wavelog**, **ClubLog**, **N1MM** | Station logs and contest logging. |
| **HamClock** | Your activity pushed to the clock display. |

Because Zeus speaks the standard WSJT-X protocol, anything that already works with WSJT-X works with Zeus Digital — no special integration required.

> **A note on power.** FT8/FT4 transmit is just an SSB carrier under the hood, so it works on **any** supported radio. Run it at a modest power level and a low duty cycle the way the modes intend — these are weak-signal modes, not contest-amplifier modes.

#### WSPR is coming

**WSPR** (the very-low-power beacon mode) is present in the engine but **disabled in the UI for this release** — its button stays greyed out even with the Zeus Digital plugin installed, and selecting it does nothing. It returns in a later release once its reporting map lands. **FT8 and FT4 are the live data modes today.**

### Which digital mode should I pick?

| You want to… | Use |
|--------------|-----|
| Have a clean, noise-free *voice* QSO or join a digital-voice net | **FreeDV** (AUTO, or RADE for best quality) |
| Work weak-signal *data* contacts, chase DX/grids, spot the band | **FT8** (or **FT4** for a faster cycle) — install the **Zeus Digital** plugin |
| Run RTTY / PSK31 / JS8 in external software | **DIGL / DIGU** + CAT (see *Connecting*) |

All three coexist with the rest of Zeus — the logbook, QRZ look-ups, the panadapter, spotting, and (for FT8/FT4) the multi-receiver and remote-operation features in the chapters that follow.
