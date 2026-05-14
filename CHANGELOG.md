# Changelog

All notable, operator-visible changes to OpenHPSDR Zeus are documented here.

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

For build artifacts (Windows installers, macOS DMG, Linux tarballs, AppImages),
see the corresponding GitHub Release page.

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
