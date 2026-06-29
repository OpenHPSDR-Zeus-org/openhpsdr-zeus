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

