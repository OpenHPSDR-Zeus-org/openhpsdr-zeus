## Operating Tools & Accessories

Beyond tuning and transmitting, Zeus carries a full operating desk: a CW
keyer, a logbook with QRZ lookups and a world map, live activation and DX
spots, a space-weather dashboard, rotator control, a HamClock dashboard, a
tape-deck recorder, operator-to-operator chat, and integration hooks for
third-party software and add-on devices. Most of these live as panels you
add from the **Add Panel** menu and configure under **Settings**. This
chapter walks through each one.

> Two quick notes that apply throughout. First, several of these tools talk
> to the world over the network, so they need the Zeus backend reachable on
> your LAN and, in most cases, an internet connection. Second, a few items
> (HamClock, the WAV recorder's local files, and the Voyeur add-on) run on
> the same machine as the backend and are best thought of as **desktop**
> features.

### CW Keyer (the Telegraph Console)

Open the **CW** panel to send Morse. The big tape display across the top
shows what's being keyed right now, character by character, with a READY /
SENDING / STOPPING state lamp and a queue chip on the right.

| Control | What it does |
| --- | --- |
| **WPM slider** | Sending speed, 5–40 WPM. Drives both host-generated keying (macros) and the radio's on-board iambic keyer speed. |
| **STOP** | Aborts the current transmission and drains the send queue. |
| **KEY (STR / IAM-A / IAM-B)** | On-board keyer mode for a paddle plugged into the radio's KEY jack. Use **STR** for a straight key or bug (you control the timing; WPM is ignored), and **IAM-A** / **IAM-B** for an iambic paddle. |
| **PITCH** | Sidetone monitor tone, 200–1200 Hz. |
| **SIDE** | Sidetone monitor volume, −60 to 0 dB. |
| **Macro slots** | Click a slot to send it. Click the pencil to edit, the ✕ to delete, and **ADD MACRO** for a new one (up to 32). |

The sidetone you hear is generated in your browser, so it sounds whenever
you key — from a macro, from a logger over TCI, or from a hardware key. WPM
and sidetone settings are saved on the server and follow you between
browsers and sessions. Zeus also includes a **live CW decoder** that streams
what it copies into the console.

**Tip:** if you run a straight key or bug, make sure KEY is set to **STR** —
driving a single-line key in iambic mode keys incorrectly.

### Logbook and ADIF

The **Logbook** panel is your QSO log. A search box at the top filters by
callsign, band, mode, and more, and an eye toggle hides QSOs you've already
published to QRZ so you can see what's left to upload. The toolbar buttons
let you **import an ADIF file** (merging another logger's history into
Zeus) and **export your log to ADIF** (a download you can hand to a contest
robot, LoTW upload tool, or another logger). Entries are stored on the
backend, so the log is the same from any browser you connect with.

### QRZ Lookup, World Map, and Contacts

Sign in under **Settings → QRZ** with your QRZ.com username and password.
Your username is remembered locally; the password is never stored — Zeus
only uses it once to fetch a session key. Lookups need an active **XML
subscription** (the panel tells you if yours is missing), and publishing
QSOs to your QRZ logbook needs an **API key**, which you enter separately.

With QRZ connected, the **QRZ** panel becomes a lookup card: type a
callsign, press **Lookup**, and you get the operator's name, location, grid,
distance and bearing from your home grid, and a **Worked Before** panel that
checks your Zeus logbook and shows the last time you worked them. A **Log
QSO** button drops the contact straight into your log. Your QRZ home
location also feeds the world map and the rotator's short-path/long-path
math.

### Activation & DX Spots

The **Spots** panel lists live **POTA**, **SOTA**, and **DX cluster**
activations, and the headline feature is **click-to-tune**: click any row
and Zeus tunes your connected radio to that frequency (and mode, if you
like). Source chips (ALL / POTA / SOTA / DX) and a search box narrow the
list; columns sort on click.

Each row carries extras: a ✓ if the activator is already in your log, a ★ to
add them to a **watchlist**, and a **LOG** button that opens a pre-filled
QSO dialog. A **▶ Scan** button steps the dial down the visible list,
dwelling a few seconds on each — handy for working a band's worth of parks
hands-free (clicking a row manually stops the scan so you stay put).

Everything is configured under **Settings → Spots**:

- **Sources & poll interval** — turn POTA/SOTA/DX on or off and set how
  often the server re-fetches (30–600 s). DX is off by default because it's
  high-volume.
- **Filters** — restrict by band and mode, hide QRT spots, hide stations
  you've already worked, collapse repeats to the latest per activator, and
  set a maximum age.
- **Watchlist & alerts** — desktop notification (and optional sound) when a
  watched callsign is spotted.
- **QRZ enrichment** — show operator names next to spots (off by default to
  respect your QRZ lookup quota).
- **Click-to-tune** — whether tuning also sets the mode, whether it's
  blocked when no radio is connected, CW sideband, and small dial offsets
  for CW and digital so you can land slightly off the published frequency.

### Space Weather & Propagation

The **Solar · Space Weather** panel is a propagation dashboard fed by the
N0NBH solar data: solar flux, sunspots, A- and K-index, X-ray and particle
flux, solar wind, MUF, and more, plus per-band **HF day/night conditions**
and **VHF** openings (aurora, E-skip). Values are color-coded — green for
quiet/good, amber for unsettled/fair, red for storm/poor — so you can read
band health at a glance. It refreshes itself every few minutes; the ↻ button
forces an update. No setup is required.

### Rotator Control

Zeus talks to any Hamlib-compatible rotator server. That includes
**rotctld** (Hamlib's own rotator daemon) and **PSTRotator** in its built-in
**Hamlib-Rotor** mode — point either at a free TCP port and Zeus connects.

Under **Settings → Rotator**, you'll see up to four rotator *slots*. A
single-rotator station only needs slot 1 — give it a label, set host and
port (often `127.0.0.1:4533`), tick **Enabled**, use **Test Connection** to
confirm it answers, and **Save**. Settings live on the backend and are
shared across browsers; Zeus only auto-connects on startup if Enabled was
set at the last clean exit.

**Multiple rotators.** Stations with more than one tower (HF beam, VHF
Yagi, dedicated 6 m antenna…) can configure up to four slots, each with its
own host:port and a checklist of the bands it covers. PSTRotator users
running several PSTRotator instances on the same PC just point each slot at
a different port. Tick **Auto-route active rotator by TX band** and Zeus
switches the live rotator automatically as you QSY across bands — the
matching slot becomes the active one and the Compass/Dial panels follow.

Only the active slot holds a live TCP connection at a time; switching slots
takes a couple of seconds while Zeus closes the old socket and opens a new
one to the next controller.

Two panels let you point whichever rotator is active:

- **Rotator Dial** — a compass dial with a live needle showing the current
  heading and a marker for the pending target. Click anywhere on the face to
  rotate there, type a heading and press **GO**, or hit **STOP**. When you
  have more than one rotator configured, a small picker in the controls row
  lets you override which rotator the dial drives.
- **Rotator Compass** — the dial overlaid on a world map centered on your QRZ
  home. Look up a callsign in the QRZ panel and the map draws the great-circle
  path; **SP** and **LP** buttons slew short-path or long-path with one click,
  with a **DIST** readout in kilometers. Right-click the map to rotate to any
  bearing. The rotator picker sits next to the NOW heading badge.

Headings are always shown as a clean 0–359°, even when the rotator reports a
signed angle from crossing its zero stop.

### HamClock

**HamClock** is a full ham-radio dashboard — propagation maps, DX cluster,
satellites, POTA/SOTA, space weather — embedded as a Zeus panel. Because it's
a separate program, you install it once from **Settings → HamClock**:
**Install** downloads and builds it locally (a few hundred megabytes land in
app data, not in the Zeus installer). It needs Node.js; if your system
doesn't have it, Install fetches a small private copy automatically, so it
never blocks you. After installing, **Enable Workspace** adds a HamClock tab
to the left layout bar. The panel starts the HamClock server on its own when
you open it. This is a **desktop** feature — it runs on the same machine as
the backend.

### WAV Recorder (the Tape Deck)

The **WAV Recorder** is a reel-to-reel tape deck for your audio. Pick **RX**
(received audio) or **TX** (your processed transmit audio) and press **●
REC**; the reels spin and a counter runs. Recordings save to your **Downloads
folder** as float-32 WAV files and appear in the list below, where you can
play or delete them.

Playback has a clever twist tied to your transmit state:

- **MOX off** → **▶** plays the file locally through your speakers.
- **MOX on** → the play button becomes **📡** and the recording is
  transmitted **on the air**. An **ON AIR** indicator warns you.

That makes the deck a built-in voice-keyer: record a CQ call once, then play
it out with MOX up whenever you want to send it. Because the files are
written on the backend machine, this is a **desktop** tool.

### ZeusChat — Operator Chat

The **Chat** panel is operator-to-operator chat over the public ZeusChat
relay. It's **off by default**; press **Enable** to connect. Two things to
know before you do: you must be **logged into QRZ** (the relay verifies your
QRZ session, so your callsign is your identity), and enabling chat
**broadcasts your callsign and live VFO frequency** to other logged-in
operators. An eye toggle in the header lets you hide your frequency.

The left sidebar lists who's online, grouped by friends and by band, with a
colored dot showing whether each operator is receiving, transmitting, or
away. Star an operator to send a friend request; click any callsign to pop
their QRZ profile card. Across the top are room tabs: **Public** for everyone,
private **Groups**, and one-to-one **DMs** (private rooms glow gold). Type in
the composer and press Enter to send (Shift+Enter for a newline). Messages up
to 2000 characters are kept in a recent history so you see context when you
join.

### TCI — Third-Party Software Integration

**TCI** lets external programs control your radio: loggers (Log4OM, N1MM+),
digital-mode software (WSJT-X, JTDX, MSHV), and other SDR display tools.
Zeus speaks the ExpertSDR3-compatible TCI WebSocket protocol. Configure it
under **Settings → TCI**: tick **Enabled**, choose a **bind address**
(`127.0.0.1` for same-machine only, or `0.0.0.0` to allow LAN clients — note
there's no authentication), and a **port** (the ExpertSDR3 standard is
**40001**). **Test Port** checks availability. Changing the port or bind
address requires a Zeus restart, which the panel reminds you of; once
running, a status line shows how many clients are connected. In your other
software's TCI settings, point it at the Zeus machine's IP, the same port,
over `ws://`. Digital clients key cleanly through TCI, and their transmit
meters and SWR show on the Zeus panels during a transmission.

### Voyeur — Unattended Net Monitor (plugin)

**Voyeur** is your automatic net secretary. Point it at a net and walk away:
it records every over, transcribes the speech to text **entirely on your
machine**, extracts callsigns and validates them against your QRZ account,
builds a live check-in roster, and uses a small local AI model to write a
"what was discussed" summary. Everything is saved in a searchable archive
with one-click replay of any over.

Voyeur is **not part of the main download** — install it from **Settings →
Plugins** so operators who don't use it carry none of its weight. The speech
and AI engines download once on first enable, on every platform. Privacy is a
design point: transcription and summarization run offline, and the only thing
that leaves your computer is the QRZ lookups you already make. If you used an
earlier in-core build of Voyeur, the plugin adopts your existing logs and
models automatically.

### RF2K-S Amplifier (plugin)

Control of the RF-Kit **RF2K-S** amplifier — including front-panel **Tune**
and **Bypass** — ships as a plugin, not as part of Zeus core. Install it from
**Settings → Plugins → Browse** ("RF2K-S Amplifier"); the panel then appears
under **Add Panel**. It talks to the amp over its normal network connection,
so you don't need to reconfigure the amplifier itself — but settings such as
the amp's IP and polling interval are re-entered in the plugin's own settings
tab the first time. Future external-device support (other amplifiers, tuners,
antenna switches) follows the same model and ships as plugins.

### External Ports (antenna / open-collector / PTT)

Where your radio exposes them, Zeus can drive external control lines —
antenna selection, open-collector outputs, and PTT/keying for sequencing
external amplifiers — across both Protocol-1 and Protocol-2 boards. Because
these lines are board-specific, what you see depends on the radio you're
connected to; consult your radio's chapter for the exact controls available
on your hardware.

> **Antenna foot-gun worth knowing:** if one band suddenly goes dead with an
> unusually low noise floor, check the per-band **RX-aux** routing. Setting a
> band's RX-aux to a Bypass/empty path can route receive to an unused jack.
> The fix is to set that band's RX-aux back to **None** — it's a routing
> choice, not a fault.

### CW Decoder (neural)

In addition to the CW Keyer's built-in live decoder, Zeus offers a separate **CW
Decoder** panel (found under the Plugins category) that runs a neural
(machine-learning) decoder over your received audio. Switch it on with its ON/OFF
toggle and it prints decoded CW text as it listens; a small status line shows when
it is loading its model and when it is actively decoding, and you can choose the
decode-window length to trade quicker response against steadier accuracy. It works
independently of the keyer's decoder — use whichever one copies a given signal
better.
