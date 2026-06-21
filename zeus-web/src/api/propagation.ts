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

// Point-to-point HF propagation (DE → DX) for the QRZ card. The Zeus backend
// proxies the embedded HamClock sidecar's ITU-R P.533-14 engine; see
// PropagationService.cs. When the sidecar isn't running, the endpoint returns
// { available: false, unavailable: <reason> } rather than an error.

export type PropagationBand = {
  band: string;
  freqMhz: number;
  /** 0..99 % circuit reliability for the current hour. */
  reliability: number;
  snr: string;
  /** GOOD | FAIR | POOR | CLOSED */
  status: string;
};

export type PropagationResult = {
  available: boolean;
  unavailable: string | null;
  model: string;
  sfi: number;
  ssn: number;
  kIndex: number;
  muf: number;
  luf: number;
  distanceKm: number;
  currentHourUtc: number;
  bands: PropagationBand[];
  /** Prediction for the radio's active band, when it could be matched. */
  currentBand: PropagationBand | null;
};

export type PropagationQuery = {
  deLat: number;
  deLon: number;
  dxLat: number;
  dxLon: number;
  mode?: string;
  power?: number;
  antenna?: string;
  /** Active operating frequency in MHz, used to pick the current band. */
  freqMhz?: number;
};

function num(v: unknown): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : 0;
}

function str(v: unknown): string {
  return typeof v === 'string' ? v : '';
}

function normalizeBand(raw: unknown): PropagationBand {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    band: str(r.band),
    freqMhz: num(r.freqMhz),
    reliability: num(r.reliability),
    snr: str(r.snr),
    status: str(r.status) || 'CLOSED',
  };
}

function normalize(raw: unknown): PropagationResult {
  const r = (raw ?? {}) as Record<string, unknown>;
  const bands = Array.isArray(r.bands) ? r.bands.map(normalizeBand) : [];
  return {
    available: Boolean(r.available),
    unavailable: typeof r.unavailable === 'string' ? r.unavailable : null,
    model: str(r.model),
    sfi: num(r.sfi),
    ssn: num(r.ssn),
    kIndex: num(r.kIndex),
    muf: num(r.muf),
    luf: num(r.luf),
    distanceKm: num(r.distanceKm),
    currentHourUtc: num(r.currentHourUtc),
    bands,
    currentBand: r.currentBand ? normalizeBand(r.currentBand) : null,
  };
}

export async function fetchPropagation(
  q: PropagationQuery,
  signal?: AbortSignal,
): Promise<PropagationResult> {
  const params = new URLSearchParams({
    deLat: String(q.deLat),
    deLon: String(q.deLon),
    dxLat: String(q.dxLat),
    dxLon: String(q.dxLon),
  });
  if (q.mode) params.set('mode', q.mode);
  if (q.power != null) params.set('power', String(q.power));
  if (q.antenna) params.set('antenna', q.antenna);
  if (q.freqMhz != null) params.set('freq', String(q.freqMhz));

  const res = await fetch(`/api/propagation?${params.toString()}`, { signal });
  if (!res.ok) {
    // The endpoint normally degrades with available:false rather than erroring;
    // treat a hard failure the same way so the card just hides the section.
    return normalize({ available: false, unavailable: `HTTP ${res.status}` });
  }
  return normalize((await res.json()) as unknown);
}
