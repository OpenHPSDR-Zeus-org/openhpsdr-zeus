// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// fillGradient — the vertical "volume" gradient that makes a meter fill read
// cylindrical: a dark floor (--meter-fill-base) → the live signal colour core
// → a bright top crown (--meter-fill-hot). It layers UNDER the existing
// horizontal good→warn→tx HUE ramp, so the bar gains depth while the hue ramp
// keeps its semantic meaning untouched. Layer (2) of the instrument stack.
//
// Returned as <stop> children so callers can drop it into a vertical
// <linearGradient> that's multiplied over (or composited under) their crisp
// horizontal fill. DISPLAY-ONLY.

import type { ReactNode } from 'react';

export interface VolumeStopsOptions {
  /** Base floor alpha-darken stop. Defaults to --meter-fill-base. */
  base?: string;
  /** Bright crown stop at the top. Defaults to --meter-fill-hot. */
  hot?: string;
}

/**
 * Vertical-volume gradient stops (top y=0 → bottom y=1 in a 0→1 gradient).
 * The crown sits near the top, a clear mid lets the underlying hue show, and
 * a dark floor anchors the bottom — the classic lit-cylinder shading.
 *
 * Use with a vertical <linearGradient> drawn over the crisp fill at a low/
 * screen-ish composite so it shades rather than recolours.
 */
export function volumeStops(opts: VolumeStopsOptions = {}): ReactNode {
  const base = opts.base ?? 'var(--meter-fill-base)';
  const hot = opts.hot ?? 'var(--meter-fill-hot)';
  return [
    <stop key="crown" offset="0" stopColor={hot} />,
    <stop key="hi" offset="0.18" stopColor="rgba(255,255,255,0.10)" />,
    <stop key="mid" offset="0.5" stopColor="rgba(0,0,0,0)" />,
    <stop key="lo" offset="0.82" stopColor="rgba(0,0,0,0.18)" />,
    <stop key="floor" offset="1" stopColor={base} />,
  ];
}

/** A CSS linear-gradient string equivalent for DOM-only callers. Vertical,
 *  top→bottom, same crown/mid/floor shading as the SVG stops. */
export function volumeGradientCss(opts: VolumeStopsOptions = {}): string {
  const base = opts.base ?? 'var(--meter-fill-base)';
  const hot = opts.hot ?? 'var(--meter-fill-hot)';
  return (
    `linear-gradient(180deg,` +
    ` ${hot} 0%,` +
    ` rgba(255,255,255,0.10) 18%,` +
    ` rgba(0,0,0,0) 50%,` +
    ` rgba(0,0,0,0.18) 82%,` +
    ` ${base} 100%)`
  );
}
