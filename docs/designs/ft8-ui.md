# FT8 mode — UI design direction

> **STATUS NOTE (2026-07-01): backend extracted to a plugin.** The FT8/FT4/WSPR
> backend (decoders, TX keying/auto-sequencing, spotting, WSJT-X egress) now
> lives in the `openhpsdr-zeus-plugins` repo under `modes/Digital/` as the
> **Zeus Digital** plugin (`com.kb2uka.digital`). This document covers the
> **in-core UI shell** that remains in Zeus — the pop-out, mode gating, stores,
> and settings surfaces — which talks to the plugin over
> `/api/plugins/com.kb2uka.digital/*` (REST + SSE).

Status: **SHIPPED as a FreeDV-style pop-out (2026-06-27), superseding the
full-screen workspace below.** Source of truth for the look/feel of Zeus's
built-in FT8/FT4/WSPR UI. Original direction provided by KB2UKA 2026-06-25;
pivoted to the pop-out 2026-06-27 (KB2UKA call).

## DIRECTION CHANGE (2026-06-27, KB2UKA) — FreeDV-style pop-out, REUSE everything

The dedicated full-screen "command center" workspace (described in the sections
below, now historical) was replaced by a **floating, draggable, always-on-top
pop-out** modelled on the FreeDV modem popup — because it built **redundant**
copies of features the operator already has. New core principle:

> **REUSE Zeus's existing features — build NOTHING redundant.** No new VFO, no
> new waterfall, no new panadapter, no new QRZ panel, no new logbook inside the
> pop-out. The operator stays in their normal console (which already has the
> panadapter / waterfall / VFO / QRZ / logbook / meters); FT8 is just a pop-out
> of operating controls floating over it.

What the pop-out is (`DigitalWindow.tsx`, body `Ft8PopBody` / `WsprPopBody`):

- **Floating, draggable, always on top, NOT resizable.** Fixed size, ~2× the
  FreeDV popup (`digital-window-store.ts` `DIGITAL_WINDOW_WIDTH/HEIGHT`, height
  clamped to the viewport). "On top" = `zIndex: 420` (above the topbar 300,
  below modals 10000), same mechanism as `FreeDvWindow` — no OS API.
