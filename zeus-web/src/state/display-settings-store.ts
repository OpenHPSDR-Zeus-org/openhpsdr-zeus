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
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import type { ColormapId } from '../gl/colormap';
import {
  deleteDisplayImage,
  displayImageUrl,
  fetchDisplaySettings,
  updateDisplaySettings,
  uploadDisplayImage,
} from '../api/display';

// Fixed defaults used when autoRange is off and no user-saved range is
// present. -140..-50 dBFS sits the noise floor where operators expect to
// read it (bottom of the left-hand scale near ~140 dB), matching Thetis's
// out-of-box panadapter feel. A user's drag-shift is persisted to
// localStorage and takes over on reload — see `shiftDbRange`.
export const FIXED_DB_MIN = -140;
export const FIXED_DB_MAX = -50;

// TX panadapter defaults — kept separate from RX so the user can drag the
// scale while keyed without disturbing their RX noise-floor view. Matches
// Thetis's `TXSpectrumGridMin = -80` / `TXSpectrumGridMax = 20` (Display.cs:
// 1881-1897). Speech peaks land inside this window; a user who wants to
// hide silence-time floor pumping raises TX_DB_MIN via the drag gesture.
export const TX_FIXED_DB_MIN = -80;
export const TX_FIXED_DB_MAX = 20;

// TX display analyzer params (live TX waterfall). Display-only — they shape the
// transmitted-signal panadapter/waterfall, never the air. Defaults mirror the
// backend WdspDspEngine constants. Window ints are WDSP win_type values
// (analyzer.c new_window): 0 Rectangular, 1 Blackman-Harris, 2 Hann (default),
// 3 Flat-top, 4 Hamming, 5 Kaiser, 6 Blackman-Harris 7-term.
export const TX_DISPLAY_FFT_SIZES = [2048, 4096, 8192, 16384, 32768, 65536] as const;
export const TX_DISPLAY_WINDOWS: readonly { value: number; label: string }[] = [
  { value: 2, label: 'Hann' },
  { value: 1, label: 'Blackman-Harris' },
  { value: 6, label: 'Blackman-Harris 7' },
  { value: 3, label: 'Flat-top' },
  { value: 4, label: 'Hamming' },
  { value: 5, label: 'Kaiser' },
  { value: 0, label: 'Rectangular' },
];
export const DEFAULT_TX_DISPLAY_CAL_OFFSET_DB = 0;
export const DEFAULT_TX_DISPLAY_FFT_SIZE = 16384;
export const DEFAULT_TX_DISPLAY_WINDOW = 2;
export const DEFAULT_TX_DISPLAY_AVG_TAU_MS = 175;
export const TX_DISPLAY_CAL_OFFSET_ABS_DB = 60;
export const TX_DISPLAY_AVG_TAU_MIN_MS = 0;
export const TX_DISPLAY_AVG_TAU_MAX_MS = 2000;

const STORAGE_KEY = 'zeus.display.dbRange';
const TX_STORAGE_KEY = 'zeus.display.txDbRange';
const WF_STORAGE_KEY = 'zeus.display.wfDbRange';
const WF_TX_STORAGE_KEY = 'zeus.display.wfTxDbRange';
const WF_SCROLL_SPEED_STORAGE_KEY = 'zeus.display.wfScrollSpeed';
const BAND_OVERLAY_STORAGE_KEY = 'zeus.display.bandOverlay';
const BAND_EDGE_ALERT_STORAGE_KEY = 'zeus.display.bandEdgeAlert';

// Legacy localStorage keys — pre-server-side storage. Read once on first
// load to migrate the operator's existing image / colour up to the backend,
// then removed. New code should never read or write these.
const LEGACY_PAN_BG_KEY = 'zeus.display.panBackground';
const LEGACY_BG_IMAGE_KEY = 'zeus.display.backgroundImage';
const LEGACY_BG_FIT_KEY = 'zeus.display.backgroundImageFit';
const LEGACY_RX_TRACE_COLOR_KEY = 'zeus.display.rxTraceColor';

// Default RX panadapter trace colour — warm amber, matching the original
// hardcoded constant in gl/panadapter.ts and the backend's
// DisplaySettingsStore.DefaultRxTraceColor.
export const DEFAULT_RX_TRACE_COLOR = '#FFA028';

function isHexColor(v: unknown): v is string {
  return typeof v === 'string' && /^#[0-9A-Fa-f]{6}$/.test(v);
}

// Panadapter background mode. 'basic' = no overlay (current QRZ-off
// look). 'beam-map' = world-map overlay with terminator lines and beam
// chrome (current QRZ-on look). 'image' = user-supplied still image
// behind a transparent panadapter / waterfall.
export type PanBackgroundMode = 'basic' | 'beam-map' | 'image';

// CSS background-size mapping for the image background.
// 'fit' → contain (entire image visible, may letterbox)
// 'fill' → cover (fills the panel, may crop)
// 'stretch' → 100% 100% (distorts to fit exactly)
export type BackgroundImageFit = 'fit' | 'fill' | 'stretch';

export const WATERFALL_SCROLL_SPEED_MIN = 0.25;
export const WATERFALL_SCROLL_SPEED_MAX = 2.5;
export const WATERFALL_SCROLL_SPEED_STEP = 0.05;
export const DEFAULT_WF_SCROLL_SPEED = 1;

function normalizeWaterfallScrollSpeed(raw: unknown): number {
  const n = typeof raw === 'number' ? raw : Number(raw);
  if (!Number.isFinite(n)) return DEFAULT_WF_SCROLL_SPEED;
  const clamped = Math.max(WATERFALL_SCROLL_SPEED_MIN, Math.min(WATERFALL_SCROLL_SPEED_MAX, n));
  return Math.round(clamped / WATERFALL_SCROLL_SPEED_STEP) * WATERFALL_SCROLL_SPEED_STEP;
}

function readSavedWaterfallScrollSpeed(): number {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_WF_SCROLL_SPEED;
    return normalizeWaterfallScrollSpeed(localStorage.getItem(WF_SCROLL_SPEED_STORAGE_KEY));
  } catch {
    return DEFAULT_WF_SCROLL_SPEED;
  }
}

