// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// MeterGlass — drop-in glass cover for any relative, overflow-hidden meter
// well. A static domed upper-left specular highlight. pointer-events:none so it
// never blocks the meter. One-liner application of the liquid-metal glass
// layer. DISPLAY-ONLY. (The moving sheen / caustic light sweeps were removed.)

import type { CSSProperties } from 'react';

export interface MeterGlassProps {
  /** Match the well's corner radius. */
  radius?: CSSProperties['borderRadius'];
}

export function MeterGlass({ radius }: MeterGlassProps) {
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
    />
  );
}
