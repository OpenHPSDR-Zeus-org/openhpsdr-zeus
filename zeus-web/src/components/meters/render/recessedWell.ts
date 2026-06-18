// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// recessedWell — the concave "machined pocket" a meter fill sits inside.
// Generalises lampCard.ts meterWellShadow() (a single flat inset) into a
// real well: a concave radial floor gradient (--meter-well-floor centre →
// --meter-well-edge rim) plus a multi-layer inset box-shadow stack so the
// pocket reads as pressed into the metal chassis, lit from the upper-left.
//
// Token-only / DISPLAY-ONLY. This is layer (1) of the five-layer instrument
// stack; the volumetric fill (fillGradient), specular glass (GlassDome) and
// bezel ring (GaugeBezel) stack on top.

import type { CSSProperties } from 'react';

export interface RecessedWellOptions {
  /** Corner radius. Defaults to 3. */
  radius?: number;
  /** Add the warm amber halo (signal bars want it; chrome wells don't). */
  warmHalo?: boolean;
  /** Add the whole-gauge outer additive bloom (--gauge-glow). */
  glow?: boolean;
}

/** Background paint for a recessed well — a concave floor lit from the
 *  upper-left so the centre reads deepest and the rim catches the key. */
export function recessedWellBackground(): string {
  return (
    // Concave floor — dark centre rising to a slightly lighter rim, biased
    // to the upper-left so the pocket reads as lit from the key light.
    'radial-gradient(120% 140% at 35% 25%,' +
    ' var(--meter-well-edge) 0%,' +
    ' var(--meter-well-floor) 55%,' +
    ' var(--meter-well-floor) 100%)'
  );
}

/** The multi-layer inset shadow stack giving the pocket its machined depth.
 *  Two crisp inner edges (dark top, faint light bottom-rim catch) over a
 *  soft inner vignette. Optional warm amber halo + gauge bloom outside. */
export function recessedWellShadow(opts: RecessedWellOptions = {}): string {
  const layers: string[] = [
    // hard top-edge shadow — the lip of the pocket
    'inset 0 2px 4px rgba(0,0,0,0.85)',
    // faint bottom-rim light catch — the far wall reflecting the key
    'inset 0 -1px 1px rgba(255,255,255,0.06)',
    // crisp inner hairline so the pocket edge stays defined
    'inset 0 0 0 1px rgba(0,0,0,0.55)',
    // soft inner vignette — depth
    'inset 0 0 10px rgba(0,0,0,0.45)',
  ];
  if (opts.warmHalo) {
    layers.push('0 0 18px rgba(255,140,40,0.18)');
    layers.push('0 0 6px rgba(255,170,80,0.12)');
  }
  if (opts.glow) {
    layers.push('0 0 22px var(--gauge-glow)');
  }
  return layers.join(', ');
}

/** Full recessed-well CSS — background + concave depth shadow + radius.
 *  Spread into the track wrapper's style; the caller owns size / layout. */
export function recessedWell(opts: RecessedWellOptions = {}): CSSProperties {
  return {
    background: recessedWellBackground(),
    borderRadius: opts.radius ?? 3,
    boxShadow: recessedWellShadow(opts),
  };
}