function writeSavedWaterfallScrollSpeed(value: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(WF_SCROLL_SPEED_STORAGE_KEY, String(value));
  } catch {
    // quota exceeded / private mode — accept silently.
  }
}

function readBoolFlag(key: string, defaultValue: boolean): boolean {
  try {
    if (typeof localStorage === 'undefined') return defaultValue;
    const raw = localStorage.getItem(key);
    if (raw === null) return defaultValue;
    return raw === '1' || raw === 'true';
  } catch {
    return defaultValue;
  }
}

function writeBoolFlag(key: string, value: boolean): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(key, value ? '1' : '0');
  } catch {
    // quota exceeded / private mode — accept silently.
  }
}

function readLegacyRxTraceColor(): string | null {
  try {
    if (typeof localStorage === 'undefined') return null;
    const raw = localStorage.getItem(LEGACY_RX_TRACE_COLOR_KEY);
    if (!isHexColor(raw)) return null;
    const norm = raw.toUpperCase();
    return norm === DEFAULT_RX_TRACE_COLOR ? null : norm;
  } catch {
    return null;
  }
}

function readSavedRange(): { dbMin: number; dbMax: number } {
  try {
    if (typeof localStorage === 'undefined') return { dbMin: FIXED_DB_MIN, dbMax: FIXED_DB_MAX };
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return { dbMin: FIXED_DB_MIN, dbMax: FIXED_DB_MAX };
    const parsed = JSON.parse(raw);
    const dbMin = typeof parsed?.dbMin === 'number' ? parsed.dbMin : FIXED_DB_MIN;
    const dbMax = typeof parsed?.dbMax === 'number' ? parsed.dbMax : FIXED_DB_MAX;
    if (!(dbMin < dbMax) || !Number.isFinite(dbMin) || !Number.isFinite(dbMax)) {
      return { dbMin: FIXED_DB_MIN, dbMax: FIXED_DB_MAX };
    }
    return { dbMin, dbMax };
  } catch {
    return { dbMin: FIXED_DB_MIN, dbMax: FIXED_DB_MAX };
  }
}

function writeSavedRange(dbMin: number, dbMax: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ dbMin, dbMax }));
  } catch {
    // quota exceeded / private mode — accept silently, the in-memory state
    // is still the source of truth for this session.
  }
}

function readSavedTxRange(): { txDbMin: number; txDbMax: number } {
  try {
    if (typeof localStorage === 'undefined') return { txDbMin: TX_FIXED_DB_MIN, txDbMax: TX_FIXED_DB_MAX };
    const raw = localStorage.getItem(TX_STORAGE_KEY);
    if (!raw) return { txDbMin: TX_FIXED_DB_MIN, txDbMax: TX_FIXED_DB_MAX };
    const parsed = JSON.parse(raw);
    const txDbMin = typeof parsed?.txDbMin === 'number' ? parsed.txDbMin : TX_FIXED_DB_MIN;
    const txDbMax = typeof parsed?.txDbMax === 'number' ? parsed.txDbMax : TX_FIXED_DB_MAX;
    if (!(txDbMin < txDbMax) || !Number.isFinite(txDbMin) || !Number.isFinite(txDbMax)) {
      return { txDbMin: TX_FIXED_DB_MIN, txDbMax: TX_FIXED_DB_MAX };
    }
    return { txDbMin, txDbMax };
  } catch {
    return { txDbMin: TX_FIXED_DB_MIN, txDbMax: TX_FIXED_DB_MAX };
  }
}

function writeSavedTxRange(txDbMin: number, txDbMax: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(TX_STORAGE_KEY, JSON.stringify({ txDbMin, txDbMax }));
  } catch {
    // quota exceeded / private mode — accept silently.
  }
}

function readSavedWfRange(): { wfDbMin: number; wfDbMax: number } {
  try {
    if (typeof localStorage === 'undefined') return { wfDbMin: FIXED_DB_MIN, wfDbMax: FIXED_DB_MAX };
    const raw = localStorage.getItem(WF_STORAGE_KEY);
    if (!raw) return { wfDbMin: FIXED_DB_MIN, wfDbMax: FIXED_DB_MAX };
    const parsed = JSON.parse(raw);
    const wfDbMin = typeof parsed?.wfDbMin === 'number' ? parsed.wfDbMin : FIXED_DB_MIN;
    const wfDbMax = typeof parsed?.wfDbMax === 'number' ? parsed.wfDbMax : FIXED_DB_MAX;
    if (!(wfDbMin < wfDbMax) || !Number.isFinite(wfDbMin) || !Number.isFinite(wfDbMax)) {
      return { wfDbMin: FIXED_DB_MIN, wfDbMax: FIXED_DB_MAX };
    }
    return { wfDbMin, wfDbMax };
  } catch {
    return { wfDbMin: FIXED_DB_MIN, wfDbMax: FIXED_DB_MAX };
  }
}

function writeSavedWfRange(wfDbMin: number, wfDbMax: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(WF_STORAGE_KEY, JSON.stringify({ wfDbMin, wfDbMax }));
  } catch {
    // quota exceeded / private mode — accept silently.
  }
}

function readSavedWfTxRange(): { wfTxDbMin: number; wfTxDbMax: number } {
  try {
    if (typeof localStorage === 'undefined') return { wfTxDbMin: TX_FIXED_DB_MIN, wfTxDbMax: TX_FIXED_DB_MAX };
    const raw = localStorage.getItem(WF_TX_STORAGE_KEY);
    if (!raw) return { wfTxDbMin: TX_FIXED_DB_MIN, wfTxDbMax: TX_FIXED_DB_MAX };
    const parsed = JSON.parse(raw);
    const wfTxDbMin = typeof parsed?.wfTxDbMin === 'number' ? parsed.wfTxDbMin : TX_FIXED_DB_MIN;
    const wfTxDbMax = typeof parsed?.wfTxDbMax === 'number' ? parsed.wfTxDbMax : TX_FIXED_DB_MAX;
    if (!(wfTxDbMin < wfTxDbMax) || !Number.isFinite(wfTxDbMin) || !Number.isFinite(wfTxDbMax)) {
      return { wfTxDbMin: TX_FIXED_DB_MIN, wfTxDbMax: TX_FIXED_DB_MAX };
    }
    return { wfTxDbMin, wfTxDbMax };
  } catch {
    return { wfTxDbMin: TX_FIXED_DB_MIN, wfTxDbMax: TX_FIXED_DB_MAX };
  }
}

