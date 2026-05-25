// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Simone Fabris (IU3QEZ), and contributors.

import { useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';

export function RitXitMarker() {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const itMode = useConnectionStore((s) => s.itMode);
  const ritOffsetHz = useConnectionStore((s) => s.ritOffsetHz);
  const xitOffsetHz = useConnectionStore((s) => s.xitOffsetHz);

  if (itMode === 'Off' || !width || hzPerPixel <= 0) return null;

  const offsetHz = itMode === 'Rit' ? ritOffsetHz : xitOffsetHz;
  if (offsetHz === 0) return null;

  const label = itMode === 'Rit' ? 'RX' : 'TX';
  const markerHz = vfoHz + offsetHz;

  const spanHz = width * hzPerPixel;
  const center = Number(centerHz);
  const startHz = center - spanHz / 2;
  const pct = ((markerHz - startHz) / spanHz) * 100;

  if (pct < 0 || pct > 100) return null;

  return (
    <div
      aria-hidden
      className="pointer-events-none absolute inset-y-0 z-[15] -translate-x-1/2"
      style={{ left: `${pct}%`, width: 2, background: 'var(--tx)', opacity: 0.85 }}
    >
      <span
        style={{
          position: 'absolute',
          top: 22,
          left: '50%',
          transform: 'translateX(-50%)',
          fontSize: '9px',
          fontWeight: 700,
          fontFamily: 'var(--font-mono, monospace)',
          color: 'var(--tx)',
          background: 'rgba(0,0,0,0.7)',
          padding: '1px 3px',
          borderRadius: 2,
          whiteSpace: 'nowrap',
          lineHeight: 1,
        }}
      >
        {label}
      </span>
    </div>
  );
}
