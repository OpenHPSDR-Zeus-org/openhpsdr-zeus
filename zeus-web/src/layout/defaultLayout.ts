// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Default workspace layout for the react-grid-layout (RGL) substrate. 12-col
// grid. The right column starts as a stack of 6-wide tiles (vfo/smeter/tx/
// txmeters/dsp). These are no longer width-capped — every panel is freely
// resizable to grid extents now (the panadapter-style "any size" model) — so
// the 6-wide seeds here are just a sane starting arrangement, not a ceiling;
// the operator can widen any of them. The left column is BANDWIDTH FILTER on
// top and the panadapter hero filling the remaining vertical space.
// FlexWorkspace shrinks rows when needed for short viewports, but does not
// stretch panel heights just because the window is taller.
//
// Coordinates are in the 24-column × 48-row (schema-v8) grid. Total height =
// WORKSPACE_TARGET_ROWS (48). The top-left row pairs the Bandwidth Filter
// (mini-pan only) with the split-out Filter Presets panel; the
// panadapter hero fills the rest of the left column, and the right column
// stacks vfo / smeter / tx / txmeters / dsp.
//
// ASCII sanity check (columns 0..23):
//
//   ┌──────────────────────────────┬───────────────┬─────────────┐  y=0
//   │   filter · mini-pan (0..11)   │ presets(12..17)│    vfo      │
//   │            h=10               │     h=10       │   (h=11)    │
//   ├──────────────────────────────┴───────────────┤             │  y=10
//   │                                               ├─────────────┤  y=11
//   │                                               │   smeter    │
//   │                                               │   (h=5)     │  y=16
//   │                                               ├─────────────┤
//   │            hero (0..17, h=38)                 │     tx      │
//   │                                               │   (h=10)    │
//   │                                               ├─────────────┤  y=26
//   │                                               │  txmeters   │
//   │                                               │   (h=12)    │
//   │                                               ├─────────────┤  y=38
//   │                                               │     dsp     │
//   └───────────────────────────────────────────────┴─────────────┘  y=48

import type { WorkspaceLayout } from './workspace';

export const DEFAULT_WORKSPACE_LAYOUT: WorkspaceLayout = {
  schemaVersion: 8,
  tiles: [
    // Stable uids (not random) for the default layout — lets a future
    // migration map "the old default 'vfo' tile" to a new layout without
    // losing operator overrides.
    { uid: 'tile-filter',        panelId: 'filter',        x: 0,  y: 0,  w: 12, h: 10 },
    { uid: 'tile-filterpresets', panelId: 'filterpresets', x: 12, y: 0,  w: 6,  h: 10 },
    { uid: 'tile-hero',          panelId: 'hero',          x: 0,  y: 10, w: 18, h: 38 },
    { uid: 'tile-vfo',           panelId: 'vfo',           x: 18, y: 0,  w: 6,  h: 11 },
    { uid: 'tile-smeter',        panelId: 'smeter',        x: 18, y: 11, w: 6,  h: 5 },
    { uid: 'tile-tx',            panelId: 'tx',            x: 18, y: 16, w: 6,  h: 10 },
    { uid: 'tile-txmeters',      panelId: 'txmeters',      x: 18, y: 26, w: 6,  h: 12 },
    { uid: 'tile-dsp',           panelId: 'dsp',           x: 18, y: 38, w: 6,  h: 10 },
  ],
};