function writeSavedWfTxRange(wfTxDbMin: number, wfTxDbMax: number): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(WF_TX_STORAGE_KEY, JSON.stringify({ wfTxDbMin, wfTxDbMax }));
  } catch {
    // quota exceeded / private mode — accept silently.
  }
}

// Debounced server save for dB range changes. The drag gesture fires many
// small shiftDbRange calls per second; we batch them into a single PUT after
// the operator lifts their finger (1 s quiet period), the same pattern used
// by layout-store.ts for tile position saves.
let dbRangeTimer: ReturnType<typeof setTimeout> | null = null;

function scheduleDbRangeSave(): void {
  if (dbRangeTimer) clearTimeout(dbRangeTimer);
  dbRangeTimer = setTimeout(() => {
    const s = useDisplaySettingsStore.getState();
    void updateDisplaySettings(
      s.panBackground,
      s.backgroundImageFit,
      s.rxTraceColor,
      s.dbMin,
      s.dbMax,
      s.txDbMin,
      s.txDbMax,
      s.wfDbMin,
      s.wfDbMax,
      s.wfTxDbMin,
      s.wfTxDbMax,
    );
  }, 1000);
}

// Debounced server save for the TX display analyzer params. Slider drags
// (cal offset, smoothing) fire many updates; batch into one PUT. Shorter quiet
// period than the dB-range save since these are discrete control tweaks, not a
// continuous finger-drag on the panadapter scale.
let txDisplayTimer: ReturnType<typeof setTimeout> | null = null;

function scheduleTxDisplaySave(): void {
  if (txDisplayTimer) clearTimeout(txDisplayTimer);
  txDisplayTimer = setTimeout(() => {
    const s = useDisplaySettingsStore.getState();
    void updateDisplaySettings(
      s.panBackground,
      s.backgroundImageFit,
      s.rxTraceColor,
      s.dbMin,
      s.dbMax,
      s.txDbMin,
      s.txDbMax,
      s.wfDbMin,
      s.wfDbMax,
      s.wfTxDbMin,
      s.wfTxDbMax,
      {
        calOffsetDb: s.txDisplayCalOffsetDb,
        fftSize: s.txDisplayFftSize,
        window: s.txDisplayWindow,
        avgTauMs: s.txDisplayAvgTauMs,
      },
    );
  }, 500);
}

// Exponential smoothing constant for the auto-range tracker. 0.1 trades
// flicker resistance for responsiveness — band-change artifacts fade over
// ~30 frames at 30 Hz (~1 s).
const SMOOTHING = 0.1;

// Give the auto-tracked range a little headroom so the tops of strong
// signals don't clip to the brightest colour and the noise-floor doesn't
// sit right at the darkest index.
const AUTO_FLOOR_MARGIN_DB = 8;
const AUTO_CEIL_MARGIN_DB = 6;

// TX auto-range margins/percentiles. Unlike RX (sparse signals against a wide
// noise floor), the TX signal is a continuous block occupying only a fraction
// of the full-span display, so p5/p95 would both land in the floor. We fit on
// a low floor percentile and a high peak percentile instead, and converge
// faster (0.3) so the window settles within a few frames of key-down. The
// peak percentile (not the absolute max) keeps a single hot bin from throwing
// the ceiling.
const TX_AUTO_FLOOR_MARGIN_DB = 6;
const TX_AUTO_CEIL_MARGIN_DB = 8;
const TX_AUTO_SMOOTHING = 0.3;
const TX_AUTO_FLOOR_PCT = 0.1;
const TX_AUTO_PEAK_PCT = 0.995;

// Guard against degenerate ranges (e.g. silent input producing p5==p95).
const MIN_SPAN_DB = 20;

/**
 * Whether the TX display auto-fit should engage for the current TX state.
 *
 * Auto-range is only meaningful for a wide modulated signal (voice MOX/PTT),
 * where the high-percentile "peak" lands on real signal. A TUNE carrier (a
 * few-bin spike) or a two-tone test (twin spikes) defeats the percentile fit —
 * the "peak" falls in the noise floor and the window collapses onto it,
 * rendering the clean carrier as "grass". Those transmit types keep the fixed
 * window. Two-tone arms independently of MOX, so it is excluded explicitly.
 */
export function shouldTxAutoRange(
  tx: { moxOn: boolean; tunOn: boolean; twoToneOn: boolean },
  txAutoRange: boolean,
): boolean {
  return txAutoRange && tx.moxOn && !tx.tunOn && !tx.twoToneOn;
}

