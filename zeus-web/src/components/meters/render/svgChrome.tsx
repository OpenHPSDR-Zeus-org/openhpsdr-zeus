// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Shared SVG render helpers for the liquid-metal meter kit: per-instance id
// namespacing (many gauges share one page, so a shared <filter>/<gradient> id
// MUST be prefixed or one gauge's blur bleeds into another's) and the
// bloom-behind-crisp "lit trace" idiom (a blurred wide low-opacity copy under
// a crisp copy). DISPLAY-ONLY.

import type { ReactNode } from 'react';

/** Sanitise a caller-supplied defs prefix into a DOM-safe id stem + local part. */
export function prefixDefs(defsId: string, local: string): string {
  const stem = defsId.replace(/\W/g, '_');
  return `${stem}-${local}`;
}

interface BloomFilterProps {
  /** Fully-resolved filter id (run the caller's prefix through prefixDefs). */
  id: string;
  /** Blur radius. Defaults to 3 — the established meter bloom radius. */
  stdDeviation?: number;
  /** Filter region [x, y, width, height] as % strings. */
  region?: readonly [string, string, string, string];
}

/** The single feGaussianBlur every two-pass fill uses. */
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

export type BloomShape =
  | { kind: 'rect'; x: number | string; y: number | string; width: number | string; height: number | string; rx?: number }
  | { kind: 'path'; d: string }
  | { kind: 'line'; x1: number | string; y1: number | string; x2: number | string; y2: number | string };

interface BloomFillProps {
  shape: BloomShape;
  /** Crisp-copy paint — a gradient url(#…) or token color string. */
  fill: string;
  /** Bloom-copy paint. Defaults to `fill`. */
  bloomFill?: string;
  /** Resolved id of the BloomFilter the blurred copy references. */
  filterId: string;
  /** Bloom-copy opacity. 0.85 matches the established look. */
  bloomOpacity?: number;
  /** For stroked shapes (path/line): crisp + bloom stroke widths. */
  strokeWidth?: number;
  bloomStrokeWidth?: number;
  strokeLinecap?: 'butt' | 'round' | 'square';
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
  const paintProps = stroked
    ? { fill: 'none' as const, stroke: paint, strokeWidth, strokeLinecap, strokeDasharray }
    : { fill: paint };
  const common = {
    ...paintProps,
    ...(filterId ? { filter: `url(#${filterId})` } : {}),
    ...(opacity != null ? { opacity } : {}),
  };
  switch (shape.kind) {
    case 'rect':
      return <rect x={shape.x} y={shape.y} width={shape.width} height={shape.height} rx={shape.rx} {...common} />;
    case 'path':
      return <path d={shape.d} {...common} />;
    case 'line':
      return <line x1={shape.x1} y1={shape.y1} x2={shape.x2} y2={shape.y2} {...common} />;
  }
}

/** Draw `shape` twice — a blurred, low-opacity copy UNDER a crisp copy — the
 *  core "lit trace halo" tell. rect → filled; path/line → stroked. */
export function BloomFill(props: BloomFillProps) {
  const stroked = props.shape.kind !== 'rect';
  return (
    <>
      {renderShape(
        props.shape,
        props.bloomFill ?? props.fill,
        stroked,
        props.bloomStrokeWidth ?? props.strokeWidth,
        props.strokeLinecap,
        props.strokeDasharray,
        props.filterId,
        props.bloomOpacity ?? 0.85,
      )}
      {renderShape(
        props.shape,
        props.fill,
        stroked,
        props.strokeWidth,
        props.strokeLinecap,
        props.strokeDasharray,
        null,
        undefined,
      )}
    </>
  );
}