- **Contains ONLY operating essentials:** the colour-coded decode/station list
  (with the operator's own TX line + the report being sent), the macro/message
  buttons, TX ENABLE, slot (1ST/2ND), HOLD TX FREQ, OFFSET, TUNE/HALT, the
  shared TX-power sliders, and a compact **⚙ gear** that opens the existing
  `Ft8SettingsView` in place (decode depth / macros / logging). WSPR shows the
  spot table + beacon TX cluster only.
- **No** waterfall / VFO / QRZ / logbook / stats panels inside it — those are
  the operator's existing panels underneath.

State / source of truth:

- Open/close is owned **entirely by `ft8-store.open` / `wspr-store.open`** — the
  pop-out has no open flag of its own (`digital-window-store` is position-only).
  Engaging the mode opens it; un-toggling closes it.
- All the working FT8 logic is **re-housed, not rewritten**: the decode table
  (`Ft8DecodeTable`), the TX-control cluster (`Ft8TxControl`), and the live
  engine (`ft8-tx-runner` → `ft8-tx-controller` → `ft8-sequencer` → backend
  keyer), own-TX echo, click-to-call, auto-sequence, CALL-1st, HOLD-TX-FREQ,
  auto-log — all preserved verbatim.

Mode-button behavior (`enter-digital.ts`, `ModeBandwidth`, `ModeFavorites`):

- FT8/FT4/WSPR are a **toggle that stays DEPRESSED while engaged**. Engage =
  snapshot the current freq+mode, QSY the **main** panadapter/VFO to that mode's
  digital dial for the current band (reuses `configureRadioForDigital`), and
  open the pop-out. Un-toggle = `exitDigital()` → restore the prior freq+mode.
- While engaged, changing the **main band** re-QSYs to that band's digital dial
  (band-follow effect in the pop-out bodies via `qsyBand`/`qsyToDigitalBand`).
- FT8/FT4/WSPR remain mutually exclusive.

REUSE wiring to existing panels:

- **Click a station** → the operator's **existing QRZ panel** populates that
  callsign (`useWorkspace().runQrzLookup`), in addition to staging the call.
- **QSO partner** (we answer a station, or a station answers our CQ) →
  auto-populates the same QRZ panel (effect on `tx.qso.dxCall`).
- QSOs continue logging to the **existing logbook** (the runner's `onLogQso` →
  `useLoggerStore`), unchanged.

Station list beautification: vibrant colour-coded lines (CQ / directed-at-me /
new-grid / worked-before / your-TX), the user's own TX line including the report
being sent, and a small colour **key/legend** (`Ft8DecodeLegend`). Tokens only
(`--hud-*` / `--tx`, `color-mix`) — no raw hex. As of 2026-06-27 the `--hud-*`
tokens are aliases of the global Zeus palette (see the palette-theme decision
below — Option A's bespoke dark-HUD theme is reversed), so the colour-coded
decode lines (CQ → `--ok` green, directed-at-me → `--power` amber, new →
`--accent-bright`, worked → `--fg-2`, your-TX → `--tx`) read in the global hues.

Note (PR-level): the pop-out no longer mounts its own spectrum surfaces, so the
old App.tsx WebGL-context-release optimization (which unmounted the main
`FlexWorkspace` while the full-screen overlay was open) was dropped — there is
no context competition because the main console stays mounted underneath.

---

_The sections below are the ORIGINAL full-screen-workspace direction, retained
for history. They are SUPERSEDED by the pop-out above._

Status (historical): **direction captured, palette decision OPEN.** Source of
the look/feel; provided by KB2UKA 2026-06-25 from his "Redesigning FT8
Interface" session.

Reference images in [`ft8-ui/refs/`](ft8-ui/refs/):
- `ft8-target-mockup.png` — **the target.** Dark-HUD command-center layout.
- `jtalert-before.webp` — the JTAlert/WSJT-X "before" we are explicitly NOT
  copying (cramped, dated, gray).
- `mood-hud-waveform.webp`, `mood-waterfall.webp` — aesthetic mood (cyan-on-navy
  HUD glow, vibrant cascading waterfall).

## North star

A first-class FT8 **command center**, not a re-skin of WSJT-X. Operator clicks
"FT8" and gets a purpose-built workspace with zero audio-routing setup. The
showcase is the live decode table tied to the waterfall, with a propagation
band map and integrated logging.

## Activation behavior — mode spawns a dedicated workspace (KB2UKA)

Selecting **FT8 / FT4 / WSPR** auto-opens a **pre-populated workspace in its own
layout tab** — fully built and ready, panadapter and all panels already placed
and live. The operator arranges nothing. Leaving the digital mode returns to
their normal layout.

- Built on Zeus's existing **layout-tab system** (PR #984) to spawn/focus its
  own tab — but the tab hosts a **bespoke, FIXED layout, NOT the standard
  moveable panel grid.** No drag, no resize, no rearrange. It is a purpose-built
  React layout that "just works" exactly as the mockup shows. (KB2UKA: "no
  moveable panels.")
- **Own VFO.** The workspace has its **own dedicated VFO** built in (the big
  freq readout in the mockup), so digital-mode tuning lives inside the
  workspace and the operator doesn't reach back to the main app VFO panel.
- The preset is **ready-to-go**: own-VFO + panadapter + decode table + band map
  + activity log/logbook + TX control cluster, pre-wired to the live
  `Ft8Service` stream.
- Idempotent: re-selecting the mode focuses the existing tab rather than
  spawning duplicates; switching to a normal mode leaves the FT8 tab intact for
  return.
- FT8 vs FT4 share the workspace (same decode UI, different slot timing); WSPR
  gets the same shell with a WSPR-appropriate decode table + map and no QSO
  state machine (beacon/spot oriented).

## Mode engagement — what "selecting FT8" does (KB2UKA)

"FT8" appears in the mode selector alongside USB/LSB/CW/etc. Selecting it:
1. Under the hood, sets the radio to **USB demod** (FT8/FT4/WSPR are USB-audio
   modes) — the operator never sees "set USB," they just pick FT8.
2. **Auto-tunes the dial to the band's standard calling frequency** for the
   current band, and shows it in the workspace's own VFO. Standard FT8 dial
   frequencies (USB), auto-populated per band:

   | Band | FT8 (MHz) | Band | FT8 (MHz) |
   |---|---|---|---|
   | 160m | 1.840 | 17m | 18.100 |
   | 80m | 3.573 | 15m | 21.074 |
   | 60m | 5.357 | 12m | 24.915 |
   | 40m | 7.074 | 10m | 28.074 |
   | 30m | 10.136 | 6m | 50.313 |
   | 20m | 14.074 | 2m | 144.174 |

   (FT4 and WSPR have their own per-band tables; WSPR e.g. 20m 14.0956.)
3. Spawns/focuses the dedicated workspace tab (above) and starts the
   `Ft8Service` decode pipeline on the RX audio.
4. Leaving FT8 mode restores the prior mode + the operator's normal layout.

So: **click FT8 → you're here, tuned, decoding — zero setup.** Changing band
while in FT8 re-populates the dial to that band's FT8 frequency.

## Layout (from the target mockup)

Three columns under a top status strip; status bar across the bottom.

```
┌ FT8  ················· 14:25 UTC ···· 20m 14.074 USB ········· [indicators] ┐
│ RADIO            │ DECODED MESSAGES         [DECODE|TUNE|SETTINGS] │ BAND MAP │
│  14.074.000 USB  │  UTC   dB   DT  Freq  Message          Country  │ (great-  │
│  [meters/sliders]│  ...   -12  0.1 1234  CQ JA9BFN PM86    Japan   │  circle  │
│ BAND ACTIVITY    │  ...  (color-coded rows: CQ / worked / me)      │  world)  │
│  [mini spectrum] │ ───────────────────────────────────────────────│ band     │
│ MESSAGE          │ ACTIVITY LOG                                    │ stats    │
│ TX MESSAGES      │  time  call   grid  rprt  ...                   │ TX CTRL  │
│  [WATERFALL]     │                                                 │ ENABLE TX│
│ SNR  -24         │                                                 │ CQ / msgs│
│                  │                                                 │ DXCC cnt │
│                  │                                                 │ [audio]  │
└ CONNECTED · CAT · PTT ·············································· CPU ·······┘
```

## Components (UI phase work-list)

1. **Decoded-messages table (hero).** Columns UTC · dB(SNR) · DT · Freq(Hz) ·
   Message · Country/flag. Row color by class: CQ, directed-at-me, worked-B4,
   new DXCC/grid. Click a row → prefill QSO / QSY audio cursor. `DECODE / TUNE
   / SETTINGS` tabs.
2. **Waterfall + band-activity.** Reuse the existing panadapter WebGL; overlay
   decode markers at each signal's audio offset/time. Display-only (no axis
   flips — see lessons).
3. **Band map.** Great-circle world map of decoded stations from grid squares;
   day/night terminator a stretch goal. High-value differentiator.
4. **TX control cluster.** ENABLE-TX arm toggle (distinct from RX decode),
   CQ/standard-message slots, TX 1st/2nd (even/odd) selector, panic/abort.
5. **Radio strip.** Big freq readout, mode, RX meters, SNR.
6. **Activity log / logbook.** Feeds the existing `LogService` (ADIF in/out
   already present) — operator's choice of Zeus log or third-party via ADIF.
7. **Status bar.** CONNECTED · CAT · PTT · CPU.

Maps cleanly onto the planned seam reuse: waterfall = existing panadapter,
logbook = existing `LogService`/`AdifParser`, freq/CAT = existing VFO plumbing.

## DECISION — palette / theme: **REVERSED (KB2UKA 2026-06-27) — matches the global palette**

> **REVERSAL (2026-06-27, KB2UKA, visual authority):** Option A below is
> superseded. The pop-out no longer uses a bespoke dark-HUD cyan/teal theme; it
> now **matches the global Zeus palette** (`zeus-web/src/styles/tokens.css`).
> The `--hud-*` layer survives only as a thin ALIAS set that points at global
> tokens (`--hud-cyan → --accent-bright`, `--hud-bg → --bg-app`, `--hud-cq →
> --ok`, `--hud-me → --power`, etc.), so the re-skin was colors-only with zero
> component/markup change. Because the pop-out already tracks the global palette,
> the "promote the HUD theme app-wide" path below is moot. The original Option A
> text is retained for history.
>
> **Light-theme behaviour (2026-06-27):** the pop-out renders as an in-tree
> `<div class="digital-window">` (not a portal), so it inherits the global
> `:root[data-theme="light"]` chassis flip. Because the decode/status hues
> (`--ok` green, `--power` amber, `--accent-bright` blue) have **no
> dark-on-silver palette variants** and Zeus has no light-staying body-text
> token, the pop-out is treated as a **lit instrument** (same doctrine as the
> panadapter / VFO / S-meter / LED-meter wells, which `tokens.css` keeps dark in
> light theme): a `:root[data-theme="light"] .digital-window` override keeps its
> readout surfaces dark (`--bg-inset` / `--bg-meter`, the non-flipping
> display-well tokens) and re-lights its text via `--btn-active-text` (the one
> `#fff`-in-both-themes token). Dark theme — the default — is unchanged. This is
> a light-theme **visual** decision (red-light territory): flagged for Brian
> (EI6LF) / KB2UKA sign-off.

The (historical) FT8 workspace direction gave it its **own dedicated dark-HUD
theme** faithful to the mockup — cyan/teal-on-near-black-navy, color-coded
decodes, monospaced data. Originally approved by KB2UKA (authority on Zeus
visual design), who further noted Zeus **may adopt this palette globally** at
some later point — now superseded by the reversal above.

### Engineering consequence — build it as a token LAYER, not hex
Because this theme may later become Zeus-wide, do NOT hard-code the HUD colors
in components. Implement it as a **new token set** (CSS custom properties)
scoped to the FT8 workspace, e.g. a `[data-theme="ft8-hud"]` (or
`.ft8-workspace`) block that overrides/extends the variables in
`zeus-web/src/styles/tokens.css`:

- New HUD tokens (`--hud-bg`, `--hud-panel`, `--hud-glow-cyan`, `--hud-grid`,
  decode-class colors `--hud-cq` / `--hud-me` / `--hud-worked` / `--hud-new`,
  etc.), defined once.
- FT8 components consume **only** those variables — never raw hex (same
  discipline as the rest of Zeus).
- Promotion path: if KB2UKA later wants it global, it's re-pointing the root
  token values to the HUD set — **no component rewrites.**
- Existing red-light protections still apply: the amber panadapter trace
  (`#FFA028`) stays signal-visualization-only; don't repurpose it for chrome.

Note: if/when the HUD palette is promoted to the **global** Zeus theme (a much
larger, app-wide visual change), loop in Brian (EI6LF) as co-authority on Zeus
visuals before that global flip. The **FT8-scoped** theme is approved now.

The layout/IA above is unaffected; build components theme-agnostic and apply
the HUD token layer as the skin.

## Layout refinement — KB2UKA 2026-06-26 (build to THIS)

KB2UKA re-confirmed `ft8-target-mockup.png` as **the** target layout ("I like
this layout a little better than ours"). Build the workspace to match it, with
these hard adaptations:

### Hard rules
- **NO closable panels.** The mockup draws an `×` on every panel header —
  **ignore those.** Zeus has zero close/collapse/move/resize affordances in this
  workspace. Every panel is **fixed in its slot**, always visible. Do not render
  a close button anywhere. (Reinforces "no moveable panels" above.)
- **Controls map to OUR feature set, not WSJT-X's.** The mockup is a visual
  reference; its labels are illustrative. Concretely:
  - **MODE** tiles = `FT8 · FT4 · WSPR` (NOT JT65/JT9 — Zeus does not implement
    those). WSPR swaps the QSO-oriented panels for spot-oriented ones (see WSPR
    note above).
  - **DECODE** tiles = Zeus's actual decode depths (`NORMAL · DEEP · MULTI`
    maps to our single-pass / multi-pass / deep multi-pass `time_osr` ladder).
  - **Power / TX controls** mirror Zeus's existing rig controls (Drive %, Tune
    power, MOX/TUNE) — same backing endpoints/state as the main app, NOT new
    parallel power logic. See "TX control + power" below.
  - **Meters** (SWR / ALC / COMP) reuse Zeus's existing meter sources; only show
    meters the connected board actually provides (board-capability gated).
  - Chrome text in the mock (`CAT: IC-7300`, `WSJT-X v2.6.1`) is mockup-only —
    Zeus shows its own connected-radio / version info.

### Waterfall ("RECEIVE" panel) — KB2UKA 2026-06-26
- **Beautiful, and reuse our existing waterfall.** Borrowing the current Zeus
  panadapter/waterfall WebGL panel and injecting it into the workspace is the
  approved approach — do not write a second waterfall renderer from scratch.
- **Click-a-slice to tune.** Operator clicks a slice/column in the waterfall to
  set their RX/TX audio frequency (the in-passband offset, 0–2.5 kHz), with a
  visible RX cursor and TX marker as in the mock. Honor `HOLD TX FREQ`.
- Controls: **WF SPEED · WF ZOOM · WF OFFSET** as shown.

### TX control + power — KB2UKA 2026-06-26
- **TX CONTROL** cluster: `TX ENABLE` arm (explicit, never auto-arms),
  `HOLD TX FREQ`, `TX EVEN/ODD` slot selector, **TX POWER** slider, **TUNE**
  button.
- **Power controls live in the workspace and mimic what we already have** so the
  operator adjusts drive/tune power without leaving the workspace. Wire them to
  the **same** drive/tune endpoints and state the main rig uses — do not fork a
  second power model. (Safety: FT8 is 100 % duty; do not change power defaults,
  and do not auto-key.)

## Build sequencing

UI is a later phase. Backend decode engine, `Zeus.Dsp.Ft8`, `Ft8Service`, and
Contracts land first; the workspace is built once live decodes exist to render.
Structure the React components theme-agnostic so the palette decision can be
applied at the end without rework.

## SETTINGS view — BUILT (TX-ready, DO-NOT-MERGE bench draft)

The `DECODE | SETTINGS` view switch in the workspace header is now live
(`Ft8SettingsView.tsx`), modelled on the curated WSJT-X + JTDX KEEP set with
everything a native SDR already owns SCRAPPED (rig/CAT/PTT selection, sound-card
in/out, split, the frequencies table, power/attenuator dials — Zeus owns all of
those). Five sections: **Station/Operator · TX & Auto-sequence · Macros · Decode
· Reporting & Logging**. Reporting (PSK Reporter / WSPRnet / WSJT-X UDP) is
SURFACED as read-only status chips with a deep-link to the Network tab — not
duplicated.

### Identity-store decision (THE TX unblock)

FT8 TX was gated on the operator callsign (`canCall`), and identity lived only in
the desktop webview's localStorage — which is scoped to a loopback port the OS
reassigns each launch, so the call was silently lost on every restart and TX
never ungated.

Fix: operator identity is now **server-authoritative and shared**. A single
`OperatorIdentityStore` (LiteDB) backs `GET/POST /api/operator` and is the
override base every resolver reads first (FT8/FT4 TX, PSK Reporter, WSPRnet,
FreeDV Reporter), with the QRZ home station as the fallback. The frontend
`operator-store.ts` is now backed by that endpoint (no localStorage
system-of-record); the workspace TX/gating uses the **resolved** value (override
else QRZ home) so a QRZ-home operator transmits without retyping. The Call/Grid
edit fields moved OUT of the 44px clipping header into §Station; a compact
read-only call chip remains in the header. An empty-call banner makes the TX gate
reason visible (never a silent dead ENABLE button) and links to Settings.

FT8/FT4 behaviour prefs (auto-seq, decode depth Fast/Normal/Deep → `/api/ft8`
`passes`, editable macros, logging) persist via `Ft8SettingsStore` /
`/api/ft8/settings`. Defaults mirror current behaviour exactly — nothing an
operator feels changes until they touch a control, and TX still requires an
explicit arm. **DO-NOT-MERGE until G2 bench confirms ungate → arm → auto-seq →
log.**

## FINAL layout — KB2UKA 2026-06-27 (WOW-factor pass; build to THIS)

New target: `ft8-ui/refs/ft8-final-layout-2026-06-27.png`. This SUPERSEDES the
earlier mockup for layout. It is a "people-see-this-and-go-wow" pass. Doug is the
visual/UX authority and approves this. Tokens only (no raw hex). **Do NOT break any
FT8 function — they all work (decode, click-to-call, TX, auto-seq, logging,
waterfall click-to-tune, settings). Re-arrange, don't rip out.**

Top bar: Zeus / OpenHPSDR FT8 · MODE (USB/LSB/CWU/CWD/AM/DIG) · FILTER · BAND
(40/20/15/10/6/2m) · SPEED · AGC · NR · Disconnect · ⚙ settings.

LEFT column (hero): big VFO readout + FT8/TX badges + RX offset line; S-meter
(-120..0, S9+xx); **BIG waterfall** (the centerpiece — make it large), Inferno
palette, freq axis + dB axis, TX/RX cursor. Waterfall control strip: **WF dB
slider (MUST work exactly like the main panadapter dbMin/dbMax range — it
currently does NOT)**, Palette, RBW, Smoothing, Center, Zoom, Span.

CENTER column: the decode/station list — **SMALLER than now** (narrower, per the
photo): UTC/dB/DT/FREQ/MESSAGE, color-coded rows, click-to-select.

RIGHT-CENTER (TX CONTROL): TX ENABLE · HOLD TX FREQ · slot 1ST/2ND/3RD/4TH ·
MSG field · macros CQ/QRZ/GRID/REPLY/RR73/73/CALL 1ST · PWR & QRM (TX PWR / TX
EVEN / QRM sliders) · TUNE / HALT · STATS (QSO today/total/confirmed/pending/
decoded/avg-snr).

RIGHT column (STATION INFO = the QRZ panel we ALREADY have): callsign + flag,
name, QTH, grid, CQ/ITU zone, IOTA, loc, 10-10, DOK, WAZ, VUCC, **profile photo**,
VIEW ON QRZ.COM / ADD TO LOG, BIO. **Populates when the operator clicks a station
(and when a station answers the user / the user answers a station) — clicking the
station triggers a QRZ lookup for that call and fills this panel.**

BOTTOM status bar: FT8 ● DECODED:Xs · DATE · UTC · CALLSIGN · BAND · MODE · RST
TX · RST RX · COUNTRY · GRID · **LOG QSO · VIEW LOG · EXPORT ADIF** · version.

Functional requirements this pass MUST deliver:
1. Bigger waterfall; **WF dB slider wired to the panadapter dbMin/dbMax like the
   main display** (the broken part).
2. Smaller station list (per photo).
3. Keep ALL existing controls, placed per the photo.
4. **QRZ STATION INFO panel** wired to station-click (QRZ lookup by callsign;
   reuse the existing QRZ component/service).
5. **VIEW LOG** → opens a modal/popup: whole log, search box, edit entries,
   delete, with **persistence after save** (needs log update/delete endpoints).
6. **EXPORT ADIF** button → actually downloads ADIF (endpoint /api/log/export/adif
   already works — wire the button).
7. Incorporate the three staged UX fixes (reachable settings, live TX-message
   banner, own-TX echo rows).