export type DisplaySettingsState = {
  autoRange: boolean;
  // Panadapter dB window. Driven by the DbScale gesture (manual) and/or
  // the AUTO toggle (EMA-tracked).
  dbMin: number;
  dbMax: number;
  // Waterfall dB window. Independent of the panadapter so the operator
  // can darken/brighten the waterfall colour mapping without disturbing
  // the panadapter's noise-floor view. Driven by its own DbScale slider.
  wfDbMin: number;
  wfDbMax: number;
  // Separate dB range for TX waterfall (rendered during MOX/TUN). Mirrors
  // the TX panadapter pair so the operator can darken/brighten the keyed
  // waterfall window independently of their RX waterfall view.
  wfTxDbMin: number;
  wfTxDbMax: number;
  // Separate dB range for TX panadapter (rendered during MOX/TUN). Thetis
  // parity — see TX_FIXED_DB_MIN/MAX constants.
  txDbMin: number;
  txDbMax: number;
  // TX display analyzer params (live TX waterfall). Display-only — they shape
  // the transmitted-signal panadapter/waterfall FFT + level, never the air.
  // calOffsetDb shifts the trace/waterfall level (Thetis TXDisplayCalOffset);
  // fftSize/window are the WDSP analyzer config; avgTauMs is the visual
  // smoothing time-constant. Persisted server-side; applied to the engine live.
  txDisplayCalOffsetDb: number;
  txDisplayFftSize: number;
  txDisplayWindow: number;
  txDisplayAvgTauMs: number;
  // TX display auto-range. While keyed, fit the TX panadapter + waterfall
  // windows to the live transmitted signal so the trace is never slammed to
  // full-scale (the WDSP TX analyzer reads far hotter than the RX one). On by
  // default; a manual TX window edit or scale-drag switches it off.
  txAutoRange: boolean;
  colormap: ColormapId;
  waterfallScrollSpeed: number;
  // Panadapter background overlay mode + (optional) user image. See the
  // PanBackgroundMode and BackgroundImageFit types above. Persisted on the
  // backend (zeus-prefs.db) so a single setting follows the operator across
  // every browser pointed at the Zeus instance — phones, tablets, multiple
  // desktops. backgroundImage is a server URL with a cache-busting query
  // string, not a data:URL. setBackgroundImage returns false on upload
  // failure (network or server-side rejection).
  panBackground: PanBackgroundMode;
  backgroundImage: string | null;
  backgroundImageFit: BackgroundImageFit;
  // RX panadapter trace colour as #RRGGBB. Drives both the sharp trace line
  // and the fill underneath in gl/panadapter.ts (kept in lockstep). Persisted
  // server-side alongside panBackground / backgroundImage so it survives the
  // Photino-desktop port shuffle (per-launch random loopback port = fresh
  // localStorage origin = orphaned setting).
  rxTraceColor: string;
  // Issue #846: licence-class band overlay on the panadapter — shading per
  // band-plan segment, with sub-band labels. Local-only (no server round-trip);
  // the active plan itself is fetched separately via the bandPlan store.
  showBandOverlay: boolean;
  // Issue #846: short audible tone when the VFO crosses from in-licence into
  // out-of-licence (or into a band gap). Mirrors the alarm Icoms ship with.
  bandEdgeAlertEnabled: boolean;
  setShowBandOverlay: (v: boolean) => void;
  setBandEdgeAlertEnabled: (v: boolean) => void;
  setPanBackground: (v: PanBackgroundMode) => Promise<void>;
  setBackgroundImage: (dataUrl: string | null) => Promise<boolean>;
  setBackgroundImageFit: (v: BackgroundImageFit) => Promise<void>;
  setRxTraceColor: (v: string) => Promise<void>;
  setAutoRange: (v: boolean) => void;
  setColormap: (id: ColormapId) => void;
  setWaterfallScrollSpeed: (value: number) => void;
  setDbRange: (dbMin: number, dbMax: number) => void;
  setTxDbRange: (txDbMin: number, txDbMax: number) => void;
  setWfDbRange: (wfDbMin: number, wfDbMax: number) => void;
  setWfTxDbRange: (wfTxDbMin: number, wfTxDbMax: number) => void;
  // Update one or more TX display analyzer params; persisted + pushed to the
  // engine (debounced). Omitted fields are left unchanged.
  setTxDisplayParams: (p: {
    calOffsetDb?: number;
    fftSize?: number;
    window?: number;
    avgTauMs?: number;
  }) => void;
  resetTxDisplayParams: () => void;
  // Toggle TX auto-range. Turning it off restores the operator's saved TX
  // windows (mirrors setAutoRange for RX).
  setTxAutoRange: (v: boolean) => void;
  // Fit the TX windows to a frame of live TX pixels (no-op when off / empty).
  // Driven from the waterfall ingest while keyed.
  updateTxAutoRange: (pixels: Float32Array) => void;
  // Snap the TX windows back to the operator's saved (or fixed -80..+20) range.
  // Used when auto-range disengages — turned off, or a non-voice TX type (TUNE /
  // two-tone) where the auto-fit would collapse onto the noise floor.
  restoreSavedTxWindows: () => void;
  resetDbRanges: () => void;
  updateAutoRange: (wfDb: Float32Array) => void;
  // Shift dbMin and dbMax together by `deltaDb`. Used by the draggable dB
  // scale overlay on the panadapter with content-follows-finger semantics:
  // drag DOWN raises both limits so the trace slides DOWN on the canvas.
  // Clamps absolute values to Thetis's ±200 dB window.
  shiftDbRange: (deltaDb: number) => void;
  // Same as shiftDbRange but for the TX-specific range.
  shiftTxDbRange: (deltaDb: number) => void;
  // Same as shiftDbRange but for the waterfall's independent range.
  shiftWfDbRange: (deltaDb: number) => void;
  // Same as shiftWfDbRange but for the TX-specific waterfall range.
  shiftWfTxDbRange: (deltaDb: number) => void;
};

const DB_ABS_LIMIT = 200;

// Clamp a shift delta so neither endpoint crosses ±DB_ABS_LIMIT while
// preserving the span. The pre-fix code clamped each endpoint independently,
// so a far-enough drag let both endpoints pile up against the same wall and
// the span collapsed to zero — at which point the colormap maps everything
// to one colour and the panadapter/waterfall renders a solid block.
function clampShift(min: number, max: number, delta: number): { min: number; max: number } {
  const lo = -DB_ABS_LIMIT;
  const hi = DB_ABS_LIMIT;
  const maxDown = lo - min; // ≤ 0
  const maxUp = hi - max; // ≥ 0
  const d = Math.max(maxDown, Math.min(maxUp, delta));
  return { min: min + d, max: max + d };
}

// Validate a (min, max) pair coming from persisted state (server or
// localStorage). Falls back to defaults if either value is non-finite,
// outside [-DB_ABS_LIMIT, DB_ABS_LIMIT], or the span is below MIN_SPAN_DB
// (which would render the trace/waterfall as a single flat colour).
function sanitizeRange(
  min: number | null | undefined,
  max: number | null | undefined,
  defaultMin: number,
  defaultMax: number,
): { min: number; max: number } {
  if (typeof min !== 'number' || typeof max !== 'number') return { min: defaultMin, max: defaultMax };
  if (!Number.isFinite(min) || !Number.isFinite(max)) return { min: defaultMin, max: defaultMax };
  if (min < -DB_ABS_LIMIT || max > DB_ABS_LIMIT) return { min: defaultMin, max: defaultMax };
  if (max - min < MIN_SPAN_DB) return { min: defaultMin, max: defaultMax };
  return { min, max };
}

