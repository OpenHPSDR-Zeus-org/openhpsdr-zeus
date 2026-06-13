# Changelog

All notable, operator-visible changes to OpenHPSDR Zeus are documented here.

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

For build artifacts (Windows installers, macOS DMG, Linux tarballs, AppImages),
see the corresponding GitHub Release page.

---

## [0.9.0] — 2026-06-13

> **🎙️ The Voyeur Mode release — Zeus can now listen to a net and write the whole thing down.** Point Zeus at a net frequency, walk away, and come back to a complete, searchable log: every transmission recorded, transcribed to text, callsigns picked out and looked up on QRZ, a roster of who checked in, and a plain-language summary of what was discussed — all generated **on your own computer, with no cloud service and no account**. This release also makes panadapter tuning glide instead of jump, adds a live CW decoder and a redesigned Telegraph Console, brings PureSignal and transmit audio to desktop-grade quality, runs on the **Raspberry Pi 4/5** for the first time, and ships a reel-to-reel tape-deck recorder. Roughly 155 changes since v0.8.4.

### What's new (in plain English)

**Voyeur Mode — your automatic net secretary.** This is the big one. Open the Voyeur panel, tell Zeus the net you want to follow, and leave it running. Zeus listens for each over (transmission), records it, and — if you've enabled it — turns the speech into text right on your machine. It scans that text for callsigns, validates them against QRZ, and builds a live roster of everyone who checked in, with names. When the net wraps up, a small AI model running locally can write a short "what was discussed" summary so you don't have to re-listen to an hour of audio. Everything is saved in a searchable archive, and you can replay any recorded over with one click. The speech engine and the AI model are **not** bundled into the main download — the first time you turn the feature on, Zeus offers a one-time, no-terminal download of just the pieces you need, and that download now works on **every platform** (Windows, macOS Intel + Apple Silicon, Linux x64 + arm64). Privacy note: transcription and summarization happen entirely offline; the only thing that ever leaves your computer is the QRZ callsign lookups you already use elsewhere in Zeus.

**Tuning that glides.** Spinning the dial used to make the spectrum jump in steps. Now the panadapter animates smoothly to the new center, the receiver "catches up" faster after a big jump so the display isn't smeared, and the filter passband and dial marker stay locked to the center line the whole time. It just feels right now. *(#597)*

**CW decoder + redesigned Telegraph Console.** Zeus now decodes received Morse to on-screen text in real time, streamed live from the radio's audio. The CW console was rebuilt and its settings persist across restarts, you get a proper host-side sidetone monitor, and the Hermes-Lite 2's on-board iambic keyer can be configured from Zeus.

**Cleaner, more reliable transmit (PureSignal).** A large body of work brought PureSignal and the transmit path on the desktop build up to the quality of the web build and Thetis: two-tone and voice are noticeably cleaner, PureSignal holds its correction instead of "jumping," your per-radio feedback-attenuation calibration is now saved and restored, and there's a two-tone IMD measurement overlay so you can see your transmit purity. Arming/disarming PureSignal mid-transmit no longer wedges it. *(#556, #557, #558, #559)*

**Runs on Raspberry Pi.** Zeus now has official arm64 Linux builds — an AppImage and a tarball — plus a headless deployment guide, so you can run the backend on a Raspberry Pi 4 or 5.

**Tape-deck recorder.** A new reel-to-reel style panel records your receive or transmit audio to a WAV file and plays it back, either locally or back out over the air.

**Polish all over.** Lots of settings now survive a restart that didn't before — panadapter dB scale, tuning step, band favorites, mic gain and Leveler. Every meter now shares the same smooth needle behavior. macOS signed builds can use the microphone and no longer depend on a Homebrew library being installed. Windows machines with multiple network adapters now discover the radio and draw the panadapter correctly. And the plugin system gained receive/transmit audio taps so audio plugins can see and shape the signal.

### Added

- **Voyeur Mode (zeus-la5)** — unattended net-monitor capture and log management; whisper speech-to-text with callsign extraction and QRZ enrichment; roster report, searchable archive, and per-over audio replay; a local-LLM "what was discussed" digest surfaced in both Log and Roster views; in-app, terminal-free, cross-platform download of the speech + AI engines and models; custom/non-NATO phonetic decoding.
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

> **🎉 Headline:** in-process **Audio Suite** with live pre-MOX meters and audition, **dual desktop icons** on every platform (Zeus + Zeus Server with a Photino status window), single-binary Zeus that hosts both modes, and Ramon's smoothed-SWR meter improvements.

### Added — Audio Suite (issue #332)

- **Live pre-MOX meter tap + audition** (#390). Every audio-chain plugin's IN / OUT / GR meters animate continuously with MOX off so you can dial dynamics, gates, and gain staging without going on the air. Toggle **Audition** in the suite-window header to hear the processed chain through your RX playback sink — share-with-receive convention means muting RX mutes audition.
- **Audio Suite floating window + reorderable chain** (#391). Open it from the new button on TX Audio Tools. Drag tiles at the top to reorder the chain — signal flow really changes when you reorder, not just metadata. `ChainOrderService` with canonical-vs-runtime separation so uninstalled plugins keep their slot for reinstall.
- **Render tx-audio-tools.chain plugins above CFC** (#373). Audio Suite plugins surface in their own region above the WDSP CFC strip on the TX Audio Tools panel.
- **One-click Download Audio Suite installer button** (#376). Drops EQ + Compressor + Exciter + Bass + Reverb in a single click via the official Kb2uka plugin registry.
- **Plugin-settings persistence fix** (#387). LiteDB upsert-by-`_id` bug was silently inserting new rows on every `SetAsync` instead of updating — operator dial-in could revert to defaults across desktop restarts. Now: atomic delete-by-key + insert, with descending-ID read so any pre-fix duplicates resolve to the latest value.
- **Restart-required modal on every plugin install.** The "Please shut down Zeus and restart" dialog that previously fired only after the Download Audio Suite bundle install now also fires after a single-plugin install from the Plugins panel (Settings → Plugins → Browse / Install). New endpoints + AssemblyLoadContexts only register at backend startup, so the operator always gets the same explicit reminder.

### Added — plugins shipping with this release (Kb2uka/openhpsdr-zeus-plugins)

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

- New **[Audio Suite](https://github.com/Kb2uka/openhpsdr-zeus/wiki/Audio-Suite)** page — full workflow, audition rules, per-plugin reference (Gate / EQ / Comp / Exciter / Bass / Reverb), gain-staging guide. Linked in the Transmit sidebar.

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
