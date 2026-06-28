// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.
//
// REST client for the persisted FT8/FT4/WSPR workspace preferences (the curated
// WSJT-X/JTDX KEEP set that is pure behaviour/UI, not radio control). Backed by
// the server (zeus-prefs.db) so the operator's choices survive desktop restarts.
//
// PER-MODE: every read/write is scoped to a mode (FT8 | FT4 | WSPR) via the
// `?mode=` query param so FT8, FT4 and WSPR each remember their own config. The
// mode is omitted-defaults-to-FT8 on the backend, but the client always sends it
// explicitly. Operator IDENTITY (callsign/grid) is NOT here — it stays GLOBAL in
// /api/operator. Defaults mirror Zeus.Contracts.Ft8Settings exactly — surfacing
// existing engine knobs, never changing a default an operator already feels. TX
// still requires an explicit arm; none of these flags transmit on their own.

import { ApiError } from './client';

/** The three digital modes that each persist an independent settings row. */
export type DigitalMode = 'FT8' | 'FT4' | 'WSPR';

export const DIGITAL_MODES: readonly DigitalMode[] = ['FT8', 'FT4', 'WSPR'];

export function normalizeDigitalMode(raw: unknown): DigitalMode {
  const t = typeof raw === 'string' ? raw.toUpperCase() : '';
  return t === 'FT4' || t === 'WSPR' ? (t as DigitalMode) : 'FT8';
}

export type Ft8Settings = {
  // TX & auto-sequence
  autoSequence: boolean;
  callFirst: boolean;
  holdTxFreq: boolean;
  disableTxAfter73: boolean;
  /** 0 = 1st (even), 1 = 2nd (odd). */
  defaultTxSlot: number;
  defaultTxOffsetHz: number;
  // Advanced sequence flags (default off to match WSJT-X)
  rr73InsteadOfRrr: boolean;
  skipGrid: boolean;
  /** Caller max retries before giving up (0 = unlimited). */
  callerMaxRetries: number;
  // Macros
  cqMessage: string;
  cqDxMessage: string;
  freeTextMacro: string;
  // Decode
  /** Decode depth as passes, matching the engine scale (1 = Normal/floor,
   *  >1 = Deep/multi). Default 3 mirrors the engine. 1..4. */
  decodePasses: number;
  showOnlyCq: boolean;
  hideWorkedBefore: boolean;
  // Logging
  autoLog: boolean;
  promptBeforeLog: boolean;
  clearDxAfterLog: boolean;
  reportToComment: boolean;
  // Waterfall / display (per-mode). Persisted storage only — these mirror the
  // main-console display-settings fields but are scoped to the digital workspace
  // so FT8/FT4/WSPR each remember their own view and survive desktop restarts.
  // Pure display; none affect the air.
  wfDbMin: number;
  wfDbMax: number;
  /** Waterfall colormap id ("blue" | "inferno" | "viridis"). */
  palette: string;
  /** Resolution-bandwidth selector ("auto" or an Hz token). */
  rbw: string;
  /** Waterfall averaging/smoothing frames (0 = none). 0..10. */
  smoothing: number;
  /** Display zoom factor (1.0 = full span). 1..64. */
  zoom: number;
  /** Display span in Hz. 500..6000. */
  spanHz: number;
};

/** Defaults must match Zeus.Contracts.Ft8Settings defaults exactly. */
export const FT8_SETTINGS_DEFAULTS: Ft8Settings = {
  autoSequence: true,
  callFirst: false,
  holdTxFreq: false,
  disableTxAfter73: true,
  defaultTxSlot: 0,
  defaultTxOffsetHz: 1500,
  rr73InsteadOfRrr: true,
  skipGrid: false,
  callerMaxRetries: 0,
  cqMessage: 'CQ',
  cqDxMessage: 'CQ DX',
  freeTextMacro: '',
  decodePasses: 3,
  showOnlyCq: false,
  hideWorkedBefore: false,
  autoLog: true,
  promptBeforeLog: false,
  clearDxAfterLog: true,
  reportToComment: false,
  wfDbMin: -140,
  wfDbMax: -50,
  palette: 'blue',
  rbw: 'auto',
  smoothing: 0,
  zoom: 1.0,
  spanHz: 3000,
};

// Display bounds mirror Zeus.Contracts.Ft8Settings (belt-and-suspenders — the
// server normalizes on POST and returns sanitized values, but a hand-crafted or
// stale row read back must not collapse the colormap / blow out a slider).
export const WF_PALETTES: readonly string[] = ['blue', 'inferno', 'viridis'];
const DB_ABS_LIMIT = 200;
const MIN_DB_SPAN = 20;
export const WF_SMOOTHING_MIN = 0;
export const WF_SMOOTHING_MAX = 10;
export const WF_ZOOM_MIN = 1.0;
export const WF_ZOOM_MAX = 64.0;
export const WF_SPAN_MIN_HZ = 500;
export const WF_SPAN_MAX_HZ = 6000;

