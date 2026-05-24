// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

// Display-side meter calibration knobs (GitHub #426). Surfaces:
//
//   * S-meter dB offset trim (signed ±20 dB)
//   * TX forward-power meter max-displayed-watts override
//     (0 = no override, use radio's rated MaxWatts as full scale)
//
// Persisted server-side in zeus-prefs.db.

export const SMETER_OFFSET_MIN_DB = -20;
export const SMETER_OFFSET_MAX_DB = 20;
export const SMETER_OFFSET_DEFAULT_DB = 0;

export const MAX_DISPLAYED_WATTS_MIN = 1;
export const MAX_DISPLAYED_WATTS_MAX = 1000;
// 0 = "no override". Matches the server's MeterDisplaySettingsDto
// shape; the meter component falls back to the radio's MaxWatts.
export const MAX_DISPLAYED_WATTS_DEFAULT = 0;

export type MeterDisplaySettings = {
  sMeterOffsetDb: number;
  maxDisplayedWatts: number;
};

type RawDto = {
  sMeterOffsetDb?: number;
  maxDisplayedWatts?: number;
};

function clampOffset(v: number): number {
  if (!Number.isFinite(v)) return SMETER_OFFSET_DEFAULT_DB;
  if (v < SMETER_OFFSET_MIN_DB) return SMETER_OFFSET_MIN_DB;
  if (v > SMETER_OFFSET_MAX_DB) return SMETER_OFFSET_MAX_DB;
  return v;
}

function clampMaxWatts(v: number): number {
  if (!Number.isFinite(v)) return MAX_DISPLAYED_WATTS_DEFAULT;
  if (v <= 0) return MAX_DISPLAYED_WATTS_DEFAULT;
  if (v < MAX_DISPLAYED_WATTS_MIN) return MAX_DISPLAYED_WATTS_MIN;
  if (v > MAX_DISPLAYED_WATTS_MAX) return MAX_DISPLAYED_WATTS_MAX;
  return v;
}

function normalize(raw: RawDto): MeterDisplaySettings {
  return {
    sMeterOffsetDb: clampOffset(
      typeof raw.sMeterOffsetDb === 'number' ? raw.sMeterOffsetDb : SMETER_OFFSET_DEFAULT_DB,
    ),
    maxDisplayedWatts: clampMaxWatts(
      typeof raw.maxDisplayedWatts === 'number'
        ? raw.maxDisplayedWatts
        : MAX_DISPLAYED_WATTS_DEFAULT,
    ),
  };
}

export async function fetchMeterDisplaySettings(
  signal?: AbortSignal,
): Promise<MeterDisplaySettings> {
  const res = await fetch('/api/meters/display-settings', { signal });
  if (!res.ok) throw new Error(`GET /api/meters/display-settings → ${res.status}`);
  return normalize((await res.json()) as RawDto);
}

export async function updateSMeterOffsetDb(
  offsetDb: number,
  signal?: AbortSignal,
): Promise<MeterDisplaySettings> {
  const res = await fetch('/api/meters/smeter-offset-db', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ offsetDb: clampOffset(offsetDb) }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/meters/smeter-offset-db → ${res.status}`);
  return normalize((await res.json()) as RawDto);
}

export async function updateMaxDisplayedWatts(
  maxWatts: number,
  signal?: AbortSignal,
): Promise<MeterDisplaySettings> {
  const res = await fetch('/api/meters/max-displayed-watts', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ maxWatts: clampMaxWatts(maxWatts) }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/meters/max-displayed-watts → ${res.status}`);
  return normalize((await res.json()) as RawDto);
}
