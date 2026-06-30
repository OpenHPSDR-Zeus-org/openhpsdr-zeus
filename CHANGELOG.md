# Changelog

All notable, operator-visible changes to OpenHPSDR Zeus are documented here.

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

For build artifacts (Windows installers, macOS DMG, Linux tarballs, AppImages),
see the corresponding GitHub Release page.

---

## [0.10.7] — 2026-06-30

> **🎛️ Hardware control + experimental PureSignal for single-ADC radios.** The headline is **MIDI controller and Stream Deck support** — drive Zeus from physical knobs, faders, and buttons. This release also brings **experimental single-ADC PureSignal for the ANAN-10E and ANAN-G2E**, validated end-to-end in Zeus's new virtual-radio test harness and now **looking for on-air feedback from operators with that hardware** (see the note below — it stays disabled by default). Rounding it out: the **Voyeur** net-monitor plugin is now at **v1.1.1** with a selectable NVIDIA Parakeet speech engine and watchword alerts, **6 m and 11 m** band support, and a deep round of transmit-fidelity, FreeDV/RADE, and web-UI improvements.

### 🎛️ New — MIDI & Stream Deck control

- **Control Zeus from a MIDI controller or an Elgato Stream Deck.** Map physical knobs, faders, and buttons to Zeus actions — VFO tuning, band/mode, AF/RF/drive, filter, MOX, and more — with a **Learn** mode that captures the next control you touch. A built-in **Stream Deck** grid lets you assign Zeus commands to the deck's keys. Set it all up under the new **Settings → MIDI** tab. MIDI **input** is available on Windows and macOS; Stream Deck (HID) works on Windows, macOS, and Linux. (PureSignal arm is deliberately excluded from MIDI mapping for safety.) *(#18, #1190)*

### 🛰️ PureSignal — experimental single-ADC support for ANAN-10E & ANAN-G2E (virtual-tested; seeking on-air feedback)