const initialRange = readSavedRange();
const initialTxRange = readSavedTxRange();
const initialWfRange = readSavedWfRange();
const initialWfTxRange = readSavedWfTxRange();
const initialWaterfallScrollSpeed = readSavedWaterfallScrollSpeed();
const initialShowBandOverlay = readBoolFlag(BAND_OVERLAY_STORAGE_KEY, true);
const initialBandEdgeAlertEnabled = readBoolFlag(BAND_EDGE_ALERT_STORAGE_KEY, true);

export const useDisplaySettingsStore = create<DisplaySettingsState>((set, get) => ({
  autoRange: false,
  dbMin: initialRange.dbMin,
  dbMax: initialRange.dbMax,
  wfDbMin: initialWfRange.wfDbMin,
  wfDbMax: initialWfRange.wfDbMax,
  wfTxDbMin: initialWfTxRange.wfTxDbMin,
  wfTxDbMax: initialWfTxRange.wfTxDbMax,
  txDbMin: initialTxRange.txDbMin,
  txDbMax: initialTxRange.txDbMax,
  txDisplayCalOffsetDb: DEFAULT_TX_DISPLAY_CAL_OFFSET_DB,
  txDisplayFftSize: DEFAULT_TX_DISPLAY_FFT_SIZE,
  txDisplayWindow: DEFAULT_TX_DISPLAY_WINDOW,
  txDisplayAvgTauMs: DEFAULT_TX_DISPLAY_AVG_TAU_MS,
  // Master enable for the TX display auto-fit. ON by default, but it only
  // actually engages for voice MOX/PTT — see shouldTxAutoRange(). TUNE and
  // two-tone are deliberately excluded: their narrow carrier / twin-tone fools
  // the percentile fit into collapsing the dB window onto the noise floor,
  // rendering the clean carrier as "grass". Those fall back to the Thetis-parity
  // fixed TX_FIXED_DB_MIN/MAX (-80..+20) window via restoreSavedTxWindows().
  txAutoRange: true,
  colormap: 'blue',
  waterfallScrollSpeed: initialWaterfallScrollSpeed,
  // Defaults until the server-side fetch lands (see hydrateFromServer at the
  // bottom of this file). The operator briefly sees a plain panadapter on
  // first paint instead of their saved image — acceptable trade-off for not
  // shipping the image on every page-load via localStorage.
  panBackground: 'basic',
  backgroundImage: null,
  backgroundImageFit: 'fill',
  // Hydrated from the server on module load (see hydrateFromServer). Until
  // that resolves the operator briefly sees the default amber trace — same
  // first-paint trade-off as panBackground / backgroundImage.
  rxTraceColor: DEFAULT_RX_TRACE_COLOR,
  showBandOverlay: initialShowBandOverlay,
  bandEdgeAlertEnabled: initialBandEdgeAlertEnabled,
  setShowBandOverlay: (v) => {
    set({ showBandOverlay: v });
    writeBoolFlag(BAND_OVERLAY_STORAGE_KEY, v);
  },
  setBandEdgeAlertEnabled: (v) => {
    set({ bandEdgeAlertEnabled: v });
    writeBoolFlag(BAND_EDGE_ALERT_STORAGE_KEY, v);
  },
  setPanBackground: async (panBackground) => {
    const prev = get().panBackground;
    set({ panBackground });
    try {
      const result = await updateDisplaySettings(
        panBackground,
        get().backgroundImageFit,
        get().rxTraceColor,
      );
      // If the server normalised the value (unknown input → 'basic'), reflect that.
      if (result.mode !== panBackground) set({ panBackground: result.mode });
    } catch {
      set({ panBackground: prev });
    }
  },
  setBackgroundImage: async (dataUrl) => {
    if (dataUrl == null) {
      try {
        const result = await deleteDisplayImage();
        set({
          backgroundImage: null,
          // Server may have transitioned mode if it had been 'image' — but we
          // only update mode if the server says so explicitly via the result.
          panBackground: result.mode,
          backgroundImageFit: result.fit,
        });
        return true;
      } catch {
        return false;
      }
    }
    try {
      const blob = await dataUrlToBlob(dataUrl);
      const result = await uploadDisplayImage(blob);
      set({
        backgroundImage: result.hasImage ? displayImageUrl(Date.now()) : null,
        panBackground: result.mode,
        backgroundImageFit: result.fit,
      });
      return result.hasImage;
    } catch {
      return false;
    }
  },
  setBackgroundImageFit: async (backgroundImageFit) => {
    const prev = get().backgroundImageFit;
    set({ backgroundImageFit });
    try {
      const result = await updateDisplaySettings(
        get().panBackground,
        backgroundImageFit,
        get().rxTraceColor,
      );
      if (result.fit !== backgroundImageFit) set({ backgroundImageFit: result.fit });
    } catch {
      set({ backgroundImageFit: prev });
    }
  },
  setRxTraceColor: async (v) => {
    if (!isHexColor(v)) return;
    const norm = v.toUpperCase();
    const prev = get().rxTraceColor;
    set({ rxTraceColor: norm });
    try {
      const result = await updateDisplaySettings(
        get().panBackground,
        get().backgroundImageFit,
        norm,
      );
      if (result.rxTraceColor !== norm) set({ rxTraceColor: result.rxTraceColor });
    } catch {
      set({ rxTraceColor: prev });
    }
  },
  setAutoRange: (autoRange) => {
    if (autoRange) {
      set({ autoRange: true });
    } else {
      // Snap back to the user's saved range if they have one, otherwise to
      // the factory fixed range. Matches the mental model of "auto is a
      // temporary override; off restores what I set".
      const saved = readSavedRange();
      set({ autoRange: false, dbMin: saved.dbMin, dbMax: saved.dbMax });
    }
  },
  setColormap: (colormap) => set({ colormap }),
  setWaterfallScrollSpeed: (value) => {
    const next = normalizeWaterfallScrollSpeed(value);
    set({ waterfallScrollSpeed: next });
    writeSavedWaterfallScrollSpeed(next);
  },
  setDbRange: (dbMin, dbMax) => {
    const next = sanitizeRange(dbMin, dbMax, FIXED_DB_MIN, FIXED_DB_MAX);
    set({ autoRange: false, dbMin: next.min, dbMax: next.max });
    writeSavedRange(next.min, next.max);
    scheduleDbRangeSave();
  },
  setTxDbRange: (txDbMin, txDbMax) => {
    const next = sanitizeRange(txDbMin, txDbMax, TX_FIXED_DB_MIN, TX_FIXED_DB_MAX);
    // A manual TX window edit takes over from auto-range.
    set({ txAutoRange: false, txDbMin: next.min, txDbMax: next.max });
    writeSavedTxRange(next.min, next.max);
    scheduleDbRangeSave();
  },
  setWfDbRange: (wfDbMin, wfDbMax) => {
    const next = sanitizeRange(wfDbMin, wfDbMax, FIXED_DB_MIN, FIXED_DB_MAX);
    set({ wfDbMin: next.min, wfDbMax: next.max });
    writeSavedWfRange(next.min, next.max);
    scheduleDbRangeSave();
  },
  setWfTxDbRange: (wfTxDbMin, wfTxDbMax) => {
    const next = sanitizeRange(wfTxDbMin, wfTxDbMax, TX_FIXED_DB_MIN, TX_FIXED_DB_MAX);
    set({ txAutoRange: false, wfTxDbMin: next.min, wfTxDbMax: next.max });
    writeSavedWfTxRange(next.min, next.max);
    scheduleDbRangeSave();
  },
  setTxDisplayParams: (p) => {
    const clampNum = (v: number, lo: number, hi: number) =>
      Math.max(lo, Math.min(hi, v));
    const next: Partial<DisplaySettingsState> = {};
    if (typeof p.calOffsetDb === 'number' && Number.isFinite(p.calOffsetDb)) {
      next.txDisplayCalOffsetDb = clampNum(
        p.calOffsetDb,
        -TX_DISPLAY_CAL_OFFSET_ABS_DB,
        TX_DISPLAY_CAL_OFFSET_ABS_DB,
      );
    }
    if (typeof p.fftSize === 'number' && (TX_DISPLAY_FFT_SIZES as readonly number[]).includes(p.fftSize)) {
      next.txDisplayFftSize = p.fftSize;
    }
    if (typeof p.window === 'number' && TX_DISPLAY_WINDOWS.some((w) => w.value === p.window)) {
      next.txDisplayWindow = p.window;
    }
    if (typeof p.avgTauMs === 'number' && Number.isFinite(p.avgTauMs)) {
      next.txDisplayAvgTauMs = clampNum(p.avgTauMs, TX_DISPLAY_AVG_TAU_MIN_MS, TX_DISPLAY_AVG_TAU_MAX_MS);
    }
    if (Object.keys(next).length === 0) return;
    // No localStorage mirror — server is authoritative for these params.
    set(next);
    scheduleTxDisplaySave();
  },
  resetTxDisplayParams: () => {
    set({
      txDisplayCalOffsetDb: DEFAULT_TX_DISPLAY_CAL_OFFSET_DB,
      txDisplayFftSize: DEFAULT_TX_DISPLAY_FFT_SIZE,
      txDisplayWindow: DEFAULT_TX_DISPLAY_WINDOW,
      txDisplayAvgTauMs: DEFAULT_TX_DISPLAY_AVG_TAU_MS,
      txAutoRange: true,
    });
    scheduleTxDisplaySave();
  },
  setTxAutoRange: (v) => {
    if (v) {
      set({ txAutoRange: true });
    } else {
      // Off restores the operator's saved TX windows (mirrors setAutoRange).
      set({ txAutoRange: false });
      get().restoreSavedTxWindows();
    }
  },
  restoreSavedTxWindows: () => {
    const tx = readSavedTxRange();
    const wf = readSavedWfTxRange();
    const { txDbMin, txDbMax, wfTxDbMin, wfTxDbMax } = get();
    // Avoid a redundant set() (and render) when the windows are already there.
    if (
      txDbMin === tx.txDbMin &&
      txDbMax === tx.txDbMax &&
      wfTxDbMin === wf.wfTxDbMin &&
      wfTxDbMax === wf.wfTxDbMax
    ) {
      return;
    }
    set({
      txDbMin: tx.txDbMin,
      txDbMax: tx.txDbMax,
      wfTxDbMin: wf.wfTxDbMin,
      wfTxDbMax: wf.wfTxDbMax,
    });
  },
  updateTxAutoRange: (pixels) => {
    if (!get().txAutoRange || pixels.length === 0) return;
    const [floor, peak] = txFloorPeak(pixels);
    let targetMin = floor - TX_AUTO_FLOOR_MARGIN_DB;
    let targetMax = peak + TX_AUTO_CEIL_MARGIN_DB;
    if (targetMax - targetMin < MIN_SPAN_DB) {
      const mid = 0.5 * (targetMin + targetMax);
      targetMin = mid - MIN_SPAN_DB / 2;
      targetMax = mid + MIN_SPAN_DB / 2;
    }
    const s = TX_AUTO_SMOOTHING;
    const { txDbMin, txDbMax, wfTxDbMin, wfTxDbMax } = get();
    // EMA toward the target so the window settles smoothly over a few frames
    // instead of jumping every tick. Both TX windows track the same signal.
    set({
      txDbMin: txDbMin * (1 - s) + targetMin * s,
      txDbMax: txDbMax * (1 - s) + targetMax * s,
      wfTxDbMin: wfTxDbMin * (1 - s) + targetMin * s,
      wfTxDbMax: wfTxDbMax * (1 - s) + targetMax * s,
    });
  },
  resetDbRanges: () => {
    set({
      autoRange: false,
      dbMin: FIXED_DB_MIN,
      dbMax: FIXED_DB_MAX,
      txDbMin: TX_FIXED_DB_MIN,
      txDbMax: TX_FIXED_DB_MAX,
      wfDbMin: FIXED_DB_MIN,
      wfDbMax: FIXED_DB_MAX,
      wfTxDbMin: TX_FIXED_DB_MIN,
      wfTxDbMax: TX_FIXED_DB_MAX,
    });
    writeSavedRange(FIXED_DB_MIN, FIXED_DB_MAX);
    writeSavedTxRange(TX_FIXED_DB_MIN, TX_FIXED_DB_MAX);
    writeSavedWfRange(FIXED_DB_MIN, FIXED_DB_MAX);
    writeSavedWfTxRange(TX_FIXED_DB_MIN, TX_FIXED_DB_MAX);
    scheduleDbRangeSave();
  },
  shiftDbRange: (deltaDb) => {
    // While AUTO is on, the live dbMin/dbMax are EMA-smoothed band-tracking
    // outputs (often messy floats and a tighter span than the user's saved
    // FIXED range). Promoting those into localStorage would lock the user
    // into a transient AUTO snapshot. Instead, mirror setAutoRange(false):
    // start from the last persisted FIXED range, apply the shift to that.
    const { autoRange, dbMin, dbMax } = get();
    const baseMin = autoRange ? readSavedRange().dbMin : dbMin;
    const baseMax = autoRange ? readSavedRange().dbMax : dbMax;
    const { min: nextMin, max: nextMax } = clampShift(baseMin, baseMax, deltaDb);
    set({ autoRange: false, dbMin: nextMin, dbMax: nextMax });
    writeSavedRange(nextMin, nextMax);
    scheduleDbRangeSave();
  },
  shiftTxDbRange: (deltaDb) => {
    const { txDbMin, txDbMax } = get();
    const { min: nextMin, max: nextMax } = clampShift(txDbMin, txDbMax, deltaDb);
    set({ txDbMin: nextMin, txDbMax: nextMax });
    writeSavedTxRange(nextMin, nextMax);
    scheduleDbRangeSave();
  },
  shiftWfDbRange: (deltaDb) => {
    const { wfDbMin, wfDbMax } = get();
    const { min: nextMin, max: nextMax } = clampShift(wfDbMin, wfDbMax, deltaDb);
    set({ wfDbMin: nextMin, wfDbMax: nextMax });
    writeSavedWfRange(nextMin, nextMax);
    scheduleDbRangeSave();
  },
  shiftWfTxDbRange: (deltaDb) => {
    const { wfTxDbMin, wfTxDbMax } = get();
    const { min: nextMin, max: nextMax } = clampShift(wfTxDbMin, wfTxDbMax, deltaDb);
    set({ wfTxDbMin: nextMin, wfTxDbMax: nextMax });
    writeSavedWfTxRange(nextMin, nextMax);
    scheduleDbRangeSave();
  },
  updateAutoRange: (wfDb) => {
    if (!get().autoRange || wfDb.length === 0) return;
    const [p5, p95] = percentiles(wfDb);
    let targetMin = p5 - AUTO_FLOOR_MARGIN_DB;
    let targetMax = p95 + AUTO_CEIL_MARGIN_DB;
    if (targetMax - targetMin < MIN_SPAN_DB) {
      const mid = 0.5 * (targetMin + targetMax);
      targetMin = mid - MIN_SPAN_DB / 2;
      targetMax = mid + MIN_SPAN_DB / 2;
    }
    const { dbMin, dbMax } = get();
    set({
      dbMin: dbMin * (1 - SMOOTHING) + targetMin * SMOOTHING,
      dbMax: dbMax * (1 - SMOOTHING) + targetMax * SMOOTHING,
    });
  },
}));

