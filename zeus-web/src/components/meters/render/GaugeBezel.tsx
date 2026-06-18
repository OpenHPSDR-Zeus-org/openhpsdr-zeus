// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// GaugeBezel — the machined chrome ring framing a gauge so it sits IN the
// panel, not ON it. Layer (4) of the instrument stack and the single biggest
// "whoa" tell. A 135° gradient runs a light edge top-left (--meter-bezel-hi)
// into a dark edge bottom-right (--meter-bezel-lo); a thin inner rim
// (--meter-bezel-ring) catches the key. SVG analogue of the WebGL shader's
// rim/goldRim. Paint-once static art — never animates.
//
// rect | arc variants. All <defs> ids are namespaced via prefixDefs() since
// 6-12 gauges share one page. DISPLAY-ONLY.

import { prefixDefs } from './svgChrome';

interface BezelGradientDef {
  /** Resolved gradient id (already run through prefixDefs by the caller). */
  id: string;
}

/** The shared 135° metallic edge gradient — light TL → dark BR. Drop this
 *  inside the consuming SVG's <defs>. `hiColor` overrides the bright TL edge
 *  stop (compact VU columns pass `--meter-bezel-hi-sm` so the chrome reads at
 *  small bar size); defaults to the standard `--meter-bezel-hi`. */
export function BezelGradient({ id, hiColor }: BezelGradientDef & { hiColor?: string }) {
  return (
    <linearGradient id={id} x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stopColor={hiColor ?? 'var(--meter-bezel-hi)'} />
      <stop offset="0.32" stopColor="rgba(255,255,255,0.04)" />
      <stop offset="0.68" stopColor="rgba(0,0,0,0.30)" />
      <stop offset="1" stopColor="var(--meter-bezel-lo)" />
    </linearGradient>
  );
}

interface RectBezelProps {
  variant: 'rect';
  /** Defs prefix; ids namespaced under it. */
  defsId: string;
  x: number;
  y: number;
  width: number;
  height: number;
  rx?: number;
  /** Bezel ring thickness in viewBox units. Default 3. */
  thickness?: number;
  /** Keep stroke width constant under preserveAspectRatio="none" stretch so
   *  the chrome doesn't smear (wide-and-short tracks). */
  nonScaling?: boolean;
  /** Override the bright TL bezel edge (compact bars pass
   *  `--meter-bezel-hi-sm` so the chrome survives at small size). */
  hiColor?: string;
}

interface ArcBezelProps {
  variant: 'arc';
  defsId: string;
  /** Full SVG path `d` for the ring stroke (the caller draws the arc). */
  d: string;
  /** Bezel ring thickness. Default 6. */
  thickness?: number;
  /** Override the bright TL bezel edge. */
  hiColor?: string;
}

export type GaugeBezelProps = RectBezelProps | ArcBezelProps;

/**
 * Machined metallic bezel ring. For `rect`, draws the chrome frame as a
 * stroked rounded rect plus a thin inner rim. For `arc`, strokes the supplied
 * path with the metallic gradient plus an inner-rim hairline. Render this
 * ABOVE the well + glass so the chrome frames everything.
 */
export function GaugeBezel(props: GaugeBezelProps) {
  const gradId = prefixDefs(props.defsId, 'bezelgrad');

  if (props.variant === 'rect') {
    const t = props.thickness ?? 3;
    const half = t / 2;
    const ve = props.nonScaling ? ('non-scaling-stroke' as const) : undefined;
    return (
      <>
        <defs>
          <BezelGradient id={gradId} hiColor={props.hiColor} />
        </defs>
        {/* outer machined frame — the thick chrome edge */}
        <rect
          x={props.x + half}
          y={props.y + half}
          width={Math.max(0, props.width - t)}
          height={Math.max(0, props.height - t)}
          rx={props.rx}
          fill="none"
          stroke={`url(#${gradId})`}
          strokeWidth={t}
          vectorEffect={ve}
        />
        {/* thin inner rim — the key-catching hairline just inside the frame */}
        <rect
          x={props.x + t}
          y={props.y + t}
          width={Math.max(0, props.width - 2 * t)}
          height={Math.max(0, props.height - 2 * t)}
          rx={props.rx ? Math.max(0, props.rx - half) : undefined}
          fill="none"
          stroke="var(--meter-bezel-ring)"
          strokeWidth={0.75}
          vectorEffect={ve}
        />
      </>
    );
  }

  const t = props.thickness ?? 6;
  return (
    <>
      <defs>
        <BezelGradient id={gradId} hiColor={props.hiColor} />
      </defs>
      <path d={props.d} fill="none" stroke={`url(#${gradId})`} strokeWidth={t} strokeLinecap="round" />
      <path
        d={props.d}
        fill="none"
        stroke="var(--meter-bezel-ring)"
        strokeWidth={0.85}
        strokeLinecap="round"
        opacity={0.9}
      />
    </>
  );
}
