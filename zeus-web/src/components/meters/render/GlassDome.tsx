// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// GlassDome — the domed-glass specular layer reading as light catching curved
// glass over an instrument. Layer (3). Renders ABOVE the well + fill, BELOW
// the bezel. Elevated over the prior kit: in addition to the upper-left
// specular dome, a top edge-light, and the slow horizontal sheen band, it now
// carries a second TRAVELING CAUSTIC — a soft cool wet-light blob that drifts
// across the glass on a longer, offset loop, so the glass reads alive rather
// than a static highlight.
//
// All drift is compositor-only CSS @keyframes translateX (layout.css), PAUSED
// by prefers-reduced-motion and when the gauge is off-screen. Paint-once defs.
// DISPLAY-ONLY, id-namespaced.

import { prefixDefs } from './svgChrome';

interface GlassDomeProps {
  defsId: string;
  x: number;
  y: number;
  width: number;
  height: number;
  rx?: number;
  /** Draw the drifting sheen band. Default true. */
  sheen?: boolean;
  /** Draw the traveling caustic blob. Default true. */
  caustic?: boolean;
  /** Sheen band width as a fraction of width. Default 0.16. */
  sheenWidthFrac?: number;
  /** Specular-dome colour (compact callers pass --meter-glass-dome-sm). */
  domeColor?: string;
  /** Sheen band colour. */
  sheenColor?: string;
}

/** Specular glass cover, lit from the shared upper-left key direction. */
export function GlassDome({
  defsId,
  x,
  y,
  width,
  height,
  rx,
  sheen = true,
  caustic = true,
  sheenWidthFrac = 0.16,
  domeColor = 'var(--meter-glass-dome)',
  sheenColor = 'var(--meter-sheen)',
}: GlassDomeProps) {
  const domeId = prefixDefs(defsId, 'glassdome');
  const sheenId = prefixDefs(defsId, 'glasssheen');
  const causticId = prefixDefs(defsId, 'glasscaustic');
  const sheenW = Math.max(2, width * sheenWidthFrac);
  const causticW = Math.max(3, width * 0.28);

  return (
    <>
      <defs>
        {/* upper-left specular hot-spot — bright core fading out */}
        <radialGradient id={domeId} cx="32%" cy="22%" r="62%">
          <stop offset="0" stopColor={domeColor} />
          <stop offset="0.45" stopColor="var(--meter-glass-top)" />
          <stop offset="1" stopColor="var(--meter-glass-bot)" />
        </radialGradient>
        {/* the moving sheen band — a thin bright vertical streak, soft edges */}
        <linearGradient id={sheenId} x1="0" y1="0" x2="1" y2="0">
          <stop offset="0" stopColor="var(--meter-sheen-soft)" stopOpacity="0" />
          <stop offset="0.5" stopColor={sheenColor} />
          <stop offset="1" stopColor="var(--meter-sheen-soft)" stopOpacity="0" />
        </linearGradient>
        {/* the traveling caustic — a soft cool wet-light blob */}
        <radialGradient id={causticId} cx="50%" cy="40%" r="55%">
          <stop offset="0" stopColor="var(--meter-caustic)" />
          <stop offset="1" stopColor="var(--meter-caustic-soft)" stopOpacity="0" />
        </radialGradient>
      </defs>

      {/* domed specular highlight over the whole glass */}
      <rect x={x} y={y} width={width} height={height} rx={rx} fill={`url(#${domeId})`} />

      {/* thin top edge-light rim */}
      <rect x={x} y={y} width={width} height={Math.max(1, height * 0.06)} rx={rx} fill={domeColor} opacity={0.5} />

      {/* traveling caustic — slower, offset loop (behind the sheen) */}
      {caustic && (
        <g
          className="meter-caustic-drift"
          style={{ ['--caustic-travel' as string]: `${width + causticW}px` }}
        >
          <rect x={x - causticW} y={y} width={causticW} height={height} rx={rx} fill={`url(#${causticId})`} />
        </g>
      )}

      {/* drifting sheen band — compositor-only CSS drift */}
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
