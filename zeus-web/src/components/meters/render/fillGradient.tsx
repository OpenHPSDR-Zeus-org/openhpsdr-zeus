// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// fillGradient — the volumetric shading that makes a meter fill read like a
// lit cylinder of liquid metal. Layer (2) of the stack.
//
//  • volumeStops / volumeGradientCss — the cylindrical crown→mid→floor shading
//    that layers UNDER the live signal hue (depth without recolouring).
//  • mercuryGradientCss / MercuryStops — NEW quicksilver overlay: a bright
//    rolled top edge + a thin mobile specular line + a darker belly, so a bar
//    or needle reads as polished liquid metal rather than a flat LED. Composite
//    this OVER the hue fill at a screen-ish blend.
//
// DISPLAY-ONLY, token-only.

import type { ReactNode } from 'react';

export interface VolumeStopsOptions {
  base?: string;
  hot?: string;
}

/** Vertical-volume gradient stops (top y=0 → bottom y=1). Crown near the top,
 *  clear mid, dark floor — the lit-cylinder shading. */
export function volumeStops(opts: VolumeStopsOptions = {}): ReactNode {
  const base = opts.base ?? 'var(--meter-fill-base)';
  const hot = opts.hot ?? 'var(--meter-fill-hot)';
  return [
    <stop key="crown" offset="0" stopColor={hot} />,
    <stop key="hi" offset="0.16" stopColor="rgba(255,255,255,0.12)" />,
    <stop key="mid" offset="0.5" stopColor="rgba(0,0,0,0)" />,
    <stop key="lo" offset="0.82" stopColor="rgba(0,0,0,0.20)" />,
    <stop key="floor" offset="1" stopColor={base} />,
  ];
}

/** CSS-string equivalent of volumeStops for DOM bars (vertical, top→bottom). */
export function volumeGradientCss(opts: VolumeStopsOptions = {}): string {
  const base = opts.base ?? 'var(--meter-fill-base)';
  const hot = opts.hot ?? 'var(--meter-fill-hot)';
  return (
    `linear-gradient(180deg,` +
    ` ${hot} 0%,` +
    ` rgba(255,255,255,0.12) 16%,` +
    ` rgba(0,0,0,0) 50%,` +
    ` rgba(0,0,0,0.20) 82%,` +
    ` ${base} 100%)`
  );
}

/** Quicksilver overlay (vertical) — a rolled bright top, a thin specular line
 *  near the top third, a clear mid, and a darker belly. Composite over the hue
 *  fill (screen / soft-light) so the bar looks like polished mercury. */
export function mercuryGradientCss(): string {
  return (
    `linear-gradient(180deg,` +
    ` var(--meter-mercury-hi) 0%,` +
    ` rgba(255,255,255,0.30) 9%,` +   // rolled top edge
    ` rgba(255,255,255,0.04) 22%,` +
    ` var(--meter-mercury-lo) 30%,` + // thin specular line
    ` rgba(0,0,0,0) 52%,` +
    ` rgba(0,0,0,0.16) 84%,` +
    ` rgba(0,0,0,0.30) 100%)`
  );
}

/** Horizontal quicksilver overlay for vertical-fill bars (VU columns): the
 *  specular rides the left edge as the column fills. */
export function mercuryGradientCssH(): string {
  return (
    `linear-gradient(90deg,` +
    ` var(--meter-mercury-hi) 0%,` +
    ` rgba(255,255,255,0.22) 14%,` +
    ` rgba(0,0,0,0) 46%,` +
    ` rgba(0,0,0,0.14) 86%,` +
    ` rgba(0,0,0,0.26) 100%)`
  );
}