// p5/p95 via a sorted copy. For the ~1024-sample widths we see in
// production this is well under 1 ms; a quickselect would be overkill.
function percentiles(arr: Float32Array): [number, number] {
  const n = arr.length;
  const sorted = Float32Array.from(arr);
  sorted.sort();
  const lowIdx = Math.min(n - 1, Math.max(0, Math.floor(0.05 * n)));
  const highIdx = Math.min(n - 1, Math.max(0, Math.floor(0.95 * n)));
  return [sorted[lowIdx] ?? FIXED_DB_MIN, sorted[highIdx] ?? FIXED_DB_MAX];
}

// TX floor/peak for auto-range. Filters out non-finite / sentinel bins (e.g.
// out-of-span edges set to the invalid-bin sentinel) so they don't drag the
// floor, then returns [low-percentile floor, high-percentile peak]. The peak
// percentile (not the absolute max) ignores a lone hot bin.
function txFloorPeak(arr: Float32Array): [number, number] {
  const vals: number[] = [];
  for (let i = 0; i < arr.length; i++) {
    const v = arr[i] ?? Number.NaN;
    if (Number.isFinite(v) && v > -250) vals.push(v);
  }
  if (vals.length === 0) return [TX_FIXED_DB_MIN, TX_FIXED_DB_MAX];
  vals.sort((a, b) => a - b);
  const n = vals.length;
  const floorIdx = Math.min(n - 1, Math.max(0, Math.floor(TX_AUTO_FLOOR_PCT * n)));
  const peakIdx = Math.min(n - 1, Math.max(0, Math.floor(TX_AUTO_PEAK_PCT * n)));
  return [vals[floorIdx] ?? TX_FIXED_DB_MIN, vals[peakIdx] ?? TX_FIXED_DB_MAX];
}

