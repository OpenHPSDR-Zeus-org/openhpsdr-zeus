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

## Build sequencing

UI is a later phase. Backend decode engine, `Zeus.Dsp.Ft8`, `Ft8Service`, and
Contracts land first; the workspace is built once live decodes exist to render.
Structure the React components theme-agnostic so the palette decision can be
applied at the end without rework.
