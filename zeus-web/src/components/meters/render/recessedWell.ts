// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// recessedWell — the concave "machined pocket" a meter sits inside. Layer (1)
// of the liquid-metal instrument stack. A concave radial floor (dark centre →
// lighter rim, lit upper-left) plus a multi-layer inset box-shadow so the
// pocket reads as pressed into the metal chassis. DISPLAY-ONLY, token-only.
//
// Elevated over the prior kit: a deeper floor, a crisper top lip, and a faint
// cool far-wall bounce so the pocket reads wetter under the glass.

import type { CSSProperties } from 'react';

export interface RecessedWellOptions {
  /** Corner radius. Defaults to 3. */
  radius?: number;
  /** Warm amber halo (signal bars want it; chrome wells don't). */
  warmHalo?: boolean;
  /** Outer instrument bloom (--gauge-glow). */
  glow?: boolean;
}

/** Concave floor — dark centre rising to a lighter rim, biased upper-left so
 *  the pocket reads as lit from the key light. */
export function recessedWellBackground(): string {
  return (
    'radial-gradient(125% 145% at 34% 22%,' +
    ' var(--meter-well-edge) 0%,' +
    ' var(--meter-well-floor) 58%,' +
    ' var(--meter-well-floor) 100%)'
  );
}

/** Multi-layer inset shadow stack giving the pocket its machined depth: a hard
 *  top lip, a faint cool far-wall bounce, a crisp edge hairline, and a soft
 *  inner vignette. Optional warm halo + outer bloom. */
export function recessedWellShadow(opts: RecessedWellOptions = {}): string {
  const layers: string[] = [
    'inset 0 2px 5px rgba(0,0,0,0.88)',          // hard top lip
    'inset 0 -1px 1.5px rgba(176,210,255,0.05)', // cool far-wall bounce
    'inset 0 0 0 1px rgba(0,0,0,0.60)',          // crisp pocket-edge hairline
    'inset 0 0 12px rgba(0,0,0,0.50)',           // inner vignette — depth
  ];
  if (opts.warmHalo) {
    layers.push('0 0 18px rgba(255,140,40,0.18)');
    layers.push('0 0 6px rgba(255,170,80,0.12)');
  }
  if (opts.glow) layers.push('0 0 22px var(--gauge-glow)');
  return layers.join(', ');
}

/** Full recessed-well CSS — background + concave depth + radius. Spread into a
 *  track/face wrapper; the caller owns size/layout. */
export function recessedWell(opts: RecessedWellOptions = {}): CSSProperties {
  return {
    background: recessedWellBackground(),
    borderRadius: opts.radius ?? 3,
    boxShadow: recessedWellShadow(opts),
  };
}
