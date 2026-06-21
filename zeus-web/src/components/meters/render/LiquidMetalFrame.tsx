// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// LiquidMetalFrame — the pure-CSS liquid-metal instrument frame. Composes the
// five-layer stack around ANY meter content so the look applies with a one-
// line wrap and scales perfectly to any size (no SVG-over-DOM distortion):
//
//   chrome bezel (gradient-border padding wrapper)
//     └ recessed well (concave pocket, clipped)
//         ├ {children}            — the live meter (bars / needle / readout)
//         └ glass overlay         — static domed specular highlight
//
// Token-only, DISPLAY-ONLY. The glass overlay is pointer-events:none so it
// never intercepts the meter's own interactions. (The moving sheen / caustic
// light sweeps were removed.)

import type { CSSProperties, ReactNode } from 'react';

const CHROME_BEZEL =
  'linear-gradient(135deg,' +
  ' var(--meter-bezel-hi) 0%,' +
  ' rgba(255,255,255,0.10) 18%,' +
  ' var(--meter-bezel-mid) 46%,' +
  ' rgba(0,0,0,0.34) 72%,' +
  ' var(--meter-bezel-lo) 100%)';

const GLASS_DOME =
  'radial-gradient(62% 62% at 32% 22%,' +
  ' var(--meter-glass-dome) 0%,' +
  ' var(--meter-glass-top) 45%,' +
  ' var(--meter-glass-bot) 100%)';

export interface LiquidMetalFrameProps {
  children: ReactNode;
  /** Outer corner radius. Default 7. */
  radius?: number;
  /** Chrome bezel thickness (px). Default 2.5. */
  bezel?: number;
  /** Warm amber halo around the well (signal meters). Default false. */
  warmHalo?: boolean;
  /** Outer cool instrument bloom. Default false. */
  glow?: boolean;
  /** Draw the glass overlay (static domed specular). Default true. */
  glass?: boolean;
  className?: string;
  style?: CSSProperties;
  /** Style applied to the inner well (e.g. min-height, padding). */
  wellStyle?: CSSProperties;
}

export function LiquidMetalFrame({
  children,
  radius = 7,
  bezel = 2.5,
  warmHalo = false,
  glow = false,
  glass = true,
  className,
  style,
  wellStyle,
}: LiquidMetalFrameProps) {
  const innerRadius = Math.max(1, radius - bezel);

  const outerShadow: string[] = ['0 1px 2px rgba(0,0,0,0.55)', 'inset 0 1px 0 rgba(255,255,255,0.05)'];
  if (glow) outerShadow.push('0 0 22px var(--gauge-glow)');
  if (warmHalo) outerShadow.push('0 0 18px rgba(255,140,40,0.16)');

  const wellShadow = [
    'inset 0 2px 5px rgba(0,0,0,0.88)',
    'inset 0 -1px 1.5px rgba(176,210,255,0.05)',
    'inset 0 0 0 1px rgba(0,0,0,0.60)',
    'inset 0 0 12px rgba(0,0,0,0.50)',
  ].join(', ');

  return (
    <div
      className={className}
      style={{
        position: 'relative',
        borderRadius: radius,
        padding: bezel,
        background: CHROME_BEZEL,
        boxShadow: outerShadow.join(', '),
        boxSizing: 'border-box',
        ...style,
      }}
    >
      <div
        style={{
          position: 'relative',
          borderRadius: innerRadius,
          overflow: 'hidden',
          background:
            'radial-gradient(125% 145% at 34% 22%, var(--meter-well-edge) 0%, var(--meter-well-floor) 58%, var(--meter-well-floor) 100%)',
          boxShadow: wellShadow,
          height: '100%',
          ...wellStyle,
        }}
      >
        {children}
        {glass && (
          <div
            aria-hidden
            style={{
              position: 'absolute',
              inset: 0,
              borderRadius: innerRadius,
              pointerEvents: 'none',
              background: GLASS_DOME,
              overflow: 'hidden',
            }}
          >
            {/* top edge-light rim */}
            <div
              style={{
                position: 'absolute',
                insetInline: 0,
                top: 0,
                height: '7%',
                background: 'var(--meter-glass-dome)',
                opacity: 0.45,
              }}
            />
          </div>
        )}
      </div>
    </div>
  );
}