// Decode a data:URL produced by canvas.toDataURL() into a Blob the multipart
// upload can carry. Used by setBackgroundImage to bridge the panel's
// canvas-based compression pipeline to the backend's byte storage.
async function dataUrlToBlob(dataUrl: string): Promise<Blob> {
  const res = await fetch(dataUrl);
  return res.blob();
}

// One-shot hydration from the backend at module load. If the server has
// nothing yet but this browser still holds a legacy localStorage image,
// push it up once and clear local — that's the migration path for operators
// who set a background before the server-side store existed. Either way the
// three legacy keys are removed afterwards so the localStorage stays clean.
async function hydrateFromServer(): Promise<void> {
  let server: Awaited<ReturnType<typeof fetchDisplaySettings>>;
  try {
    server = await fetchDisplaySettings();
  } catch {
    // Backend unreachable; leave defaults in place. Next call to
    // setPanBackground / setBackgroundImage will hit the server.
    return;
  }

  const legacy = readLegacyLocalStorage();
  const legacyColor = readLegacyRxTraceColor();
  const serverHasContent =
    server.hasImage ||
    server.mode !== 'basic' ||
    server.fit !== 'fill' ||
    server.rxTraceColor !== DEFAULT_RX_TRACE_COLOR;

  if (!serverHasContent && (legacy?.image || legacy?.mode || legacy?.fit || legacyColor)) {
    try {
      if (legacy?.mode || legacy?.fit || legacyColor) {
        const next = await updateDisplaySettings(
          legacy?.mode ?? server.mode,
          legacy?.fit ?? server.fit,
          legacyColor ?? server.rxTraceColor,
        );
        server = next;
      }
      if (legacy?.image) {
        const blob = await dataUrlToBlob(legacy.image);
        server = await uploadDisplayImage(blob);
      }
    } catch {
      // Migration failed — leave legacy keys in place so we retry next load.
      return;
    }
  }

  clearLegacyLocalStorage();

  // Server dB values of null mean the field was never stored (fresh install
  // or first run after upgrading to a version that added server persistence).
  // Use server values when present; otherwise keep the localStorage-initialized
  // state and push it up so the server has it for next restart.
  const serverHasDbRange = server.dbMin !== null;

  // Sanitize server-provided ranges so a corrupt or pre-validation row in
  // zeus-prefs.db (e.g. wfDbMin == wfDbMax from earlier builds) can't render
  // the panadapter/waterfall as a single flat colour on next load. If the
  // server value is invalid we fall back to defaults and let scheduleDbRangeSave
  // push the corrected value back up.
  const panRange = serverHasDbRange
    ? sanitizeRange(server.dbMin, server.dbMax, FIXED_DB_MIN, FIXED_DB_MAX)
    : null;
  const panTxRange = serverHasDbRange
    ? sanitizeRange(server.txDbMin, server.txDbMax, TX_FIXED_DB_MIN, TX_FIXED_DB_MAX)
    : null;
  const wfRange = serverHasDbRange
    ? sanitizeRange(server.wfDbMin, server.wfDbMax, FIXED_DB_MIN, FIXED_DB_MAX)
    : null;
  const wfTxRange = serverHasDbRange
    ? sanitizeRange(server.wfTxDbMin, server.wfTxDbMax, TX_FIXED_DB_MIN, TX_FIXED_DB_MAX)
    : null;
  const serverRangeWasCorrupt =
    serverHasDbRange &&
    (panRange!.min !== server.dbMin ||
      panRange!.max !== server.dbMax ||
      panTxRange!.min !== server.txDbMin ||
      panTxRange!.max !== server.txDbMax ||
      wfRange!.min !== server.wfDbMin ||
      wfRange!.max !== server.wfDbMax ||
      wfTxRange!.min !== server.wfTxDbMin ||
      wfTxRange!.max !== server.wfTxDbMax);

  useDisplaySettingsStore.setState({
    panBackground: server.mode,
    backgroundImage: server.hasImage ? displayImageUrl(Date.now()) : null,
    backgroundImageFit: server.fit,
    rxTraceColor: server.rxTraceColor,
    ...(serverHasDbRange
      ? {
          dbMin: panRange!.min,
          dbMax: panRange!.max,
          txDbMin: panTxRange!.min,
          txDbMax: panTxRange!.max,
          wfDbMin: wfRange!.min,
          wfDbMax: wfRange!.max,
          wfTxDbMin: wfTxRange!.min,
          wfTxDbMax: wfTxRange!.max,
        }
      : {}),
    // TX display analyzer params — server wins when present; null means never
    // stored, so the in-memory default stands.
    ...(server.txDisplayCalOffsetDb !== null ? { txDisplayCalOffsetDb: server.txDisplayCalOffsetDb } : {}),
    ...(server.txDisplayFftSize !== null ? { txDisplayFftSize: server.txDisplayFftSize } : {}),
    ...(server.txDisplayWindow !== null ? { txDisplayWindow: server.txDisplayWindow } : {}),
    ...(server.txDisplayAvgTauMs !== null ? { txDisplayAvgTauMs: server.txDisplayAvgTauMs } : {}),
  });

  if (!serverHasDbRange || serverRangeWasCorrupt) {
    // Push the current in-memory values (from localStorage or defaults) up
    // to the server so subsequent restarts find them persisted. This is the
    // one-time migration for operators upgrading from localStorage-only storage.
    scheduleDbRangeSave();
  }
}

