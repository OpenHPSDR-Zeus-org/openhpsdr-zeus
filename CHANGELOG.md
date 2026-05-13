# Changelog

All notable, operator-visible changes to OpenHPSDR Zeus are documented here.

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

For build artifacts (Windows installers, macOS DMG, Linux tarballs, AppImages),
see the corresponding GitHub Release page.

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

Operator-visible highlights from the [v0.7.0 release page](https://github.com/brianbruff/openhpsdr-zeus/releases/tag/v0.7.0):

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

For releases prior to 0.7.0, see the [GitHub Releases page](https://github.com/brianbruff/openhpsdr-zeus/releases).
