// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// MeterGlass — drop-in glass cover for any relative, overflow-hidden meter
// well. Domed upper-left specular + an optional drifting sheen band (and a
// slower traveling caustic). pointer-events:none so it never blocks the meter.
// One-liner application of the liquid-metal glass layer. DISPLAY-ONLY.

import type { CSSProperties } from 'react';

export interface MeterGlassProps {
  /** Match the well's corner radius. */
  radius?: CSSProperties['borderRadius'];
  /** Drifting sheen band. Default true. */
  sheen?: boolean;
  /** Traveling caustic blob. Default false (on for larger wells). */
  caustic?: boolean;
  /** Sheen band width (% of well). Default '16%'. */
  sheenWidth?: string;
}

export function MeterGlass({ radius, sheen = true, caustic = false, sheenWidth = '16%' }: MeterGlassProps) {
  return (
    <div
      aria-hidden
      style={{
        position: 'absolute',
        inset: 0,
        borderRadius: radius,
        pointerEvents: 'none',
        overflow: 'hidden',
        background:
          'radial-gradient(64% 130% at 32% 16%, var(--meter-glass-dome) 0%, var(--meter-glass-top) 46%, var(--meter-glass-bot) 100%)',
      }}
    >
      {caustic && (
        <div
          className="lm-caustic"
          style={{
            position: 'absolute',
            insetBlock: 0,
            left: 0,
            width: '30%',
            background:
              'radial-gradient(60% 130% at 50% 40%, var(--meter-caustic) 0%, var(--meter-caustic-soft) 100%)',
          }}
        />
      )}
      {sheen && (
        <div
          className="lm-sheen"
          style={{
            position: 'absolute',
            insetBlock: 0,
            left: 0,
            width: sheenWidth,
            background: 'linear-gradient(100deg, transparent 0%, var(--meter-sheen) 50%, transparent 100%)',
          }}
        />
      )}
    </div>
  );
}
