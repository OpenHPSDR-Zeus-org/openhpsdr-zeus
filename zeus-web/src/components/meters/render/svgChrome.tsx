// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Shared SVG render kit for the meter cluster — the central, token-only
// implementation of the "bloom-behind-crisp + glowing-peak" idiom that the
// immersive primitives (BigArc / VuColumn / HBarMeter / PullDownArc) already
// prove. Extracted here so the same lit-instrument look can be lifted onto
// the flat DOM meters (SMeter, MicMeter, TxStageMeters) without copy-pasting
// the blurred-copy + drop-shadow-pip code a fourth and fifth time.
//
// DISPLAY-ONLY: nothing here touches the meter value pipeline, ballistics,
// or peak-hold physics. Callers compute every fraction/position from the
// already-smoothed {value, peak}; this kit only decides how those are DRAWN.
//
// Per-instance id prefixing is MANDATORY — many gauges share one page, so a
// shared <filter>/<gradient> id added by this kit must be namespaced via
// `prefixDefs()` or one gauge's blur bleeds into another's.

import type { ReactNode } from 'react';

// ── id namespacing ──────────────────────────────────────────────────────
// Sanitise a caller-supplied defs prefix into a DOM-safe id stem and join a
// local part onto it. Mirrors the `def.id.replace(/\W/g, '_')` pattern
// already in use across HBarMeter so existing ids are unchanged.
export function prefixDefs(defsId: string, local: string): string {
  const stem = defsId.replace(/\W/g, '_');
  return `${stem}-${local}`;
}

// ── shared blur filter ──────────────────────────────────────────────────
// The single feGaussianBlur every two-pass fill uses. stdDeviation kept at 3
// (byte-identical to the value inlined in BigArc / VuColumn / HBarMeter) so
// lifting these into the kit does not shift the rendered halo. `pad` widens
// the filter region for callers that need extra bleed room (HBarMeter uses a
// wide horizontal region; VuColumn a tall vertical one) — default covers the
// common 180% box.
interface BloomFilterProps {
  /** Fully-resolved filter id (run the caller's prefix through prefixDefs). */
  id: string;
  /** Blur radius. Defaults to 3 — the established meter bloom radius. */
  stdDeviation?: number;
  /** Filter region as [x, y, width, height] in % strings. */
  region?: readonly [string, string, string, string];
}

export function BloomFilter({
  id,
  stdDeviation = 3,
  region = ['-40%', '-40%', '180%', '180%'],
}: BloomFilterProps) {
  const [x, y, width, height] = region;
  return (
    <filter id={id} x={x} y={y} width={width} height={height}>
      <feGaussianBlur stdDeviation={stdDeviation} />
    </filter>
  );
}

// ── bloom-behind-crisp fill ─────────────────────────────────────────────
// Draws an SVG shape twice: a blurred, wider, low-opacity copy UNDER a crisp
// copy on top. This is the core "lit trace halo" tell shared by every
// premium meter. Generalised over rect | path | line so all the gauges and
// the DOM bars share ONE implementation instead of three near-identical
// copies. The caller owns geometry + fill (gradient url or token); this owns
// the two-pass stack and the blur wiring.
type BloomShape =
  | {
      kind: 'rect';
      x: number | string;
      y: number | string;
      width: number | string;
      height: number | string;
      rx?: number;
    }
  | { kind: 'path'; d: string }
  | {
      kind: 'line';
      x1: number | string;
      y1: number | string;
      x2: number | string;
      y2: number | string;
    };

interface BloomFillProps {
  shape: BloomShape;
  /** Crisp-copy paint — a gradient `url(#…)` or a token color string. */
  fill: string;
  /** Bloom-copy paint. Defaults to `fill`; pass a softer gradient when one
   *  exists (VuColumn / HBarMeter keep a dedicated half-opacity bloom grad). */
  bloomFill?: string;
  /** Resolved id of the BloomFilter the blurred copy references. */
  filterId: string;
  /** Bloom-copy opacity. 0.85 matches the immersive primitives. */
  bloomOpacity?: number;
  /** For stroke-based shapes (path/line arcs): crisp + bloom stroke widths. */
  strokeWidth?: number;
  bloomStrokeWidth?: number;
  /** strokeLinecap for stroked shapes. */
  strokeLinecap?: 'butt' | 'round' | 'square';
  /** strokeDasharray for partial-fill arcs. */
  strokeDasharray?: string;
}

