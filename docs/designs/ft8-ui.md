# FT8 mode — UI design direction

Status: **direction captured, palette decision OPEN.** Source of truth for the
look/feel of Zeus's built-in FT8 (and later WSPR) workspace. Provided by
KB2UKA 2026-06-25 from his "Redesigning FT8 Interface" session.

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

## DECISION — palette / theme: **Option A, APPROVED (KB2UKA 2026-06-25)**

The FT8 workspace gets its **own dedicated dark-HUD theme** faithful to the
mockup — cyan/teal-on-near-black-navy, color-coded decodes, monospaced data.
Approved by KB2UKA (authority on Zeus visual design). KB2UKA further noted Zeus
**may adopt this palette globally** at some later point.

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
