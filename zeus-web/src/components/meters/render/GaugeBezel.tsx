// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// GaugeBezel — the machined chrome ring framing a gauge so it sits IN the
// panel, not ON it. Layer (4), the single biggest "whoa" tell. Elevated over
// the prior kit with a four-stop anisotropic sweep (warm key edge TL → bright
// flash → grey mid-roll → deep shadow BR) so the ring reads like turned metal
// catching the light, plus a thin inner rim that catches the key.
//
// rect | arc variants. All <defs> ids namespaced via prefixDefs() since 6-12
// gauges share one page. Paint-once static art — never animates. DISPLAY-ONLY.

import { prefixDefs } from './svgChrome';

interface BezelGradientDef {
  id: string;
}

/** The shared anisotropic metallic edge gradient — warm key TL → bright flash
 *  → grey roll → deep shadow BR. Drop inside the consuming SVG's <defs>. */
export function BezelGradient({ id, hiColor }: BezelGradientDef & { hiColor?: string }) {
  return (
    <linearGradient id={id} x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stopColor={hiColor ?? 'var(--meter-bezel-hi)'} />
      <stop offset="0.18" stopColor="rgba(255,255,255,0.10)" />
      <stop offset="0.46" stopColor="var(--meter-bezel-mid)" />
      <stop offset="0.72" stopColor="rgba(0,0,0,0.34)" />
      <stop offset="1" stopColor="var(--meter-bezel-lo)" />
    </linearGradient>
  );
}

interface RectBezelProps {
  variant: 'rect';
  defsId: string;
  x: number;
  y: number;
  width: number;
  height: number;
  rx?: number;
  /** Bezel ring thickness in viewBox units. Default 3. */
  thickness?: number;
  /** Keep stroke width constant under preserveAspectRatio="none" stretch. */
  nonScaling?: boolean;
  /** Override the bright TL edge (compact bars pass --meter-bezel-hi-sm). */
  hiColor?: string;
}

interface ArcBezelProps {
  variant: 'arc';
  defsId: string;
  /** Full SVG path `d` for the ring stroke. */
  d: string;
  /** Bezel ring thickness. Default 6. */
  thickness?: number;
  hiColor?: string;
}

export type GaugeBezelProps = RectBezelProps | ArcBezelProps;

/** Machined metallic bezel ring. Render ABOVE the well + glass so the chrome
 *  frames everything. */
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
          width={props.width - t}
          height={props.height - t}
          rx={props.rx}
          fill="none"
          stroke={`url(#${gradId})`}
          strokeWidth={t}
          vectorEffect={ve}
        />
        {/* thin inner rim — the key-light catch on the inner lip */}
        <rect
          x={props.x + t}
          y={props.y + t}
          width={props.width - t * 2}
          height={props.height - t * 2}
          rx={props.rx ? Math.max(0, props.rx - t) : undefined}
          fill="none"
          stroke="var(--meter-bezel-ring)"
          strokeWidth={1}
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
      <path d={props.d} fill="none" stroke="var(--meter-bezel-ring)" strokeWidth={1} strokeLinecap="round" />
    </>
  );
}
