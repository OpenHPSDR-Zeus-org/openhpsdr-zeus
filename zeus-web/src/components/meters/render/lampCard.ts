// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// The warm-lamp card recipe — the three-radial cream-bloom background over a
// dark linear well, plus the inset rim / under-glow / vignette boxShadow —
// extracted from the verbatim copies in BigArc.cardStyle and
// VuColumn.cardStyle. Centralised so the flat DOM meters (SMeter, MicMeter,
// TxStageMeters rows) can opt into the same lit-instrument surface, and so a
// future colour-temperature tweak lives in one place.
//
// Token-only. The light-theme flip already comes for free from the
// --immersive-lamp-* token overrides (the warm blooms go transparent on the
// silver chassis, the well flips to brushed silver). DISPLAY-ONLY.

import type { CSSProperties } from 'react';

export type LampCardVariant =
  // Bottom-anchored bloom (an arc face lit from below — BigArc / PullDownArc).
  | 'arc'
  // Bottom-anchored bloom tuned for a tall column (VuColumn).
  | 'column';

interface LampCardOptions {
  /** Corner radius. Defaults to 7 (the immersive card radius). */
  radius?: number;
  /** Override the inset depth shadow — column cards use a shallower well. */
  insetShadow?: string;
}

/** The cream-bloom background stack for a lit gauge face. */
export function lampCardBackground(variant: LampCardVariant): string {
  const bloomGeom =
    variant === 'column'
      ? '80% 95% at 50% 100%'
      : '80% 95% at 50% 95%';
  return (
    `radial-gradient(${bloomGeom}, var(--immersive-lamp-bloom-1), var(--immersive-lamp-bloom-2) ${
      variant === 'column' ? '50%' : '45%'
    }, transparent ${variant === 'column' ? '75%' : '72%'}),` +
    ' radial-gradient(60% 60% at 50% 70%, var(--immersive-lamp-bloom-3), transparent 65%),' +
    ' linear-gradient(180deg, var(--immersive-lamp-well-top) 0%, var(--immersive-lamp-well-bot) 100%)'
  );
}

/** The inset rim + warm under-glow + vignette depth shadow for a lit card. */
export function lampInsetShadow(variant: LampCardVariant): string {
  return variant === 'column'
    ? 'inset 0 1px 0 var(--immersive-lamp-rim), inset 0 -18px 32px rgba(255,240,180,0.04), inset 0 0 22px rgba(0,0,0,0.40)'
    : 'inset 0 1px 0 var(--immersive-lamp-rim), inset 0 -22px 40px rgba(255,240,180,0.05), inset 0 0 50px rgba(0,0,0,0.55)';
}

/** Full lit-instrument card surface: background + border + depth shadow.
 *  Layout (position / aspect / flex) stays the caller's job — this only owns
 *  the illuminated-surface look so it can be spread into any card style. */
export function lampCardStyle(
  variant: LampCardVariant,
  opts: LampCardOptions = {},
): CSSProperties {
  return {
    background: lampCardBackground(variant),
    border: '1px solid var(--immersive-lamp-border)',
    borderRadius: opts.radius ?? 7,
    boxShadow: opts.insetShadow ?? lampInsetShadow(variant),
  };
}

/** A recessed-well inset shadow for a thin DOM meter track that wants the
 *  lit-instrument depth without the full cream-bloom card behind it (SMeter
 *  track, MicMeter bar). Keeps the warm amber halo the TxStageMeters rows
 *  already use so every moving bar reads as the same lit channel. */
export function meterWellShadow(): string {
  return (
    'inset 0 1px 3px rgba(0,0,0,0.8),' +
    ' inset 0 0 0 1px rgba(255,255,255,0.03),' +
    ' 0 0 18px rgba(255,140,40,0.18),' +
    ' 0 0 6px rgba(255,170,80,0.12)'
  );
}