function renderShape(
  shape: BloomShape,
  paint: string,
  stroked: boolean,
  strokeWidth: number | undefined,
  strokeLinecap: BloomFillProps['strokeLinecap'],
  strokeDasharray: string | undefined,
  filterId: string | null,
  opacity: number | undefined,
): ReactNode {
  const strokeProps = stroked
    ? {
        fill: 'none' as const,
        stroke: paint,
        strokeWidth,
        strokeLinecap,
        strokeDasharray,
      }
    : { fill: paint };
  const common = {
    ...strokeProps,
    ...(filterId ? { filter: `url(#${filterId})` } : {}),
    ...(opacity != null ? { opacity } : {}),
  };
  switch (shape.kind) {
    case 'rect':
      return (
        <rect
          x={shape.x}
          y={shape.y}
          width={shape.width}
          height={shape.height}
          rx={shape.rx}
          {...common}
        />
      );
    case 'path':
      return <path d={shape.d} {...common} />;
    case 'line':
      return <line x1={shape.x1} y1={shape.y1} x2={shape.x2} y2={shape.y2} {...common} />;
  }
}

export function BloomFill({
  shape,
  fill,
  bloomFill,
  filterId,
  bloomOpacity = 0.85,
  strokeWidth,
  bloomStrokeWidth,
  strokeLinecap,
  strokeDasharray,
}: BloomFillProps) {
  const stroked = shape.kind !== 'rect';
  return (
    <>
      {renderShape(
        shape,
        bloomFill ?? fill,
        stroked,
        bloomStrokeWidth ?? strokeWidth,
        strokeLinecap,
        strokeDasharray,
        filterId,
        bloomOpacity,
      )}
      {renderShape(
        shape,
        fill,
        stroked,
        strokeWidth,
        strokeLinecap,
        strokeDasharray,
        null,
        undefined,
      )}
    </>
  );
}

// ── glowing peak markers ────────────────────────────────────────────────
// A warm-cream peak pip (BigArc rim pearl) and a peak tick (VuColumn /
// HBarMeter / DOM bar). Both carry the established drop-shadow halo so the
// peak reads as a lit indicator rather than a hairline. Geometry stays the
// caller's job; this fixes the look.
interface PeakPipProps {
  cx: number | string;
  cy: number | string;
  r?: number;
  /** Halo color token. Defaults to the warm lamp pin. */
  glow?: string;
  fill?: string;
  stroke?: string;
}

export function PeakPip({
  cx,
  cy,
  r = 3,
  glow = 'var(--immersive-lamp-pin)',
  fill = '#fff',
  stroke = 'var(--immersive-lamp-pin)',
}: PeakPipProps) {
  return (
    <circle
      cx={cx}
      cy={cy}
      r={r}
      fill={fill}
      stroke={stroke}
      strokeWidth={1}
      // Brighter halo for the taller dramatic gauges — a tight bright core
      // over a wider soft bloom so the peak pearl reads as a lit indicator.
      style={{ filter: `drop-shadow(0 0 4px ${glow}) drop-shadow(0 0 9px ${glow})` }}
    />
  );
}

interface PeakTickProps {
  x1: number | string;
  y1: number | string;
  x2: number | string;
  y2: number | string;
  stroke?: string;
  strokeWidth?: number;
  /** Halo color. Defaults to the stroke (so a white tick glows white). */
  glow?: string;
  opacity?: number;
  /** Keep the tick crisp under SVG `preserveAspectRatio="none"` stretch. */
  nonScaling?: boolean;
}

export function PeakTick({
  x1,
  y1,
  x2,
  y2,
  stroke = '#fff',
  strokeWidth = 1.8,
  glow,
  opacity = 0.95,
  nonScaling = false,
}: PeakTickProps) {
  return (
    <line
      x1={x1}
      y1={y1}
      x2={x2}
      y2={y2}
      stroke={stroke}
      strokeWidth={strokeWidth}
      opacity={opacity}
      vectorEffect={nonScaling ? 'non-scaling-stroke' : undefined}
      // Stronger double-bloom halo so the latched peak marker reads as a lit
      // indicator on the taller, brighter dramatic tracks.
      style={{ filter: `drop-shadow(0 0 4px ${glow ?? stroke}) drop-shadow(0 0 8px ${glow ?? stroke})` }}
    />
  );
}
