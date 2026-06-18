// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// GlassDome — the domed-glass specular layer that reads as light catching
// curved glass over an instrument. Layer (3) of the instrument stack:
//   • an upper-left-biased specular ellipse (--meter-glass-dome) — the SVG
//     analogue of the WebGL shader's pow() specular hot-spot,
//   • a thin edge-light along the top rim,
//   • a slow horizontally-drifting narrow sheen band (--meter-sheen) — light
//     sweeping across the glass on a multi-second loop.
//
// The drift is a compositor-only CSS @keyframes translateX (see
// .meter-sheen-drift in layout.css) — no JS, no draw-bus cost — and is
// PAUSED by prefers-reduced-motion (the animation is dropped) and when the
// gauge is off-screen (animation-play-state via the parent). Paint-once defs;
// only the sheen's transform animates. DISPLAY-ONLY, id-namespaced.

import { prefixDefs } from './svgChrome';

interface GlassDomeProps {
  /** Defs prefix; ids namespaced under it. */
  defsId: string;
  /** Glass region in viewBox units (the area under the glass). */
  x: number;
  y: number;
  width: number;
  height: number;
  /** Corner radius for the rounded glass rect. */
  rx?: number;
  /** Draw the drifting sheen band. Default true; set false to disable. */
  sheen?: boolean;
  /** Sheen band width as a fraction of `width`. Default 0.16. */
  sheenWidthFrac?: number;
  /** Specular-dome highlight colour. Defaults to the standard
   *  `--meter-glass-dome`; the compact VU columns pass `--meter-glass-dome-sm`
   *  so the wet-glass tell survives at small bar size. */
  domeColor?: string;
  /** Drifting-sheen band colour. Defaults to `--meter-sheen`; compact callers
   *  pass `--meter-sheen-sm`. */
  sheenColor?: string;
}

/**
 * Specular glass cover. Renders ABOVE the well + fill but BELOW the bezel.
 * The dome ellipse is biased to the upper-left so every gauge is lit from the
 * same key direction as the rest of the instrument family.
 */
export function GlassDome({
  defsId,
  x,
  y,
  width,
  height,
  rx,
  sheen = true,
  sheenWidthFrac = 0.16,
  domeColor = 'var(--meter-glass-dome)',
  sheenColor = 'var(--meter-sheen)',
}: GlassDomeProps) {
  const domeId = prefixDefs(defsId, 'glassdome');
  const sheenId = prefixDefs(defsId, 'glasssheen');
  const sheenW = Math.max(2, width * sheenWidthFrac);

  return (
    <>
      <defs>
        {/* upper-left specular hot-spot — bright core fading out, the pow()
            specular analogue. */}
        <radialGradient id={domeId} cx="32%" cy="22%" r="62%">
          <stop offset="0" stopColor={domeColor} />
          <stop offset="0.45" stopColor="var(--meter-glass-top)" />
          <stop offset="1" stopColor="var(--meter-glass-bot)" />
        </radialGradient>
        {/* the moving sheen band — a thin bright vertical streak, soft edges. */}
        <linearGradient id={sheenId} x1="0" y1="0" x2="1" y2="0">
          <stop offset="0" stopColor="var(--meter-sheen-soft)" stopOpacity="0" />
          <stop offset="0.5" stopColor={sheenColor} />
          <stop offset="1" stopColor="var(--meter-sheen-soft)" stopOpacity="0" />
        </linearGradient>
      </defs>

      {/* domed specular highlight over the whole glass */}
      <rect x={x} y={y} width={width} height={height} rx={rx} fill={`url(#${domeId})`} />

      {/* thin top edge-light rim */}
      <rect
        x={x}
        y={y}
        width={width}
        height={Math.max(1, height * 0.06)}
        rx={rx}
        fill={domeColor}
        opacity={0.5}
      />

      {/* drifting sheen band — the group carries the compositor-only CSS drift
          animation. The band starts off the left edge and travels one glass-
          width to the right (CSS var --sheen-travel, in SVG user units mapped
          by translateX(<n>px)). prefers-reduced-motion drops the animation
          (see layout.css). */}
      {sheen && (
        <g
          className="meter-sheen-drift"
          style={{ ['--sheen-travel' as string]: `${width + sheenW}px` }}
        >
          <rect x={x - sheenW} y={y} width={sheenW} height={height} rx={rx} fill={`url(#${sheenId})`} />
        </g>
      )}
    </>
  );
}
