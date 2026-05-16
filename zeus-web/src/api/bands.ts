// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

export type BandAllocation = 'Amateur' | 'SWL' | 'Broadcast' | 'Reserved' | 'Unknown';
export type ModeRestriction = 'Any' | 'CwOnly' | 'PhoneOnly' | 'DigitalOnly' | 'CwAndDigital';

export type BandRegion = {
  id: string;
  displayName: string;
  shortCode: string;
  parentId: string | null;
};

export type BandSegment = {
  regionId: string;
  lowHz: number;
  highHz: number;
  label: string;
  allocation: BandAllocation;
  modeRestriction: ModeRestriction;
  maxPowerW: number | null;
  notes: string | null;
};

export type BandPlanDto = {
  regionId: string;
  segments: BandSegment[];
};

export type BandCurrentDto = {
  regionId: string;
  region: BandRegion;
  segments: BandSegment[];
  txGuardIgnore: boolean;
};

export type RxMode = 'LSB' | 'USB' | 'CWL' | 'CWU' | 'AM' | 'FM' | 'SAM' | 'DSB' | 'DIGL' | 'DIGU';

export async function fetchRegions(signal?: AbortSignal): Promise<BandRegion[]> {
  const r = await fetch('/api/bands/regions', { signal });
  if (!r.ok) throw new Error(`GET /api/bands/regions: ${r.status}`);
  return r.json() as Promise<BandRegion[]>;
}

export async function fetchPlan(regionId: string, signal?: AbortSignal): Promise<BandPlanDto> {
  const r = await fetch(`/api/bands/plan?region=${encodeURIComponent(regionId)}`, { signal });
  if (!r.ok) throw new Error(`GET /api/bands/plan: ${r.status}`);
  return r.json() as Promise<BandPlanDto>;
}

export async function fetchCurrent(signal?: AbortSignal): Promise<BandCurrentDto> {
  const r = await fetch('/api/bands/current', { signal });
  if (!r.ok) throw new Error(`GET /api/bands/current: ${r.status}`);
  return r.json() as Promise<BandCurrentDto>;
}

export async function setCurrentRegion(regionId: string): Promise<void> {
  const r = await fetch('/api/bands/current', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ regionId }),
  });
  if (!r.ok) throw new Error(`POST /api/bands/current: ${r.status}`);
}

export async function savePlan(regionId: string, segments: BandSegment[]): Promise<void> {
  const r = await fetch('/api/bands/plan', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ regionId, segments }),
  });
  if (!r.ok) {
    const body = await r.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? `PUT /api/bands/plan: ${r.status}`);
  }
}

export async function resetPlan(regionId: string): Promise<void> {
  const r = await fetch(`/api/bands/plan/${encodeURIComponent(regionId)}`, { method: 'DELETE' });
  if (!r.ok) throw new Error(`DELETE /api/bands/plan: ${r.status}`);
}

export async function setTxGuardIgnore(ignore: boolean): Promise<void> {
  const r = await fetch('/api/bands/guard', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ignore }),
  });
  if (!r.ok) throw new Error(`POST /api/bands/guard: ${r.status}`);
}

/** Binary search the resolved plan for the segment containing freqHz. */
export function binarySearchSegment(segments: BandSegment[], freqHz: number): BandSegment | null {
  let lo = 0, hi = segments.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    const s = segments[mid];
    if (!s) break;
    if (freqHz < s.lowHz) hi = mid - 1;
    else if (freqHz > s.highHz) lo = mid + 1;
    else return s;
  }
  return null;
}

export function modeMatchesRestriction(mode: RxMode, restriction: ModeRestriction): boolean {
  switch (restriction) {
    case 'Any': return true;
    case 'CwOnly': return mode === 'CWU' || mode === 'CWL';
    case 'PhoneOnly': return ['USB', 'LSB', 'AM', 'SAM', 'DSB', 'FM'].includes(mode);
    case 'DigitalOnly': return mode === 'DIGL' || mode === 'DIGU';
    case 'CwAndDigital': return ['CWU', 'CWL', 'DIGL', 'DIGU'].includes(mode);
    default: return false;
  }
}

export function inBand(segments: BandSegment[], freqHz: number, mode: RxMode): boolean {
  const seg = binarySearchSegment(segments, freqHz);
  if (!seg) return false;
  if (seg.allocation !== 'Amateur') return false;
  return modeMatchesRestriction(mode, seg.modeRestriction);
}
