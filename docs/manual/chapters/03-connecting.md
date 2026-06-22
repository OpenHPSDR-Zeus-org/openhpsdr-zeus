## Connecting to Your Radio

Before Zeus can show you a waterfall or key up, it has to find your radio on the network and open a link to it. This chapter walks through the Connect panel: letting Zeus discover the radio for you, connecting by hand when you need to, picking the right protocol and sample rate, and telling Zeus exactly which board you own so its drive and power math is correct.

### The Connect panel at a glance

When Zeus starts up and you are not yet connected, the Connect panel is front and center. It carries the Zeus banner ("OpenHPSDR · Protocol 1 / 2") and two tabs:

- **Discover** — Zeus listens on your local network for any OpenHPSDR radio and lists what it finds.
- **Manual** — you type in the radio's IP address and connect directly.

Once you are connected, the panel collapses to a single **Disconnect** button (and, in the full layout, a small chip showing the radio's address). To change radios, disconnect first.

#### First-run setup ("Preparing wisdom file")

The very first time you run Zeus on a new machine, you may see "Preparing wisdom file…" with a progress line instead of the Connect controls. This is a one-time step: Zeus pre-calculates the math used by noise reduction, filters, and the panadapter so they respond instantly later. Leave the app open and wait — the Connect button appears on its own when it finishes. Subsequent startups skip this entirely.

### Discovering a radio

The Discover tab does the work for you. Zeus scans your LAN and re-scans automatically every 10 seconds, so a radio that you power on after opening Zeus shows up within a few seconds. The status text on the right tells you what it is doing ("Scanning…", "Refreshes every 10 s").

Each radio Zeus finds appears as a row showing:

- The board name it reported and its firmware version (e.g. "fw 2.7b41").
- A protocol chip: **P1** (Protocol 1) or **P2** (Protocol 2) — the protocol Zeus used to discover it.
- A **LAST** badge on the radio you most recently connected to. That radio is also floated to the top of the list for convenience.
- The radio's network address and MAC address underneath.

Press **Connect** on the row you want. If a radio shows **Busy**, another client already has it (HPSDR radios serve one client at a time). Use **Take over** next to the Busy badge to claim it anyway: Zeus asks you to confirm, then sends the radio a stop command that drops the current client and connects you. **This kicks the other operator off — including in the middle of a transmission — so only do it for a radio you own.**

> **Tip:** If the list says "No radios found," check the radio's power and Ethernet cable, and make sure your computer is on the same subnet as the radio. Discovery uses a network broadcast, so a radio on a different VLAN or across a router will not appear — use Manual mode in that case.

### Manual connect

Switch to the **Manual** tab when discovery can't reach the radio (different subnet, a managed switch that blocks broadcast, or a remote link). You fill in:

| Field | What it does |
|---|---|
| **IP address** | The radio's IPv4 address, e.g. `192.168.1.20`. Zeus validates the format before connecting. |
| **Port** | The data port (1–65535). The default is correct for almost every radio. |
| **Protocol** | Choose **P1** or **P2** (see below). |
| **Sample rate** | The receive bandwidth/spectrum rate (see below). |
| **Radio type** | Tells Zeus which board you have, or leave it on **Auto-detect**. |
| **Save for next time** | Keep this ticked to add the connection to your Saved Endpoints list. |

Press the big **Connect** button when ready.

#### Saved endpoints

Anything you connect to with "Save for next time" ticked is remembered under **Saved endpoints** at the bottom of the Manual tab. Each saved entry shows its address, sample rate, protocol chip, and — if you set one — a board-override chip. Press **Connect** to reconnect in one click, or the **✕** to forget it. Your most recent connection carries the **LAST** badge.

### Protocol 1 versus Protocol 2

Zeus speaks both OpenHPSDR protocols. The radio determines which one applies:

- **Protocol 1 (P1)** — the original protocol. This is what the **Hermes Lite 2**, Hermes, older ANAN boards, and the original Metis stack use. P1 is the most mature path in Zeus.
- **Protocol 2 (P2)** — the newer protocol used by **ANAN-G2 / G2 MkII** and other Saturn/Orion-class radios. In Zeus the P2 path is still maturing; in Manual mode the P2 button is labeled "experimental, RX only" as a caution, though G2-class TX (TUNE and MOX) has been verified on the bench.

In Discover mode Zeus already knows which protocol each radio uses and connects accordingly — you don't have to choose. In Manual mode, pick the protocol that matches your hardware. If you're unsure, an ANAN-G2/G2 MkII uses P2; a Hermes Lite 2 uses P1.

### Sample rate

The sample rate sets how wide a slice of spectrum Zeus receives and displays. Higher rates show more bandwidth at once but ask more of your computer and network.

Zeus offers 48, 96, 192, 384, 768, and 1536 kHz, but it filters the list to what your setup can actually use:

- **Protocol 1** is capped at **384 kHz**.
- **Protocol 2** can go higher (768 and 1536 kHz) on radios that support it, such as the ANAN-G2.

**192 kHz is the safe, sensible default** and what Zeus uses for one-click discovery connects. Start there; move up only if you want a wider panadapter and your machine keeps up. If you pick a rate higher than the connected radio allows, Zeus automatically drops it back to the highest legal value.

### Choosing the right radio (board) type

For most operators, **Auto-detect** is correct — Zeus reads the board ID the radio reports on the wire and configures itself to match. You only need to set the board type by hand in two situations: a radio that reports a misleading ID, or a hardware combination Zeus can't tell apart on its own.

The board picker (in Manual mode as "Radio type," and in Settings as the **Radio** dropdown) offers:

| Selection | Covers |
|---|---|
| Auto-detect | Let discovery decide (recommended) |
| Hermes Lite 2 | Hermes Lite 2 |
| ANAN G2 / 7000D / 8000D / variant | The OrionMkII (0x0A) family — see below |
| ANAN-G2E | ANAN-G2E |
| ANAN-200D | ANAN-200D (Orion) |
| ANAN-100D | ANAN-100D (Angelia) |
| Hermes / ANAN-10 / ANAN-100 | Hermes-class single-board radios |
| Hermes-II / ANAN-10E / 100B | Hermes-II firmware family |
| HPSDR Metis | Original Mercury+Penelope+Metis stack |

> **Why the board matters:** the board type drives how Zeus encodes transmit drive, sets attenuation, switches filters, and seeds power-amplifier defaults. Picking the wrong board can produce wrong drive levels — or no output power at all. If you set a board by hand, **test at low power first.**

#### The OrionMkII (0x0A) family — picking your variant

Several physically different radios all announce themselves with the same board ID (0x0A) on the network: the ANAN-G2, G2 MkII, G2-1K, 7000DLE, 8000DLE, the original Apache OrionMkII, the ANVELINA-PRO3, and the Red Pitaya. Zeus can't tell them apart from the wire alone, because they're materially different behind that one ID — different power ratings and forward-power calibration.

So when the active board is in this family, a second **Variant** dropdown appears in the Radio settings. Pick the actual radio you own so power-amplifier gain, rated watts, and forward-power readings match your hardware:

| Variant | Radio | Rated |
|---|---|---|
| ANAN-G2 / G2 MkII (Saturn) | The shipping **default** | 100 W |
| ANAN-G2-1K (1 kW) | G2 with the 1 kW amplifier | 1 kW |
| ANAN-7000DLE | 7000DLE (and MKII / MKIII — same here) | 100 W |
| ANAN-8000DLE | 8000DLE (own calibration) | 200 W |
| Apache OrionMkII (original) | The original 100 W OrionMkII board | 100 W |
| ANVELINA-PRO3 | Community board | — |
| Red Pitaya (OpenHPSDR) | Red Pitaya running OpenHPSDR firmware | — |

**G2 is the default**, so if you run a G2 or G2 MkII you can leave this alone. Owners of a 7000DLE MKIII should pick **ANAN-7000DLE** — there is no separate MKIII entry because it behaves identically at this level. If you have a physical 8000DLE, choosing the 8000DLE variant is what makes forward power read correctly.

### The RADIO settings tab

Beyond the connect screen, the Settings area has a **Radio** dropdown (with the live "Detected:" indicator described next) and a **RADIO** tab for board-specific firmware options. These options only appear when the connected board supports them:

- **Hermes Lite 2 Options — Band Volts.** HL2 only. Enables the Band Volts PWM output (replacing the fan-control PWM) so an external amplifier such as the Xiegu XPA125B can follow band changes from Zeus.
- **ANAN-G2 Options.** Verified G2-class radios only. **Dither** and **Random** toggle the ADC dither and digital-output randomizer masks that improve ADC linearity and reduce digital feedback artifacts. **MaxRXFreq** is shown as a live read-only value (Zeus enforces the G2's 0–60 MHz receive ceiling at the VFO). The **ADC1 / RX2 Step Attenuator** sets an independent attenuator for the second receive path, letting you protect RX2/diversity headroom without touching the main attenuator.

Changes on the RADIO tab apply immediately and persist for the next connection.

#### Detected vs. selected, and Override Detection

The Radio dropdown in Settings shows a live **Detected:** indicator next to it — the board discovery actually saw on the wire. If your selection disagrees with what's detected, Zeus shows a **MISMATCH** badge and, by default, **discovery wins** for all drive and power math. That's the safe behavior.

For the rare hardware combination that reports a misleading ID — for example an ANVELINA SDR driving an ANAN-200D amplifier that gets detected as a G2 — tick **Override detection**. Zeus then trusts your selection for everything: drive encoding, attenuation, filter switching, and PA defaults. An **OVERRIDE ACTIVE** badge confirms it. A wrong override can produce incorrect drive or no power, so use it deliberately and test low.

Note that simply changing the board (without override) only reseeds power-amplifier defaults for bands you have never calibrated — your saved per-band PA settings are never touched.

### Switching preference databases

On the desktop app, the connect screen also offers a **Database** picker. This is your settings profile — all your preferences live in a database file. You can switch between profiles, create a **New** one, or **Import** an existing `.db` file. Switching databases restarts Zeus (you'll be asked to confirm), since the new settings load at startup. This is handy for keeping separate profiles for, say, your home G2 and a portable Hermes Lite 2.

### Disconnecting

Press **Disconnect** to drop the link cleanly. If you have a TX audio profile loaded with unsaved edits, Zeus prompts you to save it first (the save needs the live connection), then disconnects. After disconnecting, Zeus returns to the Discover/Manual screen and resumes scanning.
