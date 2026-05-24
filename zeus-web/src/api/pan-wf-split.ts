// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

// Hero-panel vertical split between the panadapter (top) and the
// waterfall (bottom). panPercent is the panadapter share, clamped
// 10..90; the waterfall takes the remainder. Persisted server-side in
// zeus-prefs.db, same pattern as /api/bottom-pin.

export const PAN_WF_DEFAULT_PERCENT = 50;
export const PAN_WF_MIN_PERCENT = 10;
export const PAN_WF_MAX_PERCENT = 90;

export type PanWfSplit = {
  panPercent: number;
};

type PanWfSplitDtoRaw = {
  panPercent?: number;
};

function clamp(v: number): number {
  if (!Number.isFinite(v)) return PAN_WF_DEFAULT_PERCENT;
  if (v < PAN_WF_MIN_PERCENT) return PAN_WF_MIN_PERCENT;
  if (v > PAN_WF_MAX_PERCENT) return PAN_WF_MAX_PERCENT;
  return v;
}

function normalize(raw: PanWfSplitDtoRaw): PanWfSplit {
  const v = typeof raw.panPercent === 'number' ? raw.panPercent : PAN_WF_DEFAULT_PERCENT;
  return { panPercent: clamp(v) };
}

export async function fetchPanWfSplit(signal?: AbortSignal): Promise<PanWfSplit> {
  const res = await fetch('/api/pan-wf-split', { signal });
  if (!res.ok) throw new Error(`GET /api/pan-wf-split → ${res.status}`);
  return normalize((await res.json()) as PanWfSplitDtoRaw);
}

export async function updatePanWfSplit(
  next: PanWfSplit,
  signal?: AbortSignal,
): Promise<PanWfSplit> {
  const res = await fetch('/api/pan-wf-split', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ panPercent: clamp(next.panPercent) }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/pan-wf-split → ${res.status}`);
  return normalize((await res.json()) as PanWfSplitDtoRaw);
}