function readLegacyLocalStorage(): { mode: PanBackgroundMode | null; fit: BackgroundImageFit | null; image: string | null } | null {
  if (typeof localStorage === 'undefined') return null;
  try {
    const rawMode = localStorage.getItem(LEGACY_PAN_BG_KEY);
    const rawFit = localStorage.getItem(LEGACY_BG_FIT_KEY);
    const rawImg = localStorage.getItem(LEGACY_BG_IMAGE_KEY);
    const mode =
      rawMode === 'basic' || rawMode === 'beam-map' || rawMode === 'image' ? rawMode : null;
    const fit =
      rawFit === 'fit' || rawFit === 'fill' || rawFit === 'stretch' ? rawFit : null;
    const image = rawImg && rawImg.startsWith('data:image/') ? rawImg : null;
    return { mode, fit, image };
  } catch {
    return null;
  }
}

function clearLegacyLocalStorage(): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.removeItem(LEGACY_PAN_BG_KEY);
    localStorage.removeItem(LEGACY_BG_IMAGE_KEY);
    localStorage.removeItem(LEGACY_BG_FIT_KEY);
    localStorage.removeItem(LEGACY_RX_TRACE_COLOR_KEY);
  } catch {
    /* private mode — nothing to clean up */
  }
}

void hydrateFromServer();
