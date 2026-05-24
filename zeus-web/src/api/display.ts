// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

export type DisplaySettings = {
  mode: 'basic' | 'beam-map' | 'image';
  fit: 'fit' | 'fill' | 'stretch';
  hasImage: boolean;
  imageMime: string | null;
  rxTraceColor: string;
  // Panadapter and waterfall dB window bounds. null means the server has
  // never stored a value; the frontend falls back to its built-in defaults
  // (FIXED_DB_MIN / TX_FIXED_DB_MIN) and pushes the current value up on
  // next interaction. Non-null values are used as-is (server wins over
  // localStorage, surviving the Photino per-launch port shuffle).
  dbMin: number | null;
  dbMax: number | null;
  txDbMin: number | null;
  txDbMax: number | null;
  wfDbMin: number | null;
  wfDbMax: number | null;
  wfTxDbMin: number | null;
  wfTxDbMax: number | null;
};

// Matches backend DisplaySettingsStore.DefaultRxTraceColor.
const DEFAULT_RX_TRACE_COLOR = '#FFA028';

type DisplaySettingsDtoRaw = {
  mode?: string;
  fit?: string;
  hasImage?: boolean;
  imageMime?: string | null;
  rxTraceColor?: string | null;
  dbMin?: number | null;
  dbMax?: number | null;
  txDbMin?: number | null;
  txDbMax?: number | null;
  wfDbMin?: number | null;
  wfDbMax?: number | null;
  wfTxDbMin?: number | null;
  wfTxDbMax?: number | null;
};

function normalizeRxTraceColor(raw: string | null | undefined): string {
  if (typeof raw !== 'string') return DEFAULT_RX_TRACE_COLOR;
  return /^#[0-9A-Fa-f]{6}$/.test(raw) ? raw.toUpperCase() : DEFAULT_RX_TRACE_COLOR;
}

function normalizeDbValue(raw: number | null | undefined): number | null {
  return typeof raw === 'number' && Number.isFinite(raw) ? raw : null;
}

function normalize(raw: DisplaySettingsDtoRaw): DisplaySettings {
  const mode =
    raw.mode === 'beam-map' || raw.mode === 'image' || raw.mode === 'basic'
      ? raw.mode
      : 'basic';
  const fit =
    raw.fit === 'fit' || raw.fit === 'fill' || raw.fit === 'stretch'
      ? raw.fit
      : 'fill';
  return {
    mode,
    fit,
    hasImage: !!raw.hasImage,
    imageMime: raw.imageMime ?? null,
    rxTraceColor: normalizeRxTraceColor(raw.rxTraceColor),
    dbMin: normalizeDbValue(raw.dbMin),
    dbMax: normalizeDbValue(raw.dbMax),
    txDbMin: normalizeDbValue(raw.txDbMin),
    txDbMax: normalizeDbValue(raw.txDbMax),
    wfDbMin: normalizeDbValue(raw.wfDbMin),
    wfDbMax: normalizeDbValue(raw.wfDbMax),
    wfTxDbMin: normalizeDbValue(raw.wfTxDbMin),
    wfTxDbMax: normalizeDbValue(raw.wfTxDbMax),
  };
}

export async function fetchDisplaySettings(signal?: AbortSignal): Promise<DisplaySettings> {
  const res = await fetch('/api/display-settings', { signal });
  if (!res.ok) throw new Error(`GET /api/display-settings → ${res.status}`);
  return normalize((await res.json()) as DisplaySettingsDtoRaw);
}

export async function updateDisplaySettings(
  mode: DisplaySettings['mode'],
  fit: DisplaySettings['fit'],
  rxTraceColor: string,
  dbMin?: number | null,
  dbMax?: number | null,
  txDbMin?: number | null,
  txDbMax?: number | null,
  wfDbMin?: number | null,
  wfDbMax?: number | null,
  wfTxDbMin?: number | null,
  wfTxDbMax?: number | null,
  signal?: AbortSignal,
): Promise<DisplaySettings> {
  const res = await fetch('/api/display-settings', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      mode,
      fit,
      rxTraceColor,
      dbMin,
      dbMax,
      txDbMin,
      txDbMax,
      wfDbMin,
      wfDbMax,
      wfTxDbMin,
      wfTxDbMax,
    }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/display-settings → ${res.status}`);
  return normalize((await res.json()) as DisplaySettingsDtoRaw);
}

export async function uploadDisplayImage(
  blob: Blob,
  signal?: AbortSignal,
): Promise<DisplaySettings> {
  const fd = new FormData();
  fd.append('file', blob, 'background');
  const res = await fetch('/api/display-settings/image', {
    method: 'PUT',
    body: fd,
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/display-settings/image → ${res.status}`);
  return normalize((await res.json()) as DisplaySettingsDtoRaw);
}

export async function deleteDisplayImage(signal?: AbortSignal): Promise<DisplaySettings> {
  const res = await fetch('/api/display-settings/image', { method: 'DELETE', signal });
  if (!res.ok) throw new Error(`DELETE /api/display-settings/image → ${res.status}`);
  return normalize((await res.json()) as DisplaySettingsDtoRaw);
}

// Cache-busted URL for the currently-stored image. Pass a version stamp that
// increments on each upload so the browser pulls fresh bytes after a change.
export function displayImageUrl(version: number): string {
  return `/api/display-settings/image?v=${version}`;
}
