// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useCallback } from 'react';
import { setVfo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useSpotStore } from '../state/spot-store';

// Convert a packed ARGB uint32 to a CSS rgba() string.
// TCI clients commonly send A=0 meaning "default/opaque" rather than
// transparent, so A=0 is treated as fully opaque.
function argbToRgba(argb: number, opacity = 0.9): string {
  const a = (argb >>> 24) & 0xff;
  const r = (argb >>> 16) & 0xff;
  const g = (argb >>> 8) & 0xff;
  const b = argb & 0xff;
  const alpha = a === 0 ? opacity : (a / 255) * opacity;
  return `rgba(${r},${g},${b},${alpha})`;
}

// Thin vertical line + callsign label overlay for each in-view TCI spot.
// Positioned by percentage of the total span, identical to PassbandOverlay
// and FreqAxis, so no DOM measurement is needed on resize.
export function SpotOverlay() {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);
  const spots = useSpotStore((s) => s.spots);
  const vfoHz = useConnectionStore((s) => s.vfoHz);

  const handleSpotClick = useCallback(
    (freqHz: number) => {
      useConnectionStore.setState({ vfoHz: freqHz });
      setVfo(freqHz)
        .then((s) => useConnectionStore.getState().applyState(s))
        .catch(() => {});
    },
    [],
  );

  if (!width || hzPerPixel <= 0 || spots.length === 0) return null;

  const spanHz = width * hzPerPixel;
  const center = Number(centerHz);
  const startHz = center - spanHz / 2;

  const visible = spots.filter((s) => {
    const pct = ((s.freqHz - startHz) / spanHz) * 100;
    return pct >= -2 && pct <= 102;
  });

  if (visible.length === 0) return null;

  return (
    <>
      {visible.map((spot) => {
        const pct = ((spot.freqHz - startHz) / spanHz) * 100;
        const color = argbToRgba(spot.argb);
        const isNearVfo = Math.abs(spot.freqHz - vfoHz) < hzPerPixel * 4;
        return (
          <div
            key={spot.callsign}
            title={`${spot.callsign} ${spot.mode}${spot.comment ? ` — ${spot.comment}` : ''}`}
            onClick={() => handleSpotClick(spot.freqHz)}
            className="absolute inset-y-0 z-[8] -translate-x-1/2 cursor-pointer"
            style={{ left: `${pct}%` }}
          >
            {/* vertical marker line */}
            <div
              className="absolute inset-y-0 w-px"
              style={{
                background: color,
                opacity: isNearVfo ? 1 : 0.75,
              }}
            />
            {/* callsign label at top */}
            <div
              className="absolute top-5 -translate-x-1/2 select-none whitespace-nowrap font-mono text-[9px] leading-none px-0.5"
              style={{
                color,
                textShadow: '0 0 3px rgba(0,0,0,0.8)',
              }}
            >
              {spot.callsign}
            </div>
          </div>
        );
      })}
    </>
  );
}
