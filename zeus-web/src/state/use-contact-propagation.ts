// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useState } from 'react';
import { fetchPropagation, type PropagationResult } from '../api/propagation';
import { useQrzStore } from './qrz-store';
import { useConnectionStore } from './connection-store';

/** Map a Zeus RX mode to the propagation engine's mode family (drives SNR margin). */
function propMode(mode: string): string {
  const m = mode.toUpperCase();
  if (m.startsWith('CW')) return 'CW';
  if (m.startsWith('DIG') || m === 'FT8' || m === 'FT4' || m === 'DATA') return 'FT8';
  if (m.startsWith('AM') || m === 'SAM') return 'AM';
  if (m === 'FM' || m === 'NFM') return 'FM';
  return 'SSB';
}

type ContactPoint = { lat: number; lon: number } | null;

/**
 * Point-to-point propagation (your QTH → the looked-up contact) for the QRZ
 * card. Resolves DE from the QRZ home record and band/mode from the live radio,
 * then hits Zeus's /api/propagation proxy (HamClock P.533-14 engine). Returns
 * an unavailable result when home coords or the engine are missing — never
 * throws — so the card can simply hide the section.
 */
export function useContactPropagation(dx: ContactPoint): {
  data: PropagationResult | null;
  loading: boolean;
} {
  const home = useQrzStore((s) => s.home);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const mode = useConnectionStore((s) => s.mode);

  const [data, setData] = useState<PropagationResult | null>(null);
  const [loading, setLoading] = useState(false);

  const deLat = home?.lat ?? null;
  const deLon = home?.lon ?? null;
  const dxLat = dx?.lat ?? null;
  const dxLon = dx?.lon ?? null;
  // Bucket the frequency to the nearest 100 kHz so small VFO nudges within a
  // band don't refire the request (the backend also caches per rounded path).
  const freqBucketMhz = Math.round(vfoHz / 1e5) / 10;

  useEffect(() => {
    if (deLat == null || deLon == null || dxLat == null || dxLon == null) {
      setData(null);
      return;
    }
    const ctrl = new AbortController();
    setLoading(true);
    fetchPropagation(
      {
        deLat,
        deLon,
        dxLat,
        dxLon,
        mode: propMode(mode),
        freqMhz: freqBucketMhz,
      },
      ctrl.signal,
    )
      .then((r) => setData(r))
      .catch(() => {
        /* aborted or network error — leave prior data */
      })
      .finally(() => setLoading(false));
    return () => ctrl.abort();
  }, [deLat, deLon, dxLat, dxLon, mode, freqBucketMhz]);

  return { data, loading };
}
