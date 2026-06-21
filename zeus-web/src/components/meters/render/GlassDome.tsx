// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// GlassDome — the domed-glass specular layer reading as light catching curved
// glass over an instrument. Layer (3). Renders ABOVE the well + fill, BELOW
// the bezel. An upper-left specular dome plus a thin top edge-light. Paint-once
// defs. DISPLAY-ONLY, id-namespaced. (The moving sheen / caustic light sweeps
// were removed.)

import { prefixDefs } from './svgChrome';

interface GlassDomeProps {
  defsId: string;
  x: number;
  y: number;
  width: number;
  height: number;
  rx?: number;
  /** Specular-dome colour (compact callers pass --meter-glass-dome-sm). */
  domeColor?: string;
}

/** Specular glass cover, lit from the shared upper-left key direction. */
export function GlassDome({
  defsId,
  x,
  y,
  width,
  height,
  rx,
  domeColor = 'var(--meter-glass-dome)',
}: GlassDomeProps) {
  const domeId = prefixDefs(defsId, 'glassdome');

  return (
    <>
      <defs>
        {/* upper-left specular hot-spot — bright core fading out */}
        <radialGradient id={domeId} cx="32%" cy="22%" r="62%">
          <stop offset="0" stopColor={domeColor} />
          <stop offset="0.45" stopColor="var(--meter-glass-top)" />
          <stop offset="1" stopColor="var(--meter-glass-bot)" />
        </radialGradient>
      </defs>

      {/* domed specular highlight over the whole glass */}
      <rect x={x} y={y} width={width} height={height} rx={rx} fill={`url(#${domeId})`} />

      {/* thin top edge-light rim */}
      <rect x={x} y={y} width={width} height={Math.max(1, height * 0.06)} rx={rx} fill={domeColor} opacity={0.5} />
    </>
  );
}