> **What this is, and what it is not.** The **ANAN-10E (HermesII)** and **ANAN-G2E (HermesC10)** have only a *single* receive ADC, which is why PureSignal — a system that needs to listen to the amplifier's output on a feedback receiver — has never been available on them. This release adds an **experimental single-ADC, time-multiplexed feedback path** that shares the one ADC between your receiver and the PureSignal feedback, so these boards can run predistortion for the first time.
>
> **It has been validated end-to-end in Zeus's new virtual-radio loopback harness** (the emulator below) — the feedback frames, ADC-protection bytes, and correction loop all behave correctly in software. **It has not yet been confirmed on real hardware.** Because PureSignal drives real power amplifiers, this path ships **disabled by default behind a safety interlock** — it does not arm itself, and PureSignal still always starts OFF on every board, every session. **We are looking for operators with a real ANAN-10E or ANAN-G2E who want to help validate it on the air.** If that's you, please get in touch via the [Zeus Discord / GitHub](https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus) so we can enable and bench it with you before it goes live for everyone. *(#1209, #1121, #960)*

- **Virtual HPSDR radio emulator (developer/test tool).** A new software radio that speaks the real HPSDR wire protocol (Protocol 1 and Protocol 2), so Zeus — and its automated tests — can connect with **no hardware attached**. This is what let us validate the single-ADC PureSignal paths above in software, and it hardens the whole connect/stream/transmit pipeline against regressions. *(#1208, #1210)*

### 🔎 Voyeur Mode — now v1.1.1, please update

- **The Voyeur net-monitor plugin has a new release — update it.** Voyeur is your automatic net secretary: park it on a net and it records every over, transcribes the speech **on your own machine**, extracts and QRZ-validates callsigns, builds a live check-in roster, and writes a "what was discussed" summary — all offline. The new **v1.1.1** adds a **selectable speech engine** (the proven **Whisper**, plus an opt-in **NVIDIA Parakeet** engine with optional GPU acceleration) and **watchword alerts** that e-mail or push you — with a clip, time, and frequency — when your callsign or a keyword is heard.
- **How to update:** open **Settings → Plugins**, find **Voyeur Mode · Net Monitor**, and **Update** to v1.1.1 (install it from **Browse** if you don't have it yet). Restart Zeus when prompted. The speech/AI engines download in-app on first enable; your existing archive and settings are carried over automatically. Whisper remains the default — switch to Parakeet in Voyeur's settings if you want to try it. *(#1198)*

### 📻 Bands — 6 m and 11 m

- **6 m and 11 m are now in the band selector and band plans**, so you can jump to them with a band button and Zeus keeps per-band memory for them like every other band. *(#1185)*

### 🎚️ Transmit & DSP fidelity

- **Dynamic TX phase-rotator auto-tune** and **TX fidelity auto-tune** improve transmit signal quality, with the auto-tune closure correctly wired through the TX path. *(#1205, #1203)*
- **Smarter Smart-NR auto-notch decisions** — the automatic notch is less prone to grabbing the wrong thing. *(#1202)*
- **Anti-denormal conditioning on the TX VST-engine feed** prevents denormal-number CPU stalls in the transmit audio path. *(#1196)*
- **Cross-board PA-gain guard** — the Hermes-Lite 2's percentage-based drive value can no longer leak into another board's dB drive math. A correctness fix for mixed-board users; no operator-facing default changes. *(#1180, #1187)*
- **DSP pipeline hardening** — the DSP tick is guarded against concurrent runs while a sink attaches or detaches, closing a rare race. *(#1167, #1188)*

### 📡 FreeDV / RADE

- **Cleaner RADE speech** — stop-talk artifacts removed and the RADE speech resampler widened, with a new objective fidelity test harness to keep it honest. *(#1195, #1197)*
- **60 m is forced to USB** for FreeDV (USB-only by regulation on the 60 m channels). *(#1179, #1182)*
- **codec2 native binaries now ship for linux-x64, linux-arm64, and macOS arm64**, so FreeDV works out of the box on more platforms. *(#1174)*

### 🔊 Protocol-2 audio

- **Radio-speaker telemetry + paced egress** — new RX-IQ / tick / speaker instrumentation, and the Protocol-2 radio-speaker send is paced rather than bursty, building on the 0.10.6 decoupling work to chase down speaker underruns. *(#1148, #1200)*

### 🖥️ Web UI & operating

- **VFO lock button** on the multi-DDC VFO panel, so you can lock a receiver's tuning. *(#644, #1183)*
- **Panadapter spot labels** moved below the band-chip row and made size-configurable, so they no longer crowd the top of the display. *(#1175, #1177)*
- **Squelch styling** — the FIX/DYN active styling drops when squelch is OFF. *(#1176, #1178)*
- **Lightning Map** km/mi switch is now a dark toggle that matches the house style. *(#1193)*
- **LO frequency is rounded to an integer at the wire seam**, avoiding sub-Hz drift in the local-oscillator command. *(#1191, #1192)*

### 🛟 Remote diagnostics & connection robustness

- **Richer support sessions** — operator presence and crash shares now include IP, platform, version, and radio, and host signaling + `/admin/crashes` CORS were fixed so maintainer support sessions connect reliably. *(#1170, #1173)*
- **Protocol-1 disconnect fix** — the RX loop now raises *Disconnected* on a non-timeout socket error instead of silently stalling. *(#1204, #1206)*

### 🧰 Under the hood

- **Out-of-process VST/audio engine distribution** — the TX VST engine manifest is now published to Cloudflare R2 via a dispatchable workflow, so the engine and its updates fetch from a stable CDN. *(#1189)*
- **Voyeur speech engines repacked** — upstream sherpa-onnx + NVIDIA Parakeet binaries are repacked into per-platform zips for the in-app installer (the .NET runtime can't unpack the upstream `.tar.bz2`). This is what backs the new Parakeet engine option in Voyeur v1.1.1 above. *(#1198)*

**Credits.** The MIDI controller + Stream Deck support, the experimental single-ADC PureSignal paths for the ANAN-10E and ANAN-G2E, the virtual-radio test emulator, the Protocol-2 radio-speaker telemetry/pacing, the cross-platform codec2 binaries, the Voyeur v1.1.1 engine work, the DSP-tick race guard, the VFO lock, the cross-board PA-gain guard, and the LO-rounding fix are by **Douglas Cerrato (KB2UKA)**. The TX phase-rotator and TX-fidelity auto-tune, the Smart-NR auto-notch hardening, the FreeDV / RADE resampler and stop-talk fixes and fidelity harness, the anti-denormal TX-VST conditioning, the remote-diagnostics enrichment, the 6 m / 11 m bands, and the Lightning Map toggle are by **Christian Suarez (N9WAR)**.

---

## [0.10.6] — 2026-06-29

> **🔧 Hotfix on 0.10.5.** The headline is an audio-device fix for Windows: operators who couldn't select a Windows sound device for receive audio can now switch output devices freely again. This release also folds in every fix — and the one new feature — that landed since 0.10.5: a built-in **DX-cluster client**, HamClock link handling, audio-suite / VST robustness, and a round of web-UI polish.

### 🔊 Audio

- **Windows sound devices are selectable again.** Changing your **output** device no longer fails when the saved **input** (microphone) device is missing or stale — a carried-over device id can no longer block a change to the other side, and re-selecting the same device is now a no-op instead of a glitchy reopen. This fixes the "Zeus won't let me use any of the Windows sound devices for RX audio" reports. *(#1128)*
- **Smoother radio-speaker audio (Protocol 2).** The radio-speaker UDP send is decoupled from the DSP tick, so speaker output no longer rides on DSP timing. *(#1148)*

### 🛰️ New — DX Cluster (direct Telnet)

- **Connect straight to a DX cluster.** A native Telnet DX-cluster client logs in to a DXSpider / AR-Cluster / CC-Cluster node with your callsign (and a password if the node needs one), runs any post-login commands you set, and drops the received spots straight onto the panadapter — no third-party bridge in between. Configure it under **Settings → Network**, beside TCI, with a live connection status and spot count. *(#1140)*

### 🗺️ HamClock

- External links and rig-bridge downloads inside the HamClock panel now hand off to your OS web browser instead of dead-ending in the embedded webview. *(#1112)*
- The bundled map now states plainly that it cannot receive live DX auto-pins. *(#1110)*

### 🎚️ Audio suite & VST

- Editing the VST engine chain is now incremental — adding or removing one effect no longer reloads the entire chain. *(#1153)*
- TX VST plugins are reserved for the out-of-process engine, and a TX VST that fails to load in-process is no longer detached when the engine is installed. *(#1157, #1159)*

### 💬 Chat & 🖥️ Web UI

- The chat relay self-heals a stale QRZ session instead of looping on 403s, and the chat composer no longer starts as a tall multi-row box. *(#1149, #1152)*
- Panadapter callsigns render above the frequency line, honor friend-only gating, and hide together with your own frequency when you choose to hide it. *(#1151, #1155, #1158)*
- Workspace column pitch re-latches if an early width was a transient; the Meter Group tile size survives a Settings round-trip; **Settings → Zeus Digital** now matches the house settings style; and operator-scanned VST3 / AU effects are hidden from the **Settings ▸ Plugins** list. *(#1146, #1160, #1166, #1168)*

**Credits.** The Windows audio-device fix, the audio-suite / VST robustness work, the chat/QRZ self-heal, and the panadapter-callsign handling are by **Christian Suarez (N9WAR)**. The DX-cluster client, the HamClock link/download fixes, the Protocol-2 radio-speaker decoupling, the Zeus Digital settings styling, and the workspace / meter-layout fixes are by **Douglas Cerrato (KB2UKA)**.

---

## [0.10.5] — 2026-06-28

> **🎙️ The digital-modes release.** More than 200 pull requests land here, and the headline is that Zeus now does the digital modes natively: **FreeDV digital voice** (including the new neural **RADE** codec) as a first-class transmit/receive mode, and a built-in **FT8 / FT4** suite — decode, click-to-tune, armed auto-sequencing, an integrated logbook, and spotting — with no external software. Alongside the digital work this cycle brings **up to eight independent hardware receivers**, **full remote operation over the internet**, a **KiwiSDR** slice receiver, a real-time **Lightning Map**, serial **CAT** control, and the **return of external accessory-port switching** that was temporarily out in 0.10.0. The WAV recorder is now an installable plugin rather than core.

### 🎧 Digital voice — FreeDV & RADE

**FreeDV as a native mode.** Select FreeDV like any other mode and Zeus decodes and transmits digital voice end to end — a dedicated FreeDV panel (and pop-out) appears when you select it, with the station list, your decoded/transmitted text, and the controls in one place. *(#881, #885, #896, #940, #1092)*

**RADE V1 — the new neural codec.** Zeus speaks **RADE / RADAE**, the new high-quality machine-learning FreeDV waveform, for full receive *and* transmit, including the LDPC end-of-over callsign. An **AUTO** mode auto-detects RADE versus classic FreeDV so you don't have to guess what the other station is sending. *(#921, #925, #935)*

**One-click modem install.** Install the codec2 modem from inside the app — no terminal, no separate download. *(#888)*

**FreeDV Reporter & polish.** A Stations / Reporter channel shows who's active and spots you to FreeDV Reporter; the passband and crosshair draw on the correct band-convention sideband; decoded audio now follows your AF gain; and an end-of-over TX tail plus a wider speech-band resampler clean up the on-air audio. *(#921, #989, #975, #983, #1008, #990)*

### 📡 FT8 & FT4 — the Zeus Digital suite

**Native FT8 and FT4, built in.** A complete FT8/FT4 client now lives inside Zeus — decode the band, click a signal on the waterfall to tune it, and run a contact. No WSJT-X required. *(#997, #1108)*

**Armed auto-sequencing.** Arm a QSO and Zeus walks the standard FT8 exchange automatically, including sending the terminal **RR73** and standing down at the end of the contact. Set your **callsign and grid** once in the digital settings and they persist across restarts. *(#1108)*

**Built-in logbook + spotting.** Contacts log automatically to the Zeus logbook. Opt in (off by default) to spot your decodes to **PSK Reporter** and **WSPRnet**, and Zeus broadcasts the universal **WSJT-X UDP** stream so external tools — **GridTracker**, **QRZ**, **Wavelog**, **ClubLog**, **N1MM**, **HamClock** — see your QSOs live. *(#1108, #1084, #1088, #1113)*

**Pop-out "Zeus Digital" window.** The digital workspace pops out into its own window, reusing the main panadapter, QRZ lookups, and logbook so there's nothing redundant to learn. *(#1080, #1081)*

> WSPR is present in the engine but **disabled in the UI for this release** (greyed out) — it returns once its reporting map lands. FT8 and FT4 are the live modes today.

### 🎚️ Multiple receivers (multi-DDC)

**Up to eight real receivers.** On Protocol-2 radios Zeus can now run **RX1 through RX8** as genuine independent hardware receivers — each with its own band, tuning, filter, and audio — not just extra views of one signal. *(#837, #848, #850, #856, #913, #924)*

**Built for many receivers at once.** Gang band selection across the receivers you choose, give each its own waterfall dB scale with noise-floor normalization so panes match, pick which receiver has focus, and transmit on any receiver. The MULTI RX controls collapse cleanly back to a single receiver when you're done. *(#894, #926, #941, #947, #949, #963, #971, #1117)*

### 🌐 KiwiSDR & remote operation

**KiwiSDR slice receiver.** Point Zeus at a public KiwiSDR from a map picker and listen to a remote slice of spectrum right inside your workspace — with squelch, zoom/pan, and waterfall normalization. *(#995, #1000, #1005, #1047, #1114)*

**Full remote operation.** Run your Zeus station from anywhere over the internet: live panadapter, **MOX / transmit control**, low-latency **Opus** receive audio, a voice-mic uplink for remote TX, an in-app **LAN Browser**, and WAN connection through the hosted broker with TURN for tricky networks. *(#988, #1002, #1043, #1090, #845, #826, #833, #820, #855)*

### 🎛️ CAT control

**Kenwood TS-2000 CAT — now over serial, too.** Control Zeus (or let it drive logging/contesting software) with Kenwood TS-2000 CAT emulation over TCP **and serial COM ports**, with Thetis CAT1–4 parity. *(#973, #1004, #985)*

### 📻 Receive & DSP

**AGC and noise reduction tightened up.** Auto-AGC-T now servos to the band noise floor the way Thetis does and stops strong-signal loudness pumping; receive AGC behavior was corrected; auto-attenuation matches Thetis's sustained-overload gate; and AF-gain / AGC-T slider drags are rate-capped so they don't click. *(#892, #806, #873, #889, #1055, #939)*

**Smart noise reduction (NR3 / RNNoise).** An opt-in RNNoise-based noise-reduction engine is vendored and built into WDSP, with refined decision logic — and it now ships with a **default model so it works out of the box**, reporting load success rather than silently doing nothing. *(#891, #901, #904, #1036, #1129)*

**P1 receiver fixes.** ADC dither/randomize for LT2208 boards and a corrected preamp bit, plus a guard against a zero-width filter silencing the receiver. *(#1073, #1028)*

### 📤 Transmit & audio plugins

**Plugin hosting everywhere.** Native in-process **VST3** hosting on Linux (with editors), **macOS Audio Unit (AU)** hosting, a higher-fidelity in-process bridge (channel negotiation, latency reporting), self-healing engine provisioning, and recovery from the post-long-TX dead-audio wedge. TX plugin hosting is now **on by default**. *(#864, #827, #866, #1069, #955, #874, #998)*

**TX audio profiles.** Import TX audio profiles and mirror your profile catalog to a folder; recovered/imported profiles are hardened against bad data. *(#1021, #1037, #1023, #1025)*

### 🔌 External accessory ports — back, and expanded

**Port switching is restored.** The external **antenna selection**, **open-collector / PTT-out**, and **hardware PTT-in (footswitch)** control that regressed in the 0.10.0 merge is back — and now spans both protocols. *(#799, #807)*

**More jacks wired.** External **TX audio-source** selection across all radios, the **CW key jack** on Protocol-2, **RX audio to the radio's speaker jack** (Protocol-1 *and* Protocol-2), and the ANAN-10E line-in path. *(#810, #1032, #1045, #1122, #1053)*

### 💬 Operator chat

**Richer chat.** Share photos inline, send up to 60-second **voice messages**, organize into **groups**, and see a live **chat roster overlay** right on the panadapter. Admins get a premium console (global message, clear public chat, ban management, gold callsigns, and a see-all-frequencies override). Chat is in the default layout. *(#994, #1079, #1068, #1070, #900, #911, #933, #1078, #895, #1094)*

### ⚡ Lightning Map & operating aids

**Real-time Lightning Map.** A new panel plots live lightning strikes from the Blitzortung network, with a configurable **proximity strike alert** so you know when storms are closing in on your station. *(#954, #968, #978)*

### 🎙️ Recording

**Digital Recorder.** The in-app tape deck was reworked into a folder-organized recorder with full create/rename/delete, level meters, and one-click transmit of a recording. *(#1022)*

**WAV recorder is now a plugin.** The over-air WAV record/playback feature has moved out of the core and into an **installable plugin** — add it from the plugin catalog if you want it. This keeps the core lean and lets the recorder update independently. *(#1141)*

### 🩺 Diagnostics, support & stability

**Maintainer support sessions.** A new opt-in, **read-only** remote-diagnostics path lets a maintainer help you troubleshoot live — you grant access explicitly each time, and crashes can be auto-shared with your consent. Backed by an on-disk rolling log, a broker relay, and a sidecar that reports presence. *(#1082, #1086, #1093, #1095, #1096, #1097, #1098, #1127)*

**Better crash reporting.** Verbose crash diagnostics, firmware version captured in the Report-a-Problem snapshot, and a live Audio Suite diagnostics panel (latency, DSP load, fidelity). *(#1026, #1059, #1072)*

**Desktop hardening.** The long-standing **close-on-exit crash** (third-party plugin static destructors faulting under AppKit/WebView teardown) is fixed by hard-exiting after an orderly shutdown; the desktop build runs as a GUI subsystem with **no stray console window**; native-audio device opens no longer block startup. *(#1065, #1115, #1076, #1089, #1020, #1091, #1030)*

**Reset & Uninstall Zeus.** A full cross-platform "Reset & Uninstall" that wipes Zeus cleanly with an inline backup to your Downloads folder first. *(#1087)*

**Storage consolidation.** All preference stores moved onto a single shared LiteDB (fixes Windows file-lock failures), with a one-time migration off the legacy encrypted database. *(#1040, #847)*

### 🖥️ Workspace & UI

**Workspace zoom.** A persistent zoom that scales the entire panel grid — set it from the footer or the scroll wheel — so you can fit more on screen or enlarge for across-the-room reading. The top bar and transport scale with it. *(#1003, #1035, #1085, #1109)*

**Saved-layouts library.** Name, save, and manage full workspace layouts (full create/read/update/delete) separately from your workspace tabs, transfer panels between tabs, and keep detached panel windows across restarts. *(#1007, #964, #984, #1113)*

### 📖 Documentation

**The operator's manual grew with the app.** The Zeus Operator's Manual gains a full **Digital Modes** chapter (FreeDV, RADE, FT8/FT4) and new coverage of multi-receiver operation, KiwiSDR, remote operation, CAT, the Lightning Map, and the restored accessory ports. It's rebuilt fresh each release and ships **inside every installer**. *(#985)*

---

### 🙏 Thanks

- **Doug (KB2UKA)** — the digital-modes release is yours: the native FT8/FT4 suite, the FreeDV / RADE transmit-and-receive integration, the multi-receiver build-out, the external-port restoration, and the on-air bench work that proved all of it on real RF.
- **Christian Suarez (N9WAR)** — for the remote-operation stack, the KiwiSDR receiver, the maintainer support-session and diagnostics framework, the chat and cloud infrastructure, and the deep platform work this cycle runs on.
- **Every operator in the OpenHPSDR Zeus community** — thank you for testing on real radios, reporting what broke, and pushing Zeus forward. This release is for you.

---

## [0.10.0] — 2026-06-20

> **🎉 The biggest Zeus release yet — and truly the N9WAR feature release.** Around 145 pull requests land here. This is the release where the original Zeus and Christian Suarez's (N9WAR) Zeus become one, bringing whole new subsystems to operators for the first time: built-in operator chat, DX/POTA/SOTA spotting, space-weather, a true second receiver, AI net-monitoring, one-click self-diagnostics, a GPU waterfall, and a full operator's manual — on top of a deep receive/transmit audio overhaul.

### Operator collaboration & chat

**Built-in operator chat.** Zeus now has a real-time chat system so operators running Zeus can talk to each other right inside the app — public rooms plus private one-to-one direct messages. *(#692, #743)*

**Friends, consent, and moderation.** Add other operators as friends; chat respects a consent model with basic moderation tools so you control who can reach you. *(#750, #751, #765)*

**Optional callsign + frequency sharing.** When chat is on you can optionally broadcast your callsign and current frequency to others — Zeus clearly discloses what will be shared before you enable it, and you can keep your frequency private. *(#765)*

**Real licensed operators only.** Chat runs through a hosted relay that requires a valid QRZ login, with rate limiting to prevent spam, so the people you talk to are real licensed hams. *(#692)*

### Receiver & DSP

**A true second receiver (RX2).** On Protocol-2 radios (ANAN G2 class) Zeus can now run a genuine second receiver on its own slice of spectrum and its own band — full pan/zoom, click-to-tune, and crosshair on both panes, not just a second view of the same signal.

**Smart noise reduction (NR5).** A modernized adaptive noise-reduction engine cleans up background hiss while protecting speech, with a silence gate to mute dead air between transmissions; sensitivity and gating are adjustable.

**Signal smarts on the panadapter.** Zeus can highlight signals, snap your tuning to the nearest one, and mark peaks automatically — easier weak-signal hunting.

**Adaptive RX squelch.** A new receive squelch with a fixed-threshold mode and an adaptive mode that follows band conditions, so quiet bands stay quiet without clipping real signals.

**AGC brought in line with Thetis — one consistent AGC-T control.** Receive AGC now behaves like Thetis: a single authoritative AGC-T control, with Auto-AGC that tracks the band noise floor (and now watches the radio's own ADC peak/gain) for fast, stable leveling. *(#741, #754, #764)*

**A new DSP settings area.** AGC, squelch, and receive-bandwidth controls in plain, labeled sections, plus saveable filter-bandwidth presets.

**Wider spectrum on ANAN G2.** Protocol-2 radios can now run at 768 and 1536 kHz sample rates for a wider visible span.

**Cleaner receive audio.** Multiple fixes eliminate periodic crackle and dropouts from audio-buffer underruns, and stop a "leveler blast" where audio could briefly jump loud. *(#730, #735, #755)*

### Transmit & PureSignal

**Live TX waterfall + configurable TX display.** While transmitting you now get a live waterfall of your own signal, with a choice of what the TX analyzer shows. *(#760)*

**Live transmit EQ analyzer.** A real-time parametric-EQ view of your transmit audio so you can see the shape of your signal as you adjust it. *(#729)*

**TX fidelity advisor with full-chain auto-tune.** A transmit-quality advisor that can automatically tune your entire transmit dynamics chain for cleaner audio. *(#696)*

**Pre-key (MOX) delay for external amplifiers.** An optional delay between keying and RF so an amplifier's transmit/receive relay can finish switching before RF arrives, protecting the amp. *(#630, #633)*

**Lower-sideband transmit fix** so LSB no longer transmits as USB. *(#728)*

**PureSignal stays safe.** PureSignal continues to start disarmed every session — an attempt by the new base to persist its armed state was deliberately reverted to keep the no-auto-arm safety rule. *(#719)*

### Audio processing chain (transmit & receive)

**Plugins on receive, too.** The plugin/VST audio chain — previously transmit-only — now also runs on the receive path, with its own bypass and chain ordering.

**Crash-proof VST hosting.** Third-party VST plugins now run in a separate, self-healing process so a misbehaving plugin can't take down the radio, with native plugin editors that open in place. *(#698, #702)*

**Named TX audio profiles.** All your transmit-audio settings are unified into named profiles you can save, switch between, and create fresh from a one-tap "+ (New)" button, with a prompt to save unsaved changes. *(#701, #744)*

**Off-air monitoring.** Audition your processed audio off the air before you transmit; persistent compressor presets and live in/out/gain-reduction meters per block. *(#591)*

### Spotting, recording & operating aids

**DX/POTA/SOTA spotting panel.** Pulls in DX-cluster and POTA/SOTA activation spots with configurable feeds and filtering, and lets you tune straight to a spot.

**Space-weather panel.** Shows current propagation/space-weather conditions to help judge band openings.

**Logbook ADIF import + search.** Import existing ADIF logs and search/filter with quick QRZ lookups. *(#685, #695, #737)*

**Voyeur net-monitor (AI transcription) as a plugin.** Unattended net monitoring that records a net, transcribes speech to text, enriches it with callsign/QRZ info, and can summarize "what was discussed" — now an installable plugin with an in-app model-download button (no terminal) and a searchable archive with audio replay. *(#601, #602, #604)*

**HamClock as an embeddable panel**, plus a general embed-any-URL panel to dock an external web page inside your workspace.

**Browser-based CW decoder.** A new in-app machine-learning Morse decoder decodes CW straight in the browser.

### Diagnostics & help

**"Report a Problem" self-diagnostic.** A one-click button gathers a redacted health report — connection, board, DSP/audio, transmit/PureSignal state — automatically flags known issues (audio underruns, PureSignal not armed, drive-model mismatches, accidental RX-aux bypass, and more), and hands you a clean report plus plain-language instructions for filing it on GitHub — no log digging required. *(#740)*

**Release-aware update prompts.** Zeus checks the official download site (not a developer repo) and prompts you when a new released version is available, with an in-app updates panel. *(#686, #748)*

### UI & workspace

**Workspace & panel locking.** Lock the whole workspace, or individual panels, so layouts don't get nudged or resized by accident. *(#708, #757)*

**Smoother panels and tuning.** Many workspace fixes (midpoint panel swapping, drag/crash fixes, hidden empty categories, compact icon tabs), refreshed "liquid-metal" meters with a glass-domed needle gauge, butter-smooth view-centered panadapter tuning, and a new GPU-accelerated (WebGPU) waterfall. *(#732, #758, #762, #710, #712, #752, #747)*

**More robust under failures.** A workspace error boundary stops one broken panel from taking down the whole layout. *(#715)*

**Brand refresh.** New canonical Zeus logo (black + white) and brand assets, and the new operator's manual. *(#767, #768)*

### Documentation

**The Zeus Operator's Manual.** A full operator's manual now ships with the project as a living document, regenerated each release. *(#767)*

---

### ⚠️ Heads-up: external port control is temporarily out

External **antenna / open-collector / PTT port switching** that earlier builds had is **not in this release** — it regressed when the codebases were merged and is being re-ported. **It will return in the next update.** If you rely on Zeus to switch antennas or external accessories, hold off or keep your previous setup for now.

---

### 🙏 Thanks

- **Christian Suarez (N9WAR)** — an enormous shout-out. This release is built on your work; the chat, the new panels, the second receiver, and the bulk of what's new here are yours. This is the N9WAR feature release.
- **Brian (EI6LF)** — for the Zeus visual design and the plugin system that the whole audio chain runs on.
- **Doug (KB2UKA)** — for merging the original Zeus and N9WAR's Zeus into one codebase and doing all the integration fixes this cycle (the AGC/AGC-T rework, the receive-audio crackle fixes, the self-diagnostic tool, and more).
- **Every operator in the OpenHPSDR Zeus community** — thank you for testing, reporting, and pushing Zeus forward. This release is for you.

---

## [0.9.1] — 2026-06-14

> **🩹 Hotfix: the waterfall is back on Windows, plus a new amplifier-friendly TX delay.** A regression in 0.9.0 left the waterfall blank on many Windows machines while the panadapter kept working. This release fixes that, and adds an optional pre-key TX delay that protects external amplifiers.

### What's new (in plain English)

**The waterfall works again on Windows.** After upgrading to 0.9.0, a number of Windows operators found the waterfall went blank — sometimes right away, sometimes after a few minutes — while the panadapter kept running normally. The 0.9.0 smooth-tuning rework changed *when* the waterfall's scrolling history first gets allocated on the graphics card, and on Windows that step could be skipped at connect, leaving the waterfall black. It now seeds itself from the first real sweep of data, so it comes up reliably no matter the timing or the graphics driver. It's also been hardened to recover automatically if the graphics context is dropped during a long session (instead of freezing), and to fall back gracefully on virtual/limited GPUs. *(#629)*

**New "TX delay" for external amplifiers.** There's a new **TX delay** control in the **PURESIGNAL → Timing** panel. Set it to a few milliseconds (the operators who reported this found ~30 ms ideal) and Zeus will key your radio first, wait, and *then* let RF out — giving a linear amplifier's transmit/receive relay time to finish switching before any RF reaches it, which prevents "hot-switching" the relay. The default is 0 ms, so nothing changes unless you opt in. It applies to the on-screen MOX and TUNE buttons and is deliberately left out of CW and digital modes. *(#630)*

---

## [0.9.0] — 2026-06-13

> **🛰️ Smooth tuning, cleaner transmit, Raspberry Pi — and Voyeur Mode arrives as an optional plugin.** Panadapter tuning now glides instead of jumping, PureSignal and the transmit path reach desktop-grade quality, Zeus runs on the **Raspberry Pi 4/5** for the first time, and there's a live CW decoder and a reel-to-reel tape-deck recorder. Under the hood, the plugin system gained the hooks — an RX audio tap, host-mediated QRZ, and radio-state read — that let large features ship as optional add-ons instead of bloating every download. The first of those: **Voyeur Mode, the unattended AI net monitor, is now an installable plugin** — opt in only if you want it. Roughly 155 changes since v0.8.4.

### What's new (in plain English)

**Tuning that glides.** Spinning the dial used to make the spectrum jump in steps. Now the panadapter animates smoothly to the new center, the receiver "catches up" faster after a big jump so the display isn't smeared, and the filter passband and dial marker stay locked to the center line the whole time. It just feels right now. *(#597)*

**Voyeur Mode — your automatic net secretary, now an optional plugin.** Voyeur is no longer baked into the main download — install it from **Settings → Plugins** so operators who don't use it carry none of its weight. Once installed, point it at a net and walk away: it records every over, turns the speech into text **right on your machine**, picks out callsigns and validates them against **your** QRZ account, builds a live roster of who checked in, and — with a small local AI model — writes a "what was discussed" summary so you don't re-listen to an hour of audio. Everything's saved in a searchable archive and any over replays with one click. The speech + AI engines download once, on first enable, on every platform (Windows, macOS Intel + Apple Silicon, Linux x64 + arm64). Privacy: transcription and summarization run entirely offline; the only thing that ever leaves your computer is the QRZ lookups you already use. *(Install from the plugin browser; needs Zeus 0.9.0+. If you used the in-core Voyeur from a nightly, the plugin adopts your existing logs, engines, and models automatically — nothing is lost.)*

**CW decoder + redesigned Telegraph Console.** Zeus now decodes received Morse to on-screen text in real time, streamed live from the radio's audio. The CW console was rebuilt and its settings persist across restarts, you get a proper host-side sidetone monitor, and the Hermes-Lite 2's on-board iambic keyer can be configured from Zeus.

**Cleaner, more reliable transmit (PureSignal).** A large body of work brought PureSignal and the transmit path on the desktop build up to the quality of the web build and Thetis: two-tone and voice are noticeably cleaner, PureSignal holds its correction instead of "jumping," your per-radio feedback-attenuation calibration is now saved and restored, and there's a two-tone IMD measurement overlay so you can see your transmit purity. Arming/disarming PureSignal mid-transmit no longer wedges it. *(#556, #557, #558, #559)*

**Runs on Raspberry Pi.** Zeus now has official arm64 Linux builds — an AppImage and a tarball — plus a headless deployment guide, so you can run the backend on a Raspberry Pi 4 or 5.

**Tape-deck recorder.** A new reel-to-reel style panel records your receive or transmit audio to a WAV file and plays it back, either locally or back out over the air.

**Polish all over.** Lots of settings now survive a restart that didn't before — panadapter dB scale, tuning step, band favorites, mic gain and Leveler. Every meter now shares the same smooth needle behavior. macOS signed builds can use the microphone and no longer depend on a Homebrew library being installed. Windows machines with multiple network adapters now discover the radio and draw the panadapter correctly. And the plugin system gained receive/transmit audio taps so audio plugins can see and shape the signal.

### Added

- **Voyeur Mode — now an installable plugin** (`com.kb2uka.voyeur`, in the plugin browser), not a core feature. Unattended net-monitor capture + log management; whisper speech-to-text with callsign extraction and host-mediated QRZ enrichment; roster report, searchable archive, per-over audio replay; a local-LLM "what was discussed" digest; in-app, terminal-free, cross-platform download of the speech + AI engines and models. Adopts any existing in-core Voyeur data (logs/engines/models) on first run.
- **Plugin SDK seams for receive-side feature plugins** — `AudioBlockContext.Receiver` (RX taps can filter to one receiver), `IQrzLookup` (plugins reuse the operator's stored QRZ credentials), and a registered `IRadioStateReader` (current VFO/mode/band). These are what let Voyeur live outside core; SDK bumped to 1.2.0.
- **Raspberry Pi 4/5 support** — linux-arm64 AppImage and tarball in the release matrix, with a headless deployment script and guide.
- **WAV tape-deck recorder** — record RX/TX to WAV and play back locally or over the air, from a reel-to-reel panel ([#579](https://github.com/Kb2uka/openhpsdr-zeus/pull/579)).
- **CW decoder + Telegraph Console** — server-side CW decode streamed to the panel, redesigned console with persisted settings, host-side sidetone monitor, TCI raw-key sidetone, and HL2 on-board iambic-keyer configuration.
- **Two-tone IMD measurement overlay** for transmit-purity checks (Thetis parity).
- **Smooth panadapter tuning** — animated view-center with fast-attack averaging on retune ([#597](https://github.com/Kb2uka/openhpsdr-zeus/issues/597)).
- **Plugin audio pipeline** — read-only RX audio tap, TX audio taps + playback sink, a post-demod insert seam, and an `IRadioController` so plugins can key TX.
- **TCI spots** rendered on the panadapter.

### Changed

- Tuning the dial now glides the spectrum smoothly and keeps the passband + dial marker pinned to center during the move ([#597](https://github.com/Kb2uka/openhpsdr-zeus/issues/597)).
- PureSignal runs continuous auto-mode (Thetis parity) and persists/restores per-board feedback attenuation with manual override ([#557](https://github.com/Kb2uka/openhpsdr-zeus/issues/557)).
- CESSB now matches Thetis/piHPSDR/desktop defaults — off while PureSignal is armed, on otherwise — fixing voice-peak splatter ([#559](https://github.com/Kb2uka/openhpsdr-zeus/issues/559)).
- Many settings now persist server-side across restarts: panadapter dB scale ([#478](https://github.com/Kb2uka/openhpsdr-zeus/issues/478), [#496](https://github.com/Kb2uka/openhpsdr-zeus/issues/496)), tuning step + favorites ([#515](https://github.com/Kb2uka/openhpsdr-zeus/issues/515)), mic gain + Leveler.
- All meters share a unified ballistics hook for consistent needle response.

### Fixed

- **Voyeur transcription produced nothing.** Captured overs are 48 kHz but the bundled whisper engine requires 16 kHz and was silently rejecting every over; Zeus now down-converts in-process so transcription works ([#614](https://github.com/Kb2uka/openhpsdr-zeus/pull/614)).
- **macOS:** signed builds can now use the microphone (`device.audio-input` entitlement); FFTW libraries are bundled in the `.app` so it no longer needs Homebrew ([#421](https://github.com/Kb2uka/openhpsdr-zeus/issues/421), [#543](https://github.com/Kb2uka/openhpsdr-zeus/pull/543)); the bundle is code-signed correctly without `--deep` ([#530](https://github.com/Kb2uka/openhpsdr-zeus/pull/530)).
- **Windows:** radios are discovered and the panadapter draws correctly on hosts with multiple network adapters ([#574](https://github.com/Kb2uka/openhpsdr-zeus/issues/574)).
- **PureSignal/TX:** the wedge watchdog recovers in ~5 s instead of ~37 s; two-tone no longer jumps; the TUN/two-tone pump runs on a real-time thread at the exact DAC rate so the transmit FIFO can't underrun; arming/disarming PureSignal mid-transmit no longer wedges correction ([#558](https://github.com/Kb2uka/openhpsdr-zeus/issues/558), [#559](https://github.com/Kb2uka/openhpsdr-zeus/issues/559)).
- **ANAN-10E** wire byte `0x06` with a low code version is now correctly classified as HermesII ([#545](https://github.com/Kb2uka/openhpsdr-zeus/pull/545)).
- **Protocol 1:** TxFreq refreshes during receive so HL2 external-amp boards drive the band-voltage output ([#361](https://github.com/Kb2uka/openhpsdr-zeus/issues/361), [#503](https://github.com/Kb2uka/openhpsdr-zeus/pull/503)).
- **Linux AppImage:** WebKitGTK GPU acceleration disabled in `AppRun` to fix blank/garbled rendering on some systems.
- Sliders stream their value live during a drag and commit on release ([#485](https://github.com/Kb2uka/openhpsdr-zeus/issues/485)).

## [0.8.4] — 2026-05-25

> **🪟 Windows hotfix.** A single targeted fix for the long-standing ~2-second transmit→receive delay that Windows operators have reported across recent versions — the radio's T/R relay hanging for ~2 seconds after un-keying (MOX or TUNE), and voice transmit sounding slow/robotic. This release is **v0.8.3 plus only this one fix** — no other changes. macOS and Linux were never affected and are unchanged.

### Fixed

- **Windows TX→RX / MOX / relay delay** ([#468](https://github.com/Kb2uka/openhpsdr-zeus/issues/468), [#336](https://github.com/Kb2uka/openhpsdr-zeus/issues/336), [#444](https://github.com/Kb2uka/openhpsdr-zeus/issues/444), [#518](https://github.com/Kb2uka/openhpsdr-zeus/issues/518), [#539](https://github.com/Kb2uka/openhpsdr-zeus/pull/539)). On Windows, the system timer runs at a coarse ~15.6 ms resolution, which throttled Zeus's transmit-data feed to the radio to roughly half the required rate and in uneven bursts. That starved the radio's transmit buffer, so on un-key the radio held its T/R relay for ~2 seconds before returning to receive, and during transmit the half-rate feed made voice play back slow and robotic. macOS and Linux already run a ~1 ms timer, so the identical code paced correctly there — which is why this was Windows-only. Zeus now raises the Windows timer resolution to 1 ms at startup (`timeBeginPeriod(1)`), restoring the full-rate, smooth transmit feed. Verified on an ANAN-G2: the relay now releases instantly on both TUNE and MOX with correct power. Timing-only change — no drive, PA, calibration, or default is affected; it also benefits the Windows Protocol-1 / Hermes-Lite-2 transmit path. Diagnosed and fixed by Doug (KB2UKA).

## [0.8.3] — 2026-05-22

> **🪟 Windows fixes release.** Every Windows operator who installed v0.8.0..v0.8.2 on a fresh Windows machine has been silently broken in one or both of these ways: a missing system runtime stopped Zeus's audio + DSP libraries from loading at all (blank panadapter, no audio), and a growing audio buffer caused the MOX-engage delay to climb to 2-3 seconds after a few minutes of operation. v0.8.3 ships three coordinated fixes that bring Windows responsiveness to parity with macOS and Linux. Mac and Linux operators get a few small bug fixes (PS banner direction, RX trace colour persistence) and the new Audio Suite master bypass; nothing in this release changes Mac or Linux audio behaviour. We're still actively optimising the Windows audio path — see the "What's still next" section below.

### Windows audio responsiveness — the headline

These three fixes work together. Each one is necessary; together they make the Windows operator experience match Mac/Linux for the first time.

- **Installer bundles Microsoft Visual C++ Runtime** ([#452](https://github.com/Kb2uka/openhpsdr-zeus/issues/452), [#453](https://github.com/Kb2uka/openhpsdr-zeus/pull/453)). Zeus's audio (`miniaudio.dll`) and DSP (`wdsp.dll`) libraries are built with Microsoft's C++ compiler and need the Visual C++ Runtime to load. Fresh Windows installs without Microsoft Office, Visual Studio, or another large desktop app silently miss it — and Zeus quietly falls back to a placeholder mode that produces no panadapter, no audio, and no transmit power despite MOX visibly keying the radio. The v0.8.3 installer now bundles Microsoft's `vc_redist.x64.exe` (and `vc_redist.arm64.exe` for ARM Windows) and runs it during install, skipped automatically if a compatible runtime is already present. Installer grows from ~54 MB to ~67 MB; no workaround needed for operators. Diagnosed and fixed by Doug (KB2UKA) after standing up a fresh Windows 11 VM specifically to reproduce operator complaints.

- **WASAPI Pro Audio scheduling hint** ([#450](https://github.com/Kb2uka/openhpsdr-zeus/pull/450)). Tells Windows to treat Zeus's audio thread as a high-priority Pro Audio workload (via MMCSS) and to use the smallest possible WASAPI shared-mode buffer. Drops the WASAPI buffer between Zeus and the speaker from ~100-300 ms down to ~10-30 ms. No-op on macOS (CoreAudio) and Linux (ALSA/PulseAudio). Fixed by Brian (EI6LF).

- **MOX-coupled RX audio ring drain** ([#403](https://github.com/Kb2uka/openhpsdr-zeus/issues/403), [#454](https://github.com/Kb2uka/openhpsdr-zeus/pull/454)). On Windows, the radio's sample clock and the soundcard's clock drift relative to each other (the radio runs slightly faster than most Windows audio devices). Zeus's RX audio buffer accumulates this drift as a growing backlog over a multi-minute session — after 10+ minutes the buffer holds ~1.3 seconds of audio. Pressing MOX or TUNE used to produce 2-3 seconds of stale audio before silence reached the operator's ear. v0.8.3 drains the audio buffer the instant TX engages, so the MOX-engage transition stays snappy regardless of session age. macOS and Linux operators see no change — their audio backends barely drift, so the drain runs on an already-empty buffer. Diagnosed by Doug (KB2UKA) from the reproduction VM; root cause confirmed via the desktop-vs-browser asymmetry that Ronnie (@RonnieC82) spotted in his original report (#403).

### Audio Suite

- **Master Bypass toggle** ([#449](https://github.com/Kb2uka/openhpsdr-zeus/pull/449)). One click disengages the entire Audio Suite plugin chain (Noise Gate / EQ / Compressor / Exciter / Bass / Reverb) instead of clicking six individual bypass buttons. Default on a fresh install is ON (chain inert) so a brand-new operator isn't surprised by an unfamiliar processing chain transforming their first TX before they've configured it. State persists across server restarts. **CFC is downstream in WDSP and unaffected** — master bypass acts on the plugin chain only. Designed by Doug (KB2UKA); see the [Audio Suite wiki page](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Audio-Suite#master-bypass--a-single-toggle-for-the-whole-suite) for operator workflow.

### Protocol & RX fixes

- **HermesC10 / ANAN-G2E receive now works** ([#425](https://github.com/Kb2uka/openhpsdr-zeus/issues/425), [#440](https://github.com/Kb2uka/openhpsdr-zeus/pull/440)). Discovered when Stig (@lb5va) tried Zeus with his G2E for the first time and got connect-but-no-RX. Root cause: an earlier PR mis-mapped the G2E's RX path to DDC2; the N1GP G2E firmware is single-ADC Hermes-class and uses DDC0. Fix routes the RX correctly. Stig bench-confirmed the fix before merge. Thanks Stig.

- **PureSignal stall banner direction corrected and usage documented** ([#438](https://github.com/Kb2uka/openhpsdr-zeus/pull/438)). The "PS stalled" banner pointed the wrong way after a stall. Brian fixed the direction and added an operator-facing usage doc on PS setup. Plus follow-up messaging refinements ([#451](https://github.com/Kb2uka/openhpsdr-zeus/pull/451)).

- **RX trace colour now persists across restarts** ([#437](https://github.com/Kb2uka/openhpsdr-zeus/pull/437)). Operators who changed the panadapter trace colour saw it reset to default on every backend restart. Now persisted server-side alongside the rest of the display settings.

### Cleanup

- **Dead 2-DDC PureSignal scaffolding removed** ([#434](https://github.com/Kb2uka/openhpsdr-zeus/issues/434), [#436](https://github.com/Kb2uka/openhpsdr-zeus/pull/436)). Pre-merge cleanup; no operator-visible behaviour change. Thanks Ramón (EA5IUE).

### Developer / infra

- **Nightly builds** ([#433](https://github.com/Kb2uka/openhpsdr-zeus/pull/433)). A rolling pre-release nightly installer is now published every night from the latest `develop` code at https://github.com/Kb2uka/openhpsdr-zeus/releases/tag/nightly. Useful for testers and operators who want the newest fixes between tagged releases. The "Latest" badge stays on the most recent tagged release (this one). Fixed by Brian (EI6LF).

- **`miniaudio.dll` committed to the repository for fresh Windows clones** ([#448](https://github.com/Kb2uka/openhpsdr-zeus/pull/448)). Mirrors the existing `wdsp.dll` arrangement so a developer who clones the repository and runs `dotnet run --project OpenhpsdrZeus -- --desktop` on Windows gets working audio without a manual build step. Doesn't affect installed-app behaviour (the release pipeline always rebuilds the natives from source). Fixed by Brian (EI6LF).

### What's still next for Windows

These three fixes get Windows to parity, but we're not stopping. The MOX-coupled drain is a workaround for the underlying clock-drift; a proper asynchronous resampler in `NativeAudioSink` that tracks the soundcard clock and keeps the ring at a steady target depth would eliminate the drift entirely instead of papering over it on MOX edges. Tracked as future work; the current fix completely hides the symptom while the resampler is built.

We're also keeping an eye on operator reports for any remaining Windows-only quirks. **If you're on Windows and something feels off after upgrading to v0.8.3, please [open an issue](https://github.com/Kb2uka/openhpsdr-zeus/issues/new/choose)** — the v0.8.0..0.8.2 Windows bugs got missed because every maintainer was on Mac or Linux; the more Windows operator voices we hear, the faster the next refinement lands.

### Thanks

- **Brian (EI6LF)** — WASAPI Pro Audio fix (#450), nightly-builds infrastructure (#433), `miniaudio.dll` clone-and-run fix (#448), PS stall direction + usage doc (#438), PS messaging refinements (#451)
- **Doug (KB2UKA)** — Windows responsiveness diagnostic from a reproduction VM, VC++ Runtime bundle (#453), MOX-coupled ring drain (#454), Audio Suite master bypass (#449), HermesC10 / ANAN-G2E RX fix (#440), RX trace colour persistence (#437)
- **Ramón (EA5IUE)** — PS scaffolding cleanup (#436)
- **Stig (LB5VA)** — bench-testing the G2E receive fix on his own radio before merge (#425)
- **Ronnie (@RonnieC82)** — the original "desktop slow, browser snappy" observation in #403 that pointed straight at the WASAPI backend asymmetry; thank you for the persistence

---

## [0.8.2] — 2026-05-20

> **🛠 THIS IS A HOTFIX FOR v0.8.0/v0.8.1's Audio Suite installer.** If you installed v0.8.0 or v0.8.1 and clicked **Download Audio Suite**, you got 5 of the 6 audio plugins — Noise Gate was silently skipped. v0.8.2 fixes the one-click installer to deliver all six, and also catches Bass / Exciter / Reverb up from v0.1.0 to the v0.2.0 versions that shipped alongside Noise Gate on 2026-05-19. See [0.8.0](#080--2026-05-19) below for the full feature list — v0.8.2 contains only the fix described here.

### Fixed

- **Download Audio Suite now installs all six audio plugins, not five** (#405). The `AUDIO_SUITE` array in `DownloadAudioSuiteButton.tsx` was never updated when Noise Gate shipped with v0.8.0, so one-click installs left operators without the gate plugin. The same fix bumps the URLs for Bass / Exciter / Reverb from v0.1.0 to v0.2.0 (which shipped on 2026-05-19 alongside Noise Gate) so the suite now distributes the current releases. If you have v0.1.0 of any of those three already installed, the suite installer will mark them as "already present" and skip — uninstall the v0.1.0 version via Plugins → Browse and click Download Audio Suite again to pick up v0.2.0.

  Manifest IDs and versions distributed by Download Audio Suite as of v0.8.2 (in install order; conventional voice-chain signal flow):
  - `com.openhpsdr.zeus.samples.noisegate` **v0.1.0** (new in suite)
  - `com.openhpsdr.zeus.samples.eq` v0.2.0
  - `com.openhpsdr.zeus.samples.compressor` v0.1.2
  - `com.openhpsdr.zeus.samples.exciter` **v0.2.0** (bumped from v0.1.0)
  - `com.openhpsdr.zeus.samples.bass` **v0.2.0** (bumped from v0.1.0)
  - `com.openhpsdr.zeus.samples.reverb` **v0.2.0** (bumped from v0.1.0)

  Bench-verified end-to-end: wiped the plugins directory of all six audio plugins, clicked Download Audio Suite, all six installed fresh and loaded on next desktop restart.

---

## [0.8.1] — 2026-05-19

> **🛠 THIS IS A SAME-DAY HOTFIX FOR v0.8.0.** If you installed v0.8.0 earlier today and saw `openhpsdrzeus.exe` linger in Task Manager after closing the Zeus window on Windows, **install v0.8.1 — it's the fix**. macOS and Linux operators were not affected; upgrade is still recommended for the cleaner shutdown behaviour. See [0.8.0](#080--2026-05-19) below for the full release feature list — v0.8.1 contains only the fix described here.

### Fixed

- **Windows: OpenhpsdrZeus.exe no longer lingers in Task Manager after closing the desktop window** (#400, **@brianbruff**). v0.8.0 registered an `AppDomain.CurrentDomain.ProcessExit` handler in both `--desktop` and `--server` modes that called `window.Close()`. That event fires *during* exit, after `Main` returned and Photino's native state was being torn down — the handler then re-entered a half-disposed WebView2/COM apartment on the `[STAThread]` main thread, deadlocking for ~30 s. v0.8.1 deletes the handler in two lines: `Console.CancelKeyPress` already handled Ctrl-C translation; `ProcessExit` was never the right hook for shutdown.

  Bench-verified on Windows 11: process exits in ~2.2 s after window close (down from >30 s of hang). Subsequent relaunches no longer pile up orphan processes.

---

## [0.8.0] — 2026-05-19

> **🎉 Headline:** in-process **Audio Suite** with live pre-MOX meters and preview, **dual desktop icons** on every platform (Zeus + Zeus Server with a Photino status window), single-binary Zeus that hosts both modes, and Ramon's smoothed-SWR meter improvements.

### Added — Audio Suite (issue #332)

- **Live pre-MOX meter tap + preview** (#390). Every audio-chain plugin's IN / OUT / GR meters animate continuously with MOX off so you can dial dynamics, gates, and gain staging without going on the air. Toggle **Preview** in the suite-window header to hear the processed chain through your RX playback sink — share-with-receive convention means muting RX mutes preview.
- **Audio Suite floating window + reorderable chain** (#391). Open it from the new button on Audio Tools. Drag tiles at the top to reorder the chain — signal flow really changes when you reorder, not just metadata. `ChainOrderService` with canonical-vs-runtime separation so uninstalled plugins keep their slot for reinstall.
- **Render tx-audio-tools.chain plugins above CFC** (#373). Audio Suite plugins surface in their own region above the WDSP CFC strip on the Audio Tools panel.
- **One-click Download Audio Suite installer button** (#376). Drops EQ + Compressor + Exciter + Bass + Reverb in a single click via the official Kb2uka plugin registry.
- **Plugin-settings persistence fix** (#387). LiteDB upsert-by-`_id` bug was silently inserting new rows on every `SetAsync` instead of updating — operator dial-in could revert to defaults across desktop restarts. Now: atomic delete-by-key + insert, with descending-ID read so any pre-fix duplicates resolve to the latest value.
- **Restart-required modal on every plugin install.** The "Please shut down Zeus and restart" dialog that previously fired only after the Download Audio Suite bundle install now also fires after a single-plugin install from the Plugins panel (Settings → Plugins → Browse / Install). New endpoints + AssemblyLoadContexts only register at backend startup, so the operator always gets the same explicit reminder.

### Added — plugins shipping with this release (OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins)

- **EQ v0.2.0** — Input + Output gain stages plus a live FFT spectrum behind the curve.
- **NoiseGate v0.1.0** — new plugin. Peak-envelope detector with built-in 3 dB hysteresis, hold timer, asymmetric attack/release gain slew, range knob, output trim. Threshold rail UI with **OPEN / HOLD / CLOSED** state pill.
- **Bass v0.2.0**, **Exciter v0.2.0**, **Reverb v0.2.0** — IN/OUT gain trims + vertical IN/OUT peak meters retrofitted.

### Added — installers

- **Dual desktop icons on every platform** (#392). Windows / macOS / Linux all ship a **Zeus** icon (full Photino window, `--desktop`) and a **Zeus Server** icon (backend + small Photino status window listing the LAN URLs with a Stop Zeus button, new `--server` flag). Headless service mode (`OpenhpsdrZeus` with no flag) is byte-identical to before — systemd, Docker, and Raspberry Pi deploys unaffected.
- **Single-binary OpenhpsdrZeus** (#352, **@brianbruff**). Multi-phase rollup that collapsed Zeus.Server + Zeus.Desktop into one executable hosting both modes, vendored miniaudio for native RX sink + TX mic capture without a browser tab, and shipped the desktop-mode audio opt-out path.
- **Desktop session share over LAN HTTPS** (#363, **@brianbruff**). A phone or laptop on the same network can pick up the desktop session while the operator is away from the shack PC.
- **Always rebuild SPA before Publish** (#365, fixes #350).

### Added — plugin system foundation (**@brianbruff**)

- **Unified plugin system rebuilt from contracts down** (#368). `Zeus.Plugins.Contracts` with `IBackendPlugin` / `IUiPlugin` / `IAudioPlugin`, in-process VST3 via vendored vst3sdk, `AudioPluginBridge` on the WDSP TX seam, browsable registry pointing at the new plugins repo.
- **Frontend plugin runtime** (#370) + plugin-panel-tile persistence across layout reparse (#375).
- **RF2K-S amp extracted to a plugin** (#374) — refactor; behaviour unchanged.

### Added — rotator + mobile + meters (**@brianbruff**)

- **Rotator Dial panel** (#385) — pure compass.
- **Rotator: persist rotctld config server-side; trust backend status** (#388).
- **Mobile Tools drawer** (#386) — replaces the old Settings tab with per-tool pages.
- **Analog meter: radio-aware PO scale + bolder typography** (#381), **2× S-readout grid + locked PO/SWR slots** (#384).
- **Light-mode improvements + theme saved in DB** (#358), **meter-arc visibility + Add-Meter modal stays open** (#364).
- **Rotator map: Alt+arrow zoom on hero map; compass map interactive** (#344).

### Fixed — TX meters (**@rampa069**)

- **Smoothed-SWR + per-mode trip thresholds** (#367). Smoothed ADC drives the SWR axis (peak-hold stays for watts); MOX 2.5:1 / 300 ms grace; TUN 6:1 / 500 ms grace.

### Fixed — PureSignal HL2 (**@brianbruff**)

- **Match mi0bot exactly** (#380) — `hw_peak 0.233` and no dance cooldown. Aligns the HL2 PureSignal path with the canonical mi0bot openhpsdr-thetis fork.

### Fixed — TX audio / persistence

- **Gate `NativeMicCapture` behind TCI recency** (#379). Fixes the desktop dual-feed regression from #346 — same family as v0.7.5's hotfix.
- **Drive + TUN-drive % survive server restart** (#359), **hydrate from server** (#360). Kills the frontend-clobbers-server-on-connect pattern.

### Repo hygiene + docs

- **CODEOWNERS** added (#366) — `* @Kb2uka`.
- Docs: stale `Zeus.Server` paths updated to `OpenhpsdrZeus` + `Zeus.Server.Hosting` (#383, **@rampa069**).

### Wiki

- New **[Audio Suite](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Audio-Suite)** page — full workflow, preview rules, per-plugin reference (Gate / EQ / Comp / Exciter / Bass / Reverb), gain-staging guide. Linked in the Transmit sidebar.

### Contributors

Huge thanks to **@brianbruff (EI6LF)** for the plugin-system rebuild, the single-binary rollup, LAN-share-over-HTTPS, rotator + mobile + light-theme polish, and the HL2 PureSignal mi0bot alignment. To **@rampa069 (Ramon Martinez)** for the smoothed-SWR fix that ratified the no-G2-bench-access regression-risk case, and for the docs path cleanup as a 3rd-contribution regular. The Audio Suite plugin work, installer dual-icons, and persistence fixes are KB2UKA's.

---

## [0.7.5] — 2026-05-16

> **🛠 THIS IS A HOTFIX RELEASE FOR v0.7.4.** If you installed v0.7.4 earlier today and heard chopped / intermittent carrier on SSB mic or WSJT-X / JTDX over TCI on the Photino-based **Zeus.Desktop** build, **install v0.7.5 — it's the fix**. Browser-based UI users on v0.7.4 were unaffected. **All v0.7.4 Zeus.Desktop operators should upgrade.**

Huge thanks to **@rampa069 (Ramon Martinez)** for catching this on his bench the day v0.7.4 went out and filing a precise reproduction (#346) with on-wire packet captures that pinpointed the dual-feed pattern. That kind of report-with-evidence is what made the same-day hotfix possible.

### Fixed

- **Choppy TX audio on Zeus.Desktop after v0.7.4 — both SSB mic uplink and TCI (WSJT-X) transmission.** *(KB2UKA-Agent, PR #351, fixes #346 — reported by @rampa069)*
  - **Root cause:** v0.7.4's PR #343 (`MoxStateFrame` broadcast for TCI TX-meter visibility) flipped the frontend `moxOn` flag on **any** MOX edge, including TCI-driven ones. The browser mic uplink worklet was gated on `moxOn`, so a TCI key-up caused the browser to start pushing silent mic PCM blocks into `TxAudioIngest._accumulator` in parallel with the TCI audio path. Both producers appended into the same `_accumulator` under `_sync`, interleaving WSJT-X tone blocks with near-silence blocks at 50 Hz. WDSP TXA processed alternating signal/silence, producing the audible "carrier cuts in and out every ~1 second" symptom and the on-wire signature `peak=1386 / 380 / 500 / 0 / 32641 …`.
  - **Fix:** new `localMicArmed` flag in `tx-store`, raised only by local operator interaction (`MoxButton` click, spacebar PTT, `MobilePttButton` press) and lowered on the corresponding release, on server-forced MOX-off (`MoxStateFrame` off-edge, SWR trip, TCI key-up), and on WS close. `use-mic-uplink.ts` is now gated on `localMicArmed` instead of `moxOn`. Server-driven MOX-on (the TCI case) never raises the flag — that asymmetry closes the dual-feed path without breaking any of the v0.7.4 TCI TX-meter wiring. Six frontend files, no backend changes. `MoxStateFrame` wire format and the `moxOn` UI indicator are untouched.

---

## [0.7.4] — 2026-05-16

A correctness, capability, and polish release. **Hermes-class Protocol-1 boards
(ANAN-10, ANAN-10E, ANAN-100B/D, ANAN-200D, OrionMkII family, Brick2) transmit
correctly for the first time on Zeus** — two compounding bugs that capped them
at a fraction of rated power are fixed, so PR #324 and PR #338 together make
non-HL2 P1 a first-class radio. **WSJT-X over TCI now keys cleanly on every
ExpertSDR2-convention digital client** (MSHV, JTDX-TCI, etc.) and FT8 phase
continuity is rock-solid thanks to demand-driven, real-time TX_CHRONO pacing.
**One-button WWV auto-cal** lands the operator's dial inside a few Hertz of
truth without typing a number, on every board. **HL2 PureSignal stops
breaking up** out-of-the-box — the auto-attenuate dance now has hysteresis, a
cooldown, MOX-drop recovery, and a stall warning instead of oscillating in
silence. Plus: a new **Rotator Compass** map panel with one-click short-path /
long-path slew from the QRZ Lookup card, an opt-in **brushed-silver light
theme** with an operator-tweakable colour palette, and a **30m WARC band-plan
fix** so digital modes (FT8, RTTY) key correctly on Hermes-Lite 2.

### 👋 Welcome new contributor

**Ramon Martinez (rampa069)** lands his first two PRs in this release — and
they're substantial. PR #319 rewrote our TCI server's TX_CHRONO pacing into a
real-time, demand-driven loop and fixed three on-the-wire issues that kept
ExpertSDR2-convention digital clients (MSHV, JTDX-TCI) from keying through
Zeus. PR #339 fixed a long-standing 30m WARC band-plan bug where segments
labelled "CW/Digital" were silently rejecting digital-mode TX, and shipped a
new `ModeRestriction.CwAndDigital` wire value plus 13 segment corrections
across IARU R1 / R2 / R3 and US FCC region files. Welcome aboard, Ramon — and
thanks for the careful, well-tested patches.

### Fixed

- **Hermes-class Protocol-1 boards no longer treated as HL2 post-connect.** *(KB2UKA-Agent, PR #324, closes #294 on this release-merge — reported by @RonnieC82)*
  - `Protocol1Client._boardKind` was never updated from its constructor default after the socket handshake, so any non-HL2 P1 radio (ANAN-10, ANAN-10E, ANAN-100B / 100D / 200D, OrionMkII, Brick2 over P1) used the HL2 drive profile downstream. Result: wrong PA-gain table, wrong drive quantization, RX worked but TX was capped at a tiny fraction of rated output.
  - Fix: `RadioService.ConnectAsync` calls `client.SetBoardKind(discoveredKind)` after handshake (mirrors the Protocol-2 pattern), `/api/connect` plumbs `boardId` through, and the frontend P1 connect path passes the discovered board byte. Connect logs now show `board=Hermes` (or HermesII, etc.) instead of HermesLite2, and PA / drive profiles route to the correct table.
  - Backwards-compatible: an older client that sends no `boardId` keeps the previous Unknown behaviour, so manual-connect flows still work.

- **Hermes / HermesII / Metis: drive slider now sweeps full power.** *(Brian Keating / EI6LF, PR #338)*
  - `PaMaxPowerWatts` was seeded at `10` on Hermes, HermesII, and Metis, but the `HermesGains` PA-gain bracket they share (38.8 dB on 10 m) was calibrated against a **100 W target** in Thetis. Zeus's drive slider is "percent of `MaxPowerWatts`", so at 100 % it only ever asked the DAC for ~32 % of full amplitude. A 10 W Brick2 made roughly **1 W at max TUN**.
  - Fix: bump `MaxPowerWatts` to `100` for those three boards so byte=255 is reachable at slider=100. Physical 10 W radios self-clamp to their rated max as in Thetis. Operators on Hermes-class radios will feel this immediately — the slider is no longer secretly running at one-tenth amplitude. New `docs/lessons/hermes-pa-maxwatts.md` explains the math so future board seeding doesn't repeat the mistake. HL2, ANAN-100/100B/100D/200D, G2 / G2-1K / 8000DLE, and ANAN-G2E are unaffected (already correct).
  - Together with PR #324, this is what makes ANAN-10E and other Hermes-class P1 boards a first-class Zeus radio. `RonnieC82` bench-verified earlier in the cycle.

- **HL2 PureSignal stops breaking up out-of-the-box.** *(Brian Keating / EI6LF + KB2UKA, PR #341)*
  - Two compounding bugs in HL2 PS calibration. (1) The shipped `hw_peak` default (`0.233`, blanket-inherited from mi0bot) was too high for typical drive — the bench-measured DDC3(tx) envelope at standard 2-tone is ≈ 0.190, so the calcc bin never filled, COLLECT never advanced, and PS sat silently armed-but-dead. (2) Once `hw_peak` was corrected, the legacy `128 / 181` dead-band combined with a full `ddB` attenuation step caused the auto-attenuate dance to oscillate forever, disabling PS every ~300 ms via `SetPsControl(reset=1)` — heard on-air as a once-per-second IMD3 bloom.
  - Fixes (every deviation from mi0bot annotated in-code with the bench measurement that justified it):
    - `hw_peak` default `0.233 → 0.2500` on HL2 (P1 and P2 paths).
    - Dance dead-band `128 / 181 → 110 / 195` (P1 and P2) so a single ±2 dB correction lands in-window.
    - 3-second per-dance cooldown.
    - MOX-drop-mid-dance recovery: if MOX drops while the state machine is in `SetNewValues` / `RestoreOperation`, re-arm PS instead of leaving it in `reset=1`.
    - Stall warning: PS armed + keyed for >5 s with `CalibrationAttempts == 0` surfaces `PsCalibrationStalled` on the state DTO and shows a banner in the PURESIGNAL panel pointing the operator at HW Peak.
  - **Operator-calibrated `HwPeak` now persists per board across restarts.** *(KB2UKA, in PR #341)* — `PsSettingsEntry.HwPeakByBoard` maps each connected board kind to its tuned value, so an HL2 + ANAN-G2 + RF2K-S chain that needed `0.655` doesn't have to be re-dialled every backend restart. Older rows without the map fall back to the resolver default.
  - The `correcting` pill no longer latches on stale `info[14]` after MOX drop (PR #341 also folds in a small gate refactor for that).

- **TCI TX meters and SWR now update during WSJT-X / JTDX transmissions.** *(KB2UKA-Agent, PR #343, fixes #342)*
  - `ImmersiveMetersPanel` gates forward-power and SWR display on `useTxStore.moxOn || tunOn`. When a TCI client keyed via `TciSession → TxService.TrySetMox`, the backend flipped to TX and `TxMetersV2Frame` payloads were correct — but no MOX state push went to connected WebSocket clients, so `moxOn` stayed `false` and the meter panel rendered `—` / `1.00` for the whole QSO. (SWR trip protection was unaffected; it rides `AlertFrame`.)
  - Fix: new `MoxStateFrame` (`0x1C`) broadcast from every MOX / TUN edge in `TxService` — `TrySetMox`, `TrySetTun`, two-tone arm/disarm, and `TryTripForAlert`. Frontend's existing `setMoxOn` / `setTunOn` store actions enforce the MOX/TUN mutual exclusion. WSJT-X-via-TCI now drives the panel correctly.

- **WSJT-X / MSHV / JTDX-TCI now key cleanly on every digital client.** *(Ramon Martinez / rampa069, PR #319)*
  - Five fixes in the TCI server, several of which were independently load-bearing:
    - **Demand-driven, real-time TX_CHRONO pacing.** The server now sends TX_CHRONO sync frames only when MOX is on and the TCI session is the TRX source, paced by stopwatch-tick spacing instead of integer-ms intervals. Each fire advances by a fixed sample increment instead of `nowTicks` — load-bearing because resetting to wall-clock would let OS-timer drift accumulate and overflow `TxAudioIngest._accumulator` every ~1.8 s, breaking FT8 phase continuity.
    - **`tx_enable` inbound echo.** ExpertSDR2-convention clients (MSHV 2.76, JTDX-TCI) send `tx_enable:<rx>,true;` after handshake and wait for the server to echo it back before issuing `trx:0,true;`. Zeus was silently dropping the inbound request — MOX / Tune on those clients were no-ops while RX worked fine. New `HandleTxEnable` mirrors the existing `HandleTune` shape.
    - **`BuildTxChrono` length contract.** Updated from `0` to `4096` (2048 samples × 2 channels) to match the modern Thetis length semantics from SunSDR TCI Protocol v2.0 §3.4.
    - **WSJT-X oversized buffer decode.** WSJT-X allocates `length * 2` floats but only writes `length`; we now cap `floatCount` at the declared length so the trailing zero pad doesn't corrupt the audio path.
    - **QRP-accurate `tx_power` / `tx_forward_power` / `tx_sensors`.** Format upgraded from `int` to `F1` (0.1 W decimal) so WSJT-X-style clients that parse `split(".")[1]` for the fractional part show accurate low-power readouts.
  - Plus an Apple-Silicon-relevant memory barrier in `TxAudioIngest._onWdspConsumed` (`Interlocked.Exchange` / `Volatile.Read` for the cross-thread handoff between the TCI timer thread and the WDSP worker — x86 TSO hid the bug; Pi-class ARM does not), and post-call truth in TCI MOX / TUN echoes so a mid-disconnect or no-op set never leaves the client with a desynchronised view.

- **30m WARC: digital modes (FT8, RTTY) now key correctly on bands labelled "CW/Digital".** *(Ramon Martinez / rampa069, PR #339, closes #337 on this release-merge — reported on Hermes-Lite 2)*
  - The shipped band-plan JSON marked 30m WARC and several US FCC sub-bands as `CwOnly`, even though the labels said "CW/Digital". Result: an operator on HL2 trying to FT8 at 10.130 MHz had MOX silently denied with no on-screen indication.
  - Fix: new wire value `ModeRestriction.CwAndDigital = 4` (existing values keep their byte positions, signed off by @Kb2uka in the issue). `BandPlanService.ModeMatchesRestriction` accepts CW + DIG, rejects phone (USB / LSB / AM / SAM / DSB / FM). `TxService.CheckBandGuard` now also broadcasts `AlertFrame(AlertKind.OutOfBand, …)` when blocking, so out-of-band attempts surface in the same banner UX as SWR trips. Frontend `BandPlanEditor` dropdown gains the fifth option; client-side `modeMatchesRestriction()` mirrors the server. Shipped JSON updated across 13 segments in IARU R1 / R2 / R3, US FCC Extra, and US FCC General. 17 new unit tests pin the matrix.

- **RF2K-S amp panel no longer flaps green-red on transient poll blips.** *(KB2UKA, PR #335)*
  - `Rf2kService.PollOnceAsync` was flipping `Connected = false` on **any** single failed HTTP fetch across its 8 sequential endpoints (`/info`, `/data`, `/power`, `/tuner`, `/operate-mode`, `/operational-interface`, `/antennas`, `/antennas/active`). A 3 s timeout on any one of them, a transient RST, or a momentary 5xx from the amp's embedded web server produced a panel-visible flap every 5 s — even though the next poll cycle reconnected cleanly.
  - Fix: tolerate up to 3 consecutive failed polls before flipping the Connected light; snapshot fields are kept during the tolerance window so the UI shows last-known values rather than blanking on each blip. Every poll failure is now logged at warning level (previously it was silently stashed into `_error`), so operators can see *which* endpoint is flaking. Worst-case latency before a real disconnect is reported is ~3 × `PollingIntervalMs` (≈ 3 s) — well under operator-noticeable. Live-bench verified on the maintainer's amp.

### Added

- **Per-radio frequency calibration with one-button WWV auto-cal.** *(KB2UKA, PR #334, closes #325 on this release-merge)*
  - Adds a dimensionless multiplicative `FrequencyCorrectionFactor` near 1.0, applied host-side at the Protocol-1 and Protocol-2 `SetVfoAHz` seams, persisted per radio in `PreferredRadioStore`. Modelled on Thetis / piHPSDR / deskHPSDR — applied to the wire **without touching any clock or sample-rate register**. Clamped to ±100 ppm, NaN / Infinity rejected. Older rows hydrate as `1.0` so upgrading operators see no shift.
  - **One-button auto-cal** modelled on Thetis's WWV procedure (`console.cs:9779-9854`): click `Calibrate` in Settings → CALIBRATION; backend snapshots operator state, tunes to WWV 10 MHz USB at zoom 16, settles 1.5 s, captures the panadapter, finds the spectral peak, computes `factor = 1 + offsetHz / 10e6`, persists it, re-tunes so the new factor lands on the wire immediately, then restores operator state in `finally`. Sanity bounds on peak strength (−90 dBFS noise floor) and offset (±1 kHz at 10 MHz). Operator types nothing.
  - **Per-board scope**: applies to every Protocol-1 board (Radioberry / Hermes / ANAN-10 / 10E / 100B / 100D / 200D / OrionMkII / HermesC10 / HL2 / Metis) and every Protocol-2 board through the same shared apply site. Zero per-board branching.
  - New endpoints `GET / POST /api/radio/frequency-calibration{,/calibrate,/reset}`. No Zeus.Contracts DTO change. 23 new tests, all green.
  - Out of scope for v0.7.4 (called out in #325): automatic reference-frequency selection (5 / 10 / 15 / 20 MHz WWV/WWVH), external 10 MHz reference, per-band cal tables, XVTR offsets.

- **Rotator Compass panel + one-click SP / LP slew from QRZ Lookup.** *(Brian Keating / EI6LF, PR #331)*
  - New full-bleed Esri satellite Leaflet map (category: Tools) showing operator QRZ home + lookup target, great-circle arc, and the live beam-wedge overlay. Right-rail column carries a `DIST` badge, side-by-side `SP NNN°` / `LP NNN°` buttons, a numeric `HDG` input + `GO`, and `STOP`. A `NOW NNN° → tgt` chip at top-left tracks live rotator status. Right-click any marker for "Rotate to NNN°".
  - **Inline `SP` / `LP` buttons in the QRZ Lookup footer** when `rotctld` is connected and both home + contact have lat/lon — one-click slew is reachable straight from the lookup card alongside Clear / Log QSO. Row collapses gracefully when rotctld is disabled.
  - New shared brass-instrument-plate button family (`.rc-btn` + `--path` / `--go` / `--stop` / `--neutral`) — gold-on-black for primary rotate actions, white-on-black for supporting Clear / Log QSO, all on the same near-black plate as the panel header rail.

- **Brushed-silver Light theme + operator-tweakable colour palette.** *(Brian Keating / EI6LF, PR #340)*
  - Opt-in **Light** theme alongside the existing v3 Lifted Dark chrome (which is preserved byte-for-byte as the default). Chassis surfaces reskin to a silver palette taken verbatim from the Zeus Lifted Dark v4 reference; **display wells (panadapter, VFO, S-meter, gauges, LED meters) deliberately stay dark** in light mode so they still read as lit instruments embedded in a silver front panel — the classic transceiver look.
  - **Operator-facing colour palette tweaker** in Settings → DISPLAY: native colour pickers for Accent / TX / Power / Amber / Cyan / OK · Green / Orange, with per-row Reset and a global Reset-all. Overrides persist in localStorage (`zeus.theme.overrides`) and apply across both themes; defaults match `tokens.css` verbatim so this surface is opt-in only.
  - Seeds `data-theme` on `<html>` before React mounts, so a saved light preference paints immediately on reload with no flash of dark chrome.

### Known issues

- The maintainer's bench tests for PR #341 covered 2-tone source and rode every code path. Voice-source / HL2-variant-other-than-EI6LF's coverage is the v0.7.5 ask — if your HL2 dances or stalls differently, please file an issue with a 10–15 s `SetPsControl` log.
- Frequency calibration's auto-cal hard-codes 10 MHz as the reference. Operators on the wrong side of the planet (no propagation to WWV / WWVH) currently get a `NoSignal` result and the persisted factor is left unchanged. Multi-reference + external 10 MHz support is queued for a future release per #325.

### A teaser for what's next

Big things coming in the next release — including one many of you have been asking about for a while. Stay tuned.

---

## [0.7.3] — 2026-05-14

A polish + correctness release. Major visible refresh of the **v3 Lifted Dark
theme** — flat near-black chrome, brass instrument-plate panel headers, blue
VFO aurora behind 200-weight Inter digits, warm amber meter glow. Under the
chrome: **meter rendering smoothed** with 90 ms EMA + 1.5 s peak hold; **HL2
Band Volts PWM** is now an in-app toggle for external amplifier band
following; **macOS users can launch the backend from any shell again** (the
LAN cert handshake stopped routing through the keychain); and a small **MOX
edge click on RX audio is gone**.

### Fixed

- **macOS: backend launches from non-GUI shells again.** *(KB2UKA, PR #323)*
  - `LanCertificate.cs` was passing `X509KeyStorageFlags.PersistKeySet` on certificate load, which triggers `Interop+AppleCrypto+X509MoveToKeychain`. On macOS that call fails outright with `"User interaction is not allowed."` whenever the backend's parent process isn't tied to the window server — CI runners, SSH, terminal multiplexers, and anything launched from VS Code's integrated terminal all hit this.
  - Fix: drop the flag from both `X509CertificateLoader` calls. The PFX file on disk at `ResolveCertPath()` is the actual persistence mechanism; the keychain copy was a redundant side-effect for a self-signed dev cert. HTTPS binding behaviour is unchanged on every platform; the in-process private key remains available (`Exportable` is preserved).
  - Linux + Windows unaffected — they hit different code paths that don't require a window-server prompt in the first place.

- **MOX edge click on RX audio.** *(KB2UKA, PR #326)*
  - Some audio endpoints (USB DACs, pro audio interfaces) produced an audible click on the MOX rising / falling edges, occasionally accompanied by a small panadapter blip. Bench investigation traced it to the RX broadcast → browser playback boundary: WDSP's `SetChannelState` damps the outgoing side on MOX-on (`dmp=1`) but resumes with `dmp=0`, and the audio-client's buffer-drain endpoint sits on whatever the last broadcast sample happened to be.
  - Fix: `DspPipelineService` now applies a one-shot 5 ms linear ramp to the first RX audio block after each MOX edge. Rising edge ramps the last block out + zero-fills so the browser's final played sample is 0.0; falling edge ramps the resume block in. Steady-state RX audio is byte-for-byte identical to before.
  - Engine-agnostic — affects HL2, ANAN-class, and Saturn-family equally.

### Added

- **HL2 Band Volts PWM toggle** (RADIO settings tab). *(Brian Keating / EI6LF, PR #314, closes #279)*
  - Lets HL2 operators enable the firmware's Band Volts feature so an external amplifier (e.g. Xiegu XPA125B) follows Zeus's band changes automatically. Wire bit is C3 bit 3 of the Config frame (address `0x00` bit 11, "Fan or Band Volts PWM" per `docs/references/protocol-1/hermes-lite2-protocol.md` line 39).
  - Renames the legacy `EnableHl2Dither` flag to `EnableHl2BandVolts` so the in-code name matches the wire-doc terminology. mi0bot's HL2 fork uses the same one-bit repurpose. Wire encoding in `ControlFrame.WriteConfigPayload` unchanged.
  - Persists per-radio in `PreferredRadioStore` (LiteDB). Older rows hydrate as `false` — matches HL2 firmware default where the PWM line drives Fan Control unless explicitly switched.
  - New `HasHl2OptionalToggles` capability flag, true only for `HpsdrBoardKind.HermesLite2`. Frontend gates the new RADIO tab on this — invisible on non-HL2 boards. Square SDR discovers as HL2-compatible and gets the tab on the same path.
  - New endpoints `GET /api/radio/hl2-options` and `PUT /api/radio/hl2-options` returning `{ "bandVolts": bool }`. Object-shaped so future mi0bot HL2-specific toggles (e.g. "Disable PS Sync") slot in without breaking the contract.

- **Meter smoothing + peak hold across every meter.** *(Brian Keating / EI6LF, PR #328)*
  - Raw meter frames land at ~10 Hz; the render loop ticks at ~30 Hz, so needles and bars visibly stepped between frames. New shared `useEmaSmoothed(value, tauMs)` hook applies `alpha = 1 - exp(-dt/tau)` (90 ms time constant) to every BigArc / VuColumn / PullDownArc / HBarMeter via `MeterRenderer`, and to the MIC / ALC / PWR / SWR meters inside `TxStageMeters`. Sentinels (≤ -200 dBFS) pass through verbatim so "no signal" still reads correctly.
  - Peak-hold ballistics across both renderer paths bumped to **1500 ms** before decay so SSB / FT8 transients are visible long enough to read. Absolute-peak refs still consume the raw store value, so true peaks are never shaved off by the smoother.

- **NR1 / NR2 / NR4 accordion disclosure state persists across browser reloads.** *(Brian Keating / EI6LF, PR #328)*
  - The inline NR settings section was using a non-persisted Zustand store, so its chevron collapsed every page reload even if the operator preferred it open. New `nr_ui_prefs` LiteDB collection in `zeus-prefs.db` holds three booleans (one per NR engine), surfaced via `GET` / `PUT /api/nr-ui-prefs` with a 150 ms debounced write on toggle. Module-level hydration runs once on first mount; failure is best-effort (does not block UI).

- **QRZ Lookup: Clear button.** *(KB2UKA, PR #320, closes #318, requested by EI8KV)*
  - One-click reset for the callsign input + lookup result. Useful for cycling between contacts during a contest or net. Renders as a `btn sm` in the card footer (left of "Log QSO") and below the "Not found" error block. Enabled whenever there's something to clear — a current contact, an error, or a non-empty input.

### Changed

- **v3 Lifted Dark theme.** *(Brian Keating / EI6LF, PR #327)*
  - Palette in `tokens.css` remapped to a neutral near-black (`--bg-0..3`, `--bg-inset`, `--bg-meter`); type stack swapped to **Inter** for UI and **JetBrains Mono** for fixed-width. Sidebar, topbar, transport, and panel chrome flatten — no more beveled gradients or inset highlights.
  - **VFO `freq-display` blue aurora**: three layered radial-gradients + a blurred ellipse behind 200-weight Inter digits, so the tuned frequency reads at a glance with the chrome stepping out of the way.
  - **TX stage-meter wells**: warm amber halo via `box-shadow`; analog gauges bake in the "streetlamp pool" `hsla(31, 30%, 65%, 0.19)` gradient.
  - **Brass instrument-plate panel headers**: subtle vertical gradient, 2 px gold (`--power`) leading rail with a soft bloom, specular top highlight, warm-amber-soft bottom hairline, engraved-style uppercase title with a faint amber text-shadow. Applied to every panel head and workspace tile.
  - `--accent` deepened from `#2e8eff` to `#0c5f9c` so the active-button glow and VFO aurora read closer to the Hermes Lite 2 hardware blue.
  - **NR2 advanced settings card** lifts to `--bg-2` so it visibly sits above the DSP panel base.
  - **Sidebar gear button**: dropped the redundant "Settings" caption — the cog glyph is self-evident.

- **QRZ Lookup panel: portrait rework.** *(Brian Keating / EI6LF, PR #328)*
  - 2× operator portrait moves to the right side, anchored at the top of the card and stretching the full height of the info column. Drops the rig / antenna / power / qsl rows that were rarely consulted in-shack. Remaining four rows (Grid / Lat-Lon / CQ · ITU / Local) stack single-column with values aligned next to their labels.

- **S-Meter config: collapsed to a single "Zeus mode" toggle.** *(Brian Keating / EI6LF, PR #328)*
  - The header gear previously exposed 8+ controls (scales shown, dBm readout, SWR alarm, attack / decay / averaging / peak hold) the typical operator never touched. Strips the UI to just Zeus mode — image fade past S9, lightning crackle at S9+20. Underlying store + defaults untouched, so persisted state from older sessions still hydrates cleanly.

### Known issues
- **ANAN-10E / Hermes-class on Protocol-1**: TX fix (#324) is staged but not yet merged — still under bench verification by @RonnieC82 on a real 10E. Operators on non-HL2 P1 boards (Hermes / 10E / 100D / Orion) should continue using Thetis for now or follow #294 for the rollout signal.

---

## [0.7.2] — 2026-05-13

A correctness-focused release with two big on-air wins: **audio dropouts when
streaming with PureSignal armed are gone**, and **two whole board families
(ANAN-G2E, ANAN-10E) now have working panadapters on Protocol-2**. Brick2 SDR
also gets Protocol-2 support for the first time.

### Fixed

- **Audio dropouts during OBS-streaming + PS-armed sessions eliminated.** *(KB2UKA, PR #304, closes #299)*
  - Diagnosed via a two-stage probe stack — backend WS-queue drop counters confirmed the server side wasn't dropping anything (zero drops across 1,649 TX events and 470 PS-feedback windows), and a frontend `latePush` / `latenessVsSchedule` probe identified the AudioContext render thread getting preempted under sustained OS audio load as the actual cause.
  - Fix: raised `BUFFER_TARGET_SECS` from 100 ms → 300 ms and opened the `AudioContext` with `latencyHint: 'playback'` so the browser allocates larger internal render-thread buffers. Adds ~200 ms of imperceptible RX latency in exchange for eliminating the audible clicks.
  - Diagnostic probes left in place as living instrumentation — zero overhead when there are no drops, immediately diagnostic if anything regresses.

- **ANAN-G2E panadapter now works on Protocol-2.** *(Brian Keating / EI6LF, PR #308, closes #289)*
  - Root cause: Zeus hard-coded the user-RX DDC slot as DDC2 for every Protocol-2 board, but the Hermes-family firmware (which includes HermesC10 / G2E) routes user RX through DDC0. The radio was being told to enable a DDC slot it didn't use, so it never sent any RX IQ.
  - Fix: per-board `RxBaseDdc` capability — Hermes / HermesII / HermesC10 → DDC0; Saturn-class (G2 / G2-1K / 7000DLE / 8000DLE / OrionMkII) keep DDC2 (unchanged).
  - Discovered board kind is now plumbed through `/api/connect/p2` → `ConnectP2Async` → `Protocol2Client` so per-board routing applies on the first frame.
  - PS feedback block is now no-op'd for single-ADC Hermes-class boards (G2E has no PS hardware).

- **ANAN-10E panadapter now works on Protocol-2.** *(Brian Keating / EI6LF, PR #308)*
  - Same root cause and fix as G2E above — ANAN-10E maps to HermesII (wire byte `0x02`), also a single-ADC Hermes-class board.

- **Brick2 SDR works on Protocol-2.** *(Brian Keating / EI6LF, PR #308, closes #171)*
  - Same DDC0 routing fix as above, plus a Brick2-specific 48 kHz IQ gain correction (`+29 dB` lift) for the deskhpsdr firmware quirk per `new_protocol.c:2516`, and macOS UDP route priming for the receive bind.

- **Protocol-2 TUNE PTT-bit wire fix.** *(KB2UKA, PR #303)*
  - `SendCmdHighPriority` now sets the PTT bit during TUNE on Protocol-2, matching MOX behaviour. Previously the TUNE button armed the radio's tune state but the wire didn't fully reflect MOX-on, causing edge-case behaviour with some amps and external T/R sequencers.

### Added

- **`CONTRIBUTING.md` at the repo root.** *(KB2UKA, PR #305, #306)*
  - First contribution-rules document the project has had. Codifies the red-light/green-light system, branch model, hot paths to leave alone (audio scheduling + PureSignal), commit conventions including the no-AI-tool-mentions hard rule, on-air testing expectations, and reviewer assignments. Linked from the README's new Contributing section.

### Changed

- **Repo URL canonical updated** from `brianbruff/openhpsdr-zeus` to `Kb2uka/openhpsdr-zeus` across all 11 hardcoded references — README, AboutPanel update-check, CHANGELOG, ATTRIBUTIONS, install docs, release workflow, issue template, CLAUDE.md. *(KB2UKA, PR #309)*. GitHub's auto-redirect handles old links, but the canonical home is now correct everywhere.

### Known issues
- None new at release. CW-only feature requests (Zero Beat, APF — #300) are in flight for a future release.

---

## [0.7.1] — 2026-05-12

Focused release around **PureSignal correctness** and **server-side performance**.
If you've been seeing slow PS convergence, sporadic on-air splatter bursts, or
audio hiccups during PS correcting, this release fixes all three. Brian's
five-iteration performance pass also lands here, taking ~25% off steady-state
CPU.

### Fixed — PureSignal: three separate regressions

PS was audited end-to-end against the Thetis reference implementation. Three
distinct root causes were identified and fixed:

- **Initial-arm convergence dropped from 5–10 s to 2–3 s** — matches Thetis on
  the same hardware. Zeus's per-step `±1 dB` clamp + `engine.ResetPs()` storm
  was replaced with the Thetis-canonical 3-state dance (save calibration mode →
  write attenuator in a single jump → restore calibration mode). One calcc
  reset per attenuator change instead of N.
  *(KB2UKA — Doug Cerrato — PR #293)*

- **Sporadic on-air splatter bursts eliminated.** Previously, any unrelated
  state change during a live MOX — the RX-side auto-attenuator firing at 10 Hz
  on ADC overload, S-meter retracking, panadapter zoom, operator UI nudges —
  would silently re-fire `SetPSControl(reset=1)` and truncate the PS polynomial
  mid-fit, blooming IMD3 sidebands for 50–500 ms until calcc rebuilt the
  polynomial. PS knob applies (`SetPsHwPeak`, `SetPsAdvanced`, `SetPsControl`)
  are now deferred while keyed; they catch up on PTT release.
  *(KB2UKA — Doug Cerrato — PR #293)*

- **`hw_peak` auto-cal disabled.** A silent server-side retarget of WDSP's
  `hw_peak` to `observed_envelope × 1.02` was pinning `env/hw_peak ≈ 0.98` and
  starving calcc LCOLLECT bins 0..13 — the cause of "PS never quite settles."
  mi0bot / Thetis don't auto-cal `hw_peak`; it's operator-tuned only. Restoring
  that behaviour fixes the bin starvation. The Settings panel still surfaces
  `Observed peak` if you want to dial it manually.
  *(Brian Keating — EI6LF — PR #292)*

**Who this affects:** every Protocol-2 board (ANAN-G2, G2-1K, 7000DLE, 8000DLE,
OrionMkII, ANVELINA-PRO3, RedPitaya, HermesC10/G2E). HL2's existing PS path is
untouched.

### Performance — Brian's iter1–5 server-side pass (PR #295)

Five iterations of measurement-driven server-side performance work landed:

- **Live CPU under steady RX: ~32.8% → ~24.3%** (iter5 head-to-head measurement
  on Brian's bench).
- **Workstation GC** for the desktop-radio workload — eliminates long Gen2
  pauses that were the dominant cause of audio hiccups during PS correcting.
- **Async-iterators removed** from the PS-feedback pump, IQ pump, hub send
  loop, and DSP pump. Direct channel reads + `WaitToReadAsync + TryRead` batch
  drain cut per-frame allocation churn by ~25%.
- **Lock-free SPSC ring** for the DSP-thread → hub-frame handoff — no managed
  lock on the audio hot path.
- **Single-thread DSP ownership** — `_engineLock` removed from the hot path;
  DSP runs on one dedicated thread instead of fanning across the thread pool.
- **`IRxPacketSink` seam** decouples the protocol receive loops from the DSP
  pump architecture — the pump-collapse refactor that made the per-tick work
  measurable in the first place.
- Full iter1–5 writeups, before/after counters, and sample profiles under
  `docs/perf/server/`.

*(Brian Keating — EI6LF)*

### Added — Settings persistence (PR #291, closes #287)

The operator-facing state that used to evaporate every time you restarted the
backend now persists:

- **VFO frequency**
- **Active mode** (USB / LSB / AM / FM / CW / DIGU / DIGL)
- **RX/TX filter widths**, with **per-mode memory** — each mode remembers its
  own filter, so an `AM → SSB` mode switch recalls the last SSB width
- **AGC top-dB**, **attenuator**, **auto-AGC/auto-att toggles**
- **Master RX volume** and **display zoom**
- **Per-board sample rate** — stored keyed by board type + variant, so
  switching between an HL2 and an ANAN G2 doesn't drag one radio's preferred
  rate onto the other
- **Panadapter background image** — survives backend restarts, works across
  browser/origin switches (already moved to LiteDB; this release adds it to the
  per-restart audit)

A new **`/run fresh`** dev convenience starts the backend against a
throw-away `/tmp/zeus-fresh-*.db` so dev testing doesn't pollute your
production settings. Backed by a new **`ZEUS_PREFS_PATH`** env var that
overrides the prefs database path.

Implementation: `RadioStateStore` + `PrefsDbPath` helper + per-board
sample-rate sub-collection + 1 Hz debounce flush; final flush on Dispose so a
clean shutdown captures your last action.

8 new unit tests pin the contract.

### Changed

- 13 LiteDB-backed preference stores (PA, DSP, filter presets, band memory,
  layouts, etc.) now route through a shared `PrefsDbPath` helper. The 13
  orphaned `private GetDatabasePath()` methods left behind by the refactor
  were removed.

### Known issues

- **ANAN-G2E / ANAN-10E Protocol-2 panadapter dead (issue #289).** Two users
  report no RX traffic on these boards. Root cause was identified during the
  v0.7.1 cycle: Zeus hard-codes the user-RX DDC slot as DDC2 for every
  Protocol-2 board, but the Hermes-family firmware (HermesC10 / HermesII /
  Hermes) uses DDC0. The fix is a per-board capability table + plumbing it
  through `Protocol2Client`; in-progress, deliberately not in 0.7.1 because we
  can't bench-test wire-format changes on these boards in-house. Expected for
  0.7.2.

---

## [0.7.0] — 2026-05-10

Operator-visible highlights from the [v0.7.0 release page](https://github.com/Kb2uka/openhpsdr-zeus/releases/tag/v0.7.0):

### Added
- **RF2K-S amplifier panel** — drive your amp directly from Zeus with
  immersive arc gauges for forward power, SWR, drain current, and temperature.
  VNC password protection supported.
- **Immersive meters** — full makeover with arcs, VU columns, and a pull-down
  gain-reduction meter, styled like real lab gear. Build your own meter groups
  with named layouts; single-click rename, drag to rearrange.
- **Final Output meter** shows forward power and SWR side-by-side.
- **Per-radio power scale** — meter axis automatically matches your board
  (5 W HL2 doesn't share a 200 W ANAN dial).
- **Coloured zone bands** (green / amber / red) on every meter widget.
- **Continuous Frequency Compressor (CFC)** — 10-band compressor with TX Audio
  Tools menu (issue #123).
- **PureSignal Monitor toggle** — clean TX panadapter via WDSP siphon
  (issue #121).
- **VFO wheel scroll tuning** — per-digit wheel tuning (issue #42 / #127).
- **Master AF Gain slider** driving `WDSP SetRXAPanelGain1` (issue #77).
- **Custom RX filter bandwidth presets** + user-defined low/high (issue #39).
- **Settings modal** — draggable, with MENU button + tab scaffolding.
- **New wallpaper options** — Zeus beach backdrop, flat-design hero image.
- **Operator-selectable RX trace color** in the panadapter.

### Changed
- Release builds now rebuild the WDSP DSP library from source as part of the
  pipeline, instead of relying on pre-committed binaries.
- Native-library workflow pinned to `ubuntu-22.04` (glibc 2.35) so Linux
  releases work on Debian 12 / older distros.

### Fixed
- Various P2 / TX / meter polish (see PRs #281, #282, #284, #286).

---

## Earlier releases

For releases prior to 0.7.0, see the [GitHub Releases page](https://github.com/Kb2uka/openhpsdr-zeus/releases).