function clampNum(v: number, lo: number, hi: number, fallback: number): number {
  if (!Number.isFinite(v)) return fallback;
  return Math.max(lo, Math.min(hi, v));
}

function sanitizeDbRange(min: number, max: number): { min: number; max: number } {
  const d = FT8_SETTINGS_DEFAULTS;
  if (!Number.isFinite(min) || !Number.isFinite(max)) return { min: d.wfDbMin, max: d.wfDbMax };
  if (min < -DB_ABS_LIMIT || max > DB_ABS_LIMIT) return { min: d.wfDbMin, max: d.wfDbMax };
  if (max - min < MIN_DB_SPAN) return { min: d.wfDbMin, max: d.wfDbMax };
  return { min, max };
}

function normalize(raw: unknown): Ft8Settings {
  const r = (raw ?? {}) as Record<string, unknown>;
  const d = FT8_SETTINGS_DEFAULTS;
  const bool = (v: unknown, f: boolean) => (typeof v === 'boolean' ? v : f);
  const num = (v: unknown, f: number) => (typeof v === 'number' && Number.isFinite(v) ? v : f);
  const str = (v: unknown, f: string) => (typeof v === 'string' ? v : f);
  const dbRange = sanitizeDbRange(num(r.wfDbMin, d.wfDbMin), num(r.wfDbMax, d.wfDbMax));
  const palette = str(r.palette, d.palette).trim().toLowerCase();
  const rbw = str(r.rbw, d.rbw).trim();
  return {
    autoSequence: bool(r.autoSequence, d.autoSequence),
    callFirst: bool(r.callFirst, d.callFirst),
    holdTxFreq: bool(r.holdTxFreq, d.holdTxFreq),
    disableTxAfter73: bool(r.disableTxAfter73, d.disableTxAfter73),
    defaultTxSlot: num(r.defaultTxSlot, d.defaultTxSlot) === 0 ? 0 : 1,
    defaultTxOffsetHz: num(r.defaultTxOffsetHz, d.defaultTxOffsetHz),
    rr73InsteadOfRrr: bool(r.rr73InsteadOfRrr, d.rr73InsteadOfRrr),
    skipGrid: bool(r.skipGrid, d.skipGrid),
    callerMaxRetries: num(r.callerMaxRetries, d.callerMaxRetries),
    cqMessage: str(r.cqMessage, d.cqMessage),
    cqDxMessage: str(r.cqDxMessage, d.cqDxMessage),
    freeTextMacro: str(r.freeTextMacro, d.freeTextMacro),
    decodePasses: num(r.decodePasses, d.decodePasses),
    showOnlyCq: bool(r.showOnlyCq, d.showOnlyCq),
    hideWorkedBefore: bool(r.hideWorkedBefore, d.hideWorkedBefore),
    autoLog: bool(r.autoLog, d.autoLog),
    promptBeforeLog: bool(r.promptBeforeLog, d.promptBeforeLog),
    clearDxAfterLog: bool(r.clearDxAfterLog, d.clearDxAfterLog),
    reportToComment: bool(r.reportToComment, d.reportToComment),
    wfDbMin: dbRange.min,
    wfDbMax: dbRange.max,
    palette: WF_PALETTES.includes(palette) ? palette : d.palette,
    rbw: rbw.length === 0 ? d.rbw : rbw.slice(0, 16),
    smoothing: Math.round(clampNum(num(r.smoothing, d.smoothing), WF_SMOOTHING_MIN, WF_SMOOTHING_MAX, d.smoothing)),
    zoom: clampNum(num(r.zoom, d.zoom), WF_ZOOM_MIN, WF_ZOOM_MAX, d.zoom),
    spanHz: Math.round(clampNum(num(r.spanHz, d.spanHz), WF_SPAN_MIN_HZ, WF_SPAN_MAX_HZ, d.spanHz)),
  };
}

async function jsonFetch(input: RequestInfo, init: RequestInit | undefined): Promise<Ft8Settings> {
  const res = await fetch(input, init);
  if (!res.ok) throw new ApiError(res.status, `${res.status} ${res.statusText}`);
  return normalize((await res.json()) as unknown);
}

function settingsPath(mode: DigitalMode): string {
  return `/api/ft8/settings?mode=${encodeURIComponent(mode)}`;
}

export function getFt8Settings(mode: DigitalMode, signal?: AbortSignal): Promise<Ft8Settings> {
  return jsonFetch(settingsPath(mode), { signal });
}

export function postFt8Settings(
  mode: DigitalMode,
  settings: Ft8Settings,
  signal?: AbortSignal,
): Promise<Ft8Settings> {
  return jsonFetch(settingsPath(mode), {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(settings),
    signal,
  });
}
