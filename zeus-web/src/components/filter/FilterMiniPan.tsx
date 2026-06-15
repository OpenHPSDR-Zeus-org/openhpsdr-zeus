// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.2.1 — mini-panadapter inside the advanced
// filter ribbon. Originally a fixed 12 kHz peak-hold trace with draggable
// passband walls (mockup docs/pics/filterpanel_mockup.png). This revamp keeps
// that core but layers the shared snap/pop signal estimator on top so the
// filter panel sees the same flattened baseline and detected carriers the main
// panadapter does, and adds finer, more granular edge control:
//
//   • POP'd trace + floor line — when global Pop is engaged, the trace plots
//     SNR above the per-bin noise floor (weak in-band signals lift off a flat
//     baseline). The estimated floor contour is drawn faintly so the operator
//     sees what the gate is reading.
//   • In-band peak markers — when global Snap is engaged, detected carriers in
//     the visible window get amber ticks (sanctioned signal-strength colour,
//     SNR-scaled alpha); carriers inside the passband read brighter/taller than
//     those outside it, so you see exactly what the filter is letting through.
//   • Magnetic edge snap — dragging a LOW/HIGH wall gently snaps onto a nearby
//     detected carrier (within SNAP_PULL_HZ); hold Alt to place freely. Only
//     active while Snap is engaged.
//   • Granular fine-tune — scroll-wheel nudges the hovered edge by the per-mode
//     step (Shift ×10); Ctrl/⌘+wheel zooms the visible span. A live width pill
//     is click-to-edit (center-preserving Hz entry).
//
// All estimator features reuse the SAME singleton floor the panadapter/
// waterfall already maintain (signal-estimator.ts). That estimator only runs
// while global Pop or Snap is on, so when both are off this component is
// byte-for-byte its previous self and carries no extra cost. Pop/Snap defaults,
// colours and snap feel are display/UX surface — the maintainer's call
// (CLAUDE.md); flagged for sign-off.
//
// Uses Canvas 2D (not a second WebGL context) — at this size the 2D path hits
// the <2 ms/frame budget comfortably and avoids scissor-clipping or sharing the
// main panadapter's GL context.

import { useEffect, useRef, useState } from 'react';
import { registerFrameConsumer, useDisplayStore } from '../../state/display-store';
import { useConnectionStore } from '../../state/connection-store';
import { useThemeStore } from '../../state/theme-store';
import { useDisplaySettingsStore } from '../../state/display-settings-store';
import {
  detectPeaks,
  getNoiseFloor,
  registerEstimatorConsumer,
  signalExtentHz,
  useSignalEnhanceStore,
  type DetectedPeak,
} from '../../dsp/signal-estimator';
import { setFilter } from '../../api/client';
import { formatCutOffset, formatFilterWidth, nudgeStepHz } from './filterPresets';
import type { RxMode } from '../../api/client';

const DEFAULT_SPAN_HZ = 12_000;       // initial visible window around VFO
const MIN_SPAN_HZ = 3_000;            // Ctrl+wheel zoom-in floor
const MAX_SPAN_HZ = 48_000;           // Ctrl+wheel zoom-out ceiling
const TICK_STEP_HZ = 2_000;           // base axis-label spacing (scaled by span)
const DB_FLOOR = -130;
const DB_CEIL = -30;
const POP_RANGE_DB = 55;              // SNR (dB above floor) that reaches full height in Pop mode
const DRAG_MIN_INTERVAL_MS = 50;
const EDGE_HIT_PX = 6;
const SNAP_PULL_HZ = 150;             // magnetic pull radius for edge→carrier snap
const EDGE_ANCHOR_HZ = 400;           // radius to lock signal-extent onto a carrier crest
const BRACKET_MAX = 6;                // most signal-edge markers drawn at once
const BRACKET_MIN_SNR_DB = 6;         // only mark carriers this far above the floor
const BRACKET_TRACK_TOLERANCE_HZ = 260; // nearby detections belong to the same visual marker
const BRACKET_TRACK_TTL_MS = 1800;     // keep marker state briefly across missed detections
const BRACKET_SMOOTH_ALPHA = 0.055;    // EMA for measured signal edges
const BRACKET_LABEL_MAX = 1;           // one active bandwidth lock; guides may still show
const BRACKET_LABEL_HOLD_MS = 1500;    // minimum dwell before small label changes
const BRACKET_LABEL_CONFIRM_MS = 900;  // candidate value must persist before replacing a label
const BRACKET_LABEL_QUANTUM_HZ = 100;  // kHz labels step by 0.1 kHz
const BRACKET_LABEL_JUMP_HZ = 600;     // real bandwidth changes can break the dwell early
const BRACKET_LABEL_JUMP_CONFIRM_MS = 350; // but only after a brief confirmation
const BRACKET_LABEL_MIN_CONFIDENCE = 0.45;
const BRACKET_CONFIDENCE_SPAN_HZ = 550; // edge disagreement that drains confidence
const BRACKET_OVERLAP_RATIO = 0.45;    // suppress duplicate labels on the same signal hump
const EQ_NODE_MAX = 3;                 // strongest frequency nodes shown in analyzer mode
const EQ_NODE_MIN_CONFIDENCE = 0.32;
const MEASUREMENT_PEAK_HOLD_MS = 1000; // hold prominent-frequency readouts for operator readability
const PEAKHOLD_DECAY_PX = 0.45;       // peak-hold envelope fall rate (px/frame ≈ 13 px/s)
const FIT_HIT_PX = 26;                // click-to-fit grab radius around a carrier
const FIT_MARGIN_HZ = 120;            // breathing room added each side when fitting
const AVG_ALPHA = 0.02;               // PSD time-average EMA (~2 s) for instrument-like width
const OBW_FRACTION = 0.99;            // ITU occupied-bandwidth power fraction (99%)

// Palette — passband walls / dots / halo are neutral silvery (read on both
// themes), but the four *text* surfaces (LOW/HIGH CUT label + value, axis
// ticks, VFO centre tick) and the spectrum trace are resolved from
// --fg-0 / --fg-1 / --fg-2 at draw time so the Theme Settings token pickers
// drive them.
const COL_VFO_CENTER = 'rgba(200, 205, 215, 0.14)'; // subtle neutral VFO line
const COL_CUT_TICK = 'rgba(220, 225, 232, 0.35)';   // hairline callout connecting label to wall

type DragMode = 'lo' | 'hi' | 'inside';

type BracketTrack = {
  crestHz: number;
  loHz: number;
  hiHz: number;
  labelBwHz: number;
  candidateBwHz: number;
  candidateSince: number;
  confidence: number;
  snrDb: number;
  heldCrestHz: number;
  heldSnrDb: number;
  heldUntil: number;
  lastLabelAt: number;
  lastSeenAt: number;
};

function presetIsFixed(name: string | null): boolean {
  return !!name && /^F([1-9]|10)$/.test(name);
}

function isSymmetricMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
}

// Single-sideband modes demodulate one side of the carrier only: LSB/CWL below
// it (negative offsets), USB/CWU above it (positive offsets). DIGL/DIGU and the
// symmetric modes straddle 0 (see filterPresets.ts), so they carry no sideband
// constraint here.
function lowerSidebandMode(mode: RxMode): boolean {
  return mode === 'LSB' || mode === 'CWL';
}
function upperSidebandMode(mode: RxMode): boolean {
  return mode === 'USB' || mode === 'CWU';
}

const FIT_MIN_EDGE_HZ = 50; // keep at least this sliver of passband off the carrier

// Map a clicked signal's VFO-relative energy extent (loOff..hiOff, plus a little
// breathing room) to a passband that respects the active mode's sideband. For
// SSB/CW the passband must stay on the demodulated side of the carrier — a
// signal sitting entirely on the WRONG side is unreachable without retuning, so
// we return null and leave the filter untouched rather than flipping it to the
// opposite sideband (operator report 2026-06-14: a click in LSB threw the
// passband onto the USB side). Symmetric / DIG modes pass through unchanged.
export function fitPassbandForMode(
  mode: RxMode,
  loOff: number,
  hiOff: number,
  margin: number,
): { low: number; high: number } | null {
  let low = Math.round(loOff - margin);
  let high = Math.round(hiOff + margin);
  if (lowerSidebandMode(mode)) {
    if (loOff >= 0) return null;            // signal entirely above the carrier — wrong side
    high = Math.min(high, -FIT_MIN_EDGE_HZ); // clamp the passband below the carrier
  } else if (upperSidebandMode(mode)) {
    if (hiOff <= 0) return null;            // signal entirely below the carrier — wrong side
    low = Math.max(low, FIT_MIN_EDGE_HZ);   // clamp the passband above the carrier
  }
  if (high <= low + FIT_MIN_EDGE_HZ) return null;
  return { low, high };
}

// Format VFO-relative Hz offset as absolute-MHz with 3 decimals (e.g. 14.249).
// Used for x-axis tick labels.
function formatTickMhz(absHz: number): string {
  return (absHz / 1_000_000).toFixed(3);
}

// Axis label spacing scales with the visible span so a zoomed-out window does
// not crowd the baseline with ticks.
function tickStepForSpan(spanHz: number): number {
  if (spanHz <= 14_000) return TICK_STEP_HZ;
  if (spanHz <= 28_000) return 5_000;
  return 10_000;
}

// Resolve a CSS colour token (hex or rgb()/rgba()) to [r,g,b] so we can build
// alpha variants at draw time. Lets the accent passband follow the live
// --accent token (and any Theme Settings override) without hard-coding hex.
function parseRgb(s: string): [number, number, number] {
  const t = s.trim();
  if (t.startsWith('#')) {
    let h = t.slice(1);
    if (h.length === 3) h = h.split('').map((ch) => ch + ch).join('');
    const n = Number.parseInt(h, 16);
    if (Number.isFinite(n)) return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }
  const m = t.match(/\d+(?:\.\d+)?/g);
  if (m && m.length >= 3) return [Number(m[0]), Number(m[1]), Number(m[2])];
  return [74, 158, 255]; // --accent fallback
}

// Snap a dragged edge onto the nearest detected carrier when it falls inside
// SNAP_PULL_HZ — the "magnetic assist". Returns the (possibly) pulled absolute
// frequency. peaks are captured once at drag start (carriers move slowly).
function magnetEdge(absHz: number, peaks: DetectedPeak[]): number {
  let best = absHz;
  let bestDist = SNAP_PULL_HZ;
  for (const p of peaks) {
    const d = Math.abs(p.hz - absHz);
    if (d < bestDist) {
      bestDist = d;
      best = p.hz;
    }
  }
  return best;
}

// ITU-style occupied bandwidth: within [loBin, hiBin] of an averaged POWER
// spectrum, integrate signal power above the noise floor and trim (1−frac)/2 of
// the total from each tail — the band carrying `frac` (e.g. 99%) of the power.
// This is the standard occupied-bandwidth measure, so the reported width is the
// bandwidth the signal is actually transmitting at, not its noisy instantaneous
// skirts. Returns inclusive [loBin, hiBin].
function occupiedBandBins(
  avgLin: Float32Array,
  floorDb: Float32Array | null,
  loBin: number,
  hiBin: number,
  frac: number,
): [number, number] {
  const haveFloor = floorDb !== null && floorDb.length === avgLin.length;
  const sig = (i: number): number => {
    const fl = haveFloor ? Math.pow(10, floorDb![i]! / 10) : 0;
    const s = avgLin[i]! - fl;
    return s > 0 ? s : 0;
  };
  let total = 0;
  for (let i = loBin; i <= hiBin; i++) total += sig(i);
  if (total <= 0) return [loBin, hiBin];
  const tail = ((1 - frac) / 2) * total;
  let cum = 0;
  let lo = loBin;
  for (let i = loBin; i <= hiBin; i++) {
    cum += sig(i);
    if (cum >= tail) { lo = i; break; }
  }
  cum = 0;
  let hi = hiBin;
  for (let i = hiBin; i >= loBin; i--) {
    cum += sig(i);
    if (cum >= tail) { hi = i; break; }
  }
  return [lo, hi <= lo ? lo : hi];
}

function quantizeBracketBandwidth(hz: number): number {
  const safeHz = Math.max(0, hz);
  if (safeHz < 1000) return Math.round(safeHz / 10) * 10;
  return Math.round(safeHz / BRACKET_LABEL_QUANTUM_HZ) * BRACKET_LABEL_QUANTUM_HZ;
}

function smoothedBracketMeasurement(
  tracks: BracketTrack[],
  now: number,
  crestHz: number,
  loHz: number,
  hiHz: number,
  snrDb: number,
): BracketTrack {
  let best: BracketTrack | null = null;
  let bestDist = BRACKET_TRACK_TOLERANCE_HZ;
  for (const t of tracks) {
    const dist = Math.abs(t.crestHz - crestHz);
    if (dist < bestDist) {
      bestDist = dist;
      best = t;
    }
  }

  const rawBwHz = Math.max(0, hiHz - loHz);
  const quantizedBwHz = quantizeBracketBandwidth(rawBwHz);
  if (!best) {
    const track: BracketTrack = {
      crestHz,
      loHz,
      hiHz,
      labelBwHz: quantizedBwHz,
      candidateBwHz: quantizedBwHz,
      candidateSince: now,
      confidence: 0.35,
      snrDb,
      heldCrestHz: crestHz,
      heldSnrDb: snrDb,
      heldUntil: now + MEASUREMENT_PEAK_HOLD_MS,
      lastLabelAt: now,
      lastSeenAt: now,
    };
    tracks.push(track);
    return track;
  }

  if (best.lastSeenAt === now) return best;

  const stale = now - best.lastSeenAt > 500;
  const edgeAlpha = stale ? 0.35 : BRACKET_SMOOTH_ALPHA;
  best.crestHz += (crestHz - best.crestHz) * Math.min(0.4, edgeAlpha * 1.5);
  best.loHz += (loHz - best.loHz) * edgeAlpha;
  best.hiHz += (hiHz - best.hiHz) * edgeAlpha;
  best.snrDb += (snrDb - best.snrDb) * 0.14;
  best.lastSeenAt = now;

  if (best.snrDb >= best.heldSnrDb || now >= best.heldUntil) {
    best.heldCrestHz = best.crestHz;
    best.heldSnrDb = best.snrDb;
    best.heldUntil = now + MEASUREMENT_PEAK_HOLD_MS;
  }

  const smoothedBwHz = Math.max(0, best.hiHz - best.loHz);
  const widthErrorHz = Math.abs(rawBwHz - smoothedBwHz);
  const targetConfidence = Math.max(0, Math.min(1, 1 - widthErrorHz / BRACKET_CONFIDENCE_SPAN_HZ));
  best.confidence += (targetConfidence - best.confidence) * 0.16;

  const nextLabelHz = quantizeBracketBandwidth(smoothedBwHz);
  if (nextLabelHz !== best.candidateBwHz) {
    best.candidateBwHz = nextLabelHz;
    best.candidateSince = now;
  }

  const labelDwelled = now - best.lastLabelAt >= BRACKET_LABEL_HOLD_MS;
  const candidateConfirmed = now - best.candidateSince >= BRACKET_LABEL_CONFIRM_MS;
  const jumpConfirmed =
    Math.abs(best.candidateBwHz - best.labelBwHz) >= BRACKET_LABEL_JUMP_HZ &&
    now - best.candidateSince >= BRACKET_LABEL_JUMP_CONFIRM_MS;
  if (
    best.candidateBwHz !== best.labelBwHz &&
    ((labelDwelled && candidateConfirmed) || jumpConfirmed)
  ) {
    best.labelBwHz = best.candidateBwHz;
    best.lastLabelAt = now;
  }
  return best;
}

function rangeOverlapRatio(aLo: number, aHi: number, bLo: number, bHi: number): number {
  const loA = Math.min(aLo, aHi);
  const hiA = Math.max(aLo, aHi);
  const loB = Math.min(bLo, bHi);
  const hiB = Math.max(bLo, bHi);
  const overlap = Math.max(0, Math.min(hiA, hiB) - Math.max(loA, loB));
  const denom = Math.max(1, Math.min(hiA - loA, hiB - loB));
  return overlap / denom;
}

function rectsOverlap(a: { left: number; right: number; top: number; bottom: number }, b: { left: number; right: number; top: number; bottom: number }): boolean {
  return a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
}

export function FilterMiniPan() {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  // Visible span lives in a ref (read by the imperative draw loop) plus a state
  // mirror so the width pill / React tree re-render on zoom.
  const spanHzRef = useRef<number>(DEFAULT_SPAN_HZ);
  const [, setSpanTick] = useState(0);
  const redrawRef = useRef<(() => void) | null>(null);
  const hoverEdgeRef = useRef<DragMode | null>(null);

  // Live filter edges + mode for the editable width pill. These re-render the
  // component (cheap) but the per-frame canvas path stays imperative.
  const filterLowHz = useConnectionStore((s) => s.filterLowHz);
  const filterHighHz = useConnectionStore((s) => s.filterHighHz);
  const mode = useConnectionStore((s) => s.mode);

  const [editingWidth, setEditingWidth] = useState(false);
  const [widthDraft, setWidthDraft] = useState('');

  const dragRef = useRef<{
    mode: DragMode;
    rect: DOMRect;
    spanHz: number;
    activeSlot: string;
    startLoHz: number;
    startHiHz: number;
    startX: number;
    pendingLo: number;
    pendingHi: number;
    lastWriteAt: number;
    flushTimer: number | null;
    pointerId: number;
    peaks: DetectedPeak[];
  } | null>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d', { alpha: true });
    if (!ctx) return;

    // Tell the realtime client that decoded spectrum frames are needed —
    // ws-client.ts skips decodeDisplayFrame entirely when no consumer is
    // registered (all spectrum surfaces closed).
    const releaseFrameConsumer = registerFrameConsumer();
    // Keep the floor estimator live while this panel is open so the signal-aware
    // features (heat trace, bandwidth brackets, fit-to-signal) work without the
    // operator first toggling global Pop/Snap.
    const releaseEstimator = registerEstimatorConsumer();

    let rafHandle = 0;
    let lastSeq: number | null = null;
    let traceY: Float32Array | null = null; // reused per-column trace Y buffer
    let traceSm: Float32Array | null = null; // reused smoothed-trace buffer
    let peakHoldY: Float32Array | null = null; // per-column peak-hold envelope (Y)
    let peakHoldKey = ''; // geometry key; reset the envelope when it changes
    let autoLoDb = NaN; // EMA of the visible-window floor (dynamic vertical scale)
    let autoHiDb = NaN; // EMA of the visible-window peak
    let avgLin: Float32Array | null = null; // time-averaged linear power per bin
    let avgDb: Float32Array | null = null; // averaged spectrum in dB (for the edge walk)
    let avgKey = ''; // geometry key; reset the average when it changes
    let bracketTracks: BracketTrack[] = []; // smoothed signal-bandwidth annotations
    let bracketKey = ''; // geometry key; reset annotations when the display window changes

    const draw = () => {
      rafHandle = 0;
      const now = performance.now();
      const d = useDisplayStore.getState();
      const c = useConnectionStore.getState();
      if (lastSeq !== null && d.lastSeq === lastSeq) return;
      lastSeq = d.lastSeq;

      const spanHz = spanHzRef.current;
      const dpr = window.devicePixelRatio || 1;
      const cssW = canvas.clientWidth;
      const cssH = canvas.clientHeight;
      if (cssW <= 0 || cssH <= 0) return;
      const w = Math.floor(cssW * dpr);
      const h = Math.floor(cssH * dpr);
      if (canvas.width !== w) canvas.width = w;
      if (canvas.height !== h) canvas.height = h;

      ctx.clearRect(0, 0, w, h);

      // Resolve theme-driven text colours once per frame. Operator overrides
      // from the Theme Settings panel flow through these tokens.
      const cs = getComputedStyle(document.documentElement);
      const fg0 = cs.getPropertyValue('--fg-0').trim() || '#edeef1';
      const fg1 = cs.getPropertyValue('--fg-1').trim() || '#cccccc';
      const fg2 = cs.getPropertyValue('--fg-2').trim() || '#7c8088';
      const fg3 = cs.getPropertyValue('--fg-3').trim() || '#5a5a60';
      const colTickLabel = fg2;
      const colTickLabelCenter = fg0;
      const colCutKey = fg2;
      const colCutVal = fg0;
      const [fg0r, fg0g, fg0b] = parseRgb(fg0);
      const [fg1r, fg1g, fg1b] = parseRgb(fg1);
      const [fg3r, fg3g, fg3b] = parseRgb(fg3);
      const ink0 = (a: number) => `rgba(${fg0r}, ${fg0g}, ${fg0b}, ${a})`;
      const ink1 = (a: number) => `rgba(${fg1r}, ${fg1g}, ${fg1b}, ${a})`;
      const ink3 = (a: number) => `rgba(${fg3r}, ${fg3g}, ${fg3b}, ${a})`;
      // Accent drives the active-filter passband (focus/state token, CLAUDE.md).
      const [ar, ag, ab] = parseRgb(cs.getPropertyValue('--accent') || '#4a9eff');
      const accent = (a: number) => `rgba(${ar}, ${ag}, ${ab}, ${a})`;

      // Snap/pop state. This panel registers an estimator consumer, so the floor
      // is maintained whenever the panel is open (not only when global Pop/Snap
      // are on) — the heat trace, brackets and fit-to-signal all read it.
      const enh = useSignalEnhanceStore.getState();
      const floor = getNoiseFloor();
      const signalColor = useDisplaySettingsStore.getState().rxTraceColor;
      const [sr, sg, sb] = parseRgb(signalColor || '#FFA028');
      const signal = (a: number) => `rgba(${sr}, ${sg}, ${sb}, ${a})`;

      // Reserve the top ~22 px for LOW CUT / HIGH CUT wall callouts and the
      // bottom ~14 px for the x-axis labels so neither overlap the trace.
      const labelH = Math.round(22 * dpr);
      const axisH = Math.round(14 * dpr);
      const plotTop = labelH;
      const plotH = Math.max(1, h - axisH - labelH);
      const plotBottom = plotTop + plotH;
      const tickStep = tickStepForSpan(spanHz);

      // Instrument well: the canvas owns the display surface so the docked
      // tile reads like a small SDR scope instead of a flat transparent strip.
      const bg = ctx.createLinearGradient(0, 0, 0, h);
      bg.addColorStop(0.0, 'rgba(255, 255, 255, 0.035)');
      bg.addColorStop(0.18, 'rgba(255, 255, 255, 0.010)');
      bg.addColorStop(1.0, 'rgba(0, 0, 0, 0.22)');
      ctx.fillStyle = bg;
      ctx.fillRect(0, 0, w, h);

      const well = ctx.createLinearGradient(0, plotTop, 0, plotBottom);
      well.addColorStop(0.0, 'rgba(255, 255, 255, 0.030)');
      well.addColorStop(0.38, 'rgba(255, 255, 255, 0.010)');
      well.addColorStop(1.0, 'rgba(0, 0, 0, 0.24)');
      ctx.fillStyle = well;
      ctx.fillRect(0, plotTop, w, plotH);

      ctx.save();
      ctx.lineWidth = 1 * dpr;
      for (let i = 1; i <= 3; i++) {
        const y = Math.round(plotTop + (plotH * i) / 4) + 0.5;
        ctx.strokeStyle = ink3(0.28);
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(w, y);
        ctx.stroke();
      }
      const halfGridTicks = Math.ceil(spanHz / tickStep / 2);
      for (let i = -halfGridTicks; i <= halfGridTicks; i++) {
        const offHz = i * tickStep;
        const x = ((offHz + spanHz / 2) / spanHz) * w;
        if (x < 0 || x > w) continue;
        ctx.strokeStyle = offHz === 0 ? ink1(0.16) : ink3(0.20);
        ctx.beginPath();
        ctx.moveTo(Math.round(x) + 0.5, plotTop);
        ctx.lineTo(Math.round(x) + 0.5, plotBottom);
        ctx.stroke();
      }
      ctx.restore();

      const vfo = Number(c.vfoHz);
      const panDb = d.panDb;
      const binsPerHz = d.hzPerPixel > 0 ? 1 / d.hzPerPixel : 0;
      const popOn = enh.popEnabled && floor !== null && panDb !== null && floor.length === panDb.length;

      // Time-averaged power spectrum (EMA in the LINEAR-power domain — the
      // correct domain to average). Edge + occupied-bandwidth measurement runs
      // on this, not the jittery instantaneous frame, so the reported width is
      // the signal's true transmitted bandwidth. The live trace stays
      // instantaneous; only the bandwidth read is averaged.
      if (panDb) {
        const akey = `${d.centerHz}:${d.hzPerPixel}:${panDb.length}`;
        if (avgLin === null || avgLin.length !== panDb.length) {
          avgLin = new Float32Array(panDb.length);
          avgDb = new Float32Array(panDb.length);
          avgKey = '';
        }
        const reset = akey !== avgKey;
        const a = reset ? 1 : AVG_ALPHA;
        const al = avgLin;
        const ad = avgDb!;
        for (let i = 0; i < panDb.length; i++) {
          const lin = Math.pow(10, panDb[i]! / 10);
          al[i] = reset ? lin : al[i]! + a * (lin - al[i]!);
          ad[i] = 10 * Math.log10(al[i]! > 1e-20 ? al[i]! : 1e-20);
        }
        avgKey = akey;
      }

      // Window geometry shared by the trace, floor contour and markers.
      const loHz = vfo - spanHz / 2;
      let binStart = 0;
      let binEnd = 0;
      let fullStartHz = 0;
      if (panDb && binsPerHz > 0) {
        const displayCenter = Number(d.centerHz);
        const fullSpanHz = panDb.length * d.hzPerPixel;
        fullStartHz = displayCenter - fullSpanHz / 2;
        binStart = Math.max(0, Math.floor((loHz - fullStartHz) * binsPerHz));
        binEnd = Math.min(panDb.length, Math.ceil((loHz + spanHz - fullStartHz) * binsPerHz));
      }
      const bins = binEnd - binStart;

      // Dynamic vertical auto-range (non-Pop): scan the visible window, track an
      // EMA-smoothed floor→peak, and map THAT to the plot height. The noise
      // floor hugs the bottom and any signal fills the panel regardless of
      // absolute level — so weak signals and their width are always easy to see.
      // EMA keeps it from jumping frame-to-frame. A minimum span stops a quiet
      // window from amplifying noise into a full-height mess.
      if (panDb && bins > 0 && !popOn) {
        let mn = Infinity;
        let mx = -Infinity;
        for (let i = binStart; i < binEnd; i++) {
          const v = panDb[i]!;
          if (v < mn) mn = v;
          if (v > mx) mx = v;
        }
        if (mn === Infinity) { mn = DB_FLOOR; mx = DB_CEIL; }
        const targetLo = mn - 3;
        const targetHi = Math.max(mx + 5, mn + 18); // ≥18 dB span
        autoLoDb = Number.isNaN(autoLoDb) ? targetLo : autoLoDb + 0.15 * (targetLo - autoLoDb);
        autoHiDb = Number.isNaN(autoHiDb) ? targetHi : autoHiDb + 0.15 * (targetHi - autoHiDb);
      }
      const loDb = Number.isNaN(autoLoDb) ? DB_FLOOR : autoLoDb;
      const hiDb = Number.isNaN(autoHiDb) ? DB_CEIL : autoHiDb;
      const dbSpan = hiDb - loDb > 1 ? hiDb - loDb : 1;

      // Map a dB (auto-ranged) or, in Pop mode, an SNR value to a plot Y.
      const dbToY = (v: number) => {
        const norm = (v - loDb) / dbSpan;
        return plotTop + plotH - Math.max(0, Math.min(1, norm)) * plotH;
      };
      const snrToY = (snr: number) => {
        const norm = snr / POP_RANGE_DB;
        return plotTop + plotH - Math.max(0, Math.min(1, norm)) * plotH;
      };

      if (panDb && bins > 0) {
        // Faint floor contour (non-Pop) so the operator can see the estimated
        // noise floor riding under the trace. In Pop mode the floor IS the
        // baseline, so it collapses to the bottom rail instead — drawn below.
        if (floor !== null && floor.length === panDb.length && !popOn) {
          ctx.strokeStyle = fg2;
          ctx.globalAlpha = 0.35;
          ctx.lineWidth = 1 * dpr;
          ctx.beginPath();
          for (let x = 0; x < w; x++) {
            const b0 = binStart + Math.floor((x * bins) / w);
            const y = dbToY(floor[Math.min(floor.length - 1, b0)] ?? DB_FLOOR);
            if (x === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          }
          ctx.stroke();
          ctx.globalAlpha = 1;
        }

        // Spectrum trace — peak-hold per pixel column. In Pop mode each column
        // plots the strongest SNR above the floor so weak carriers lift; the
        // baseline (SNR≈0) sits on the bottom rail. Ys are recorded into a
        // reused buffer so we can both stroke the line and fill the area below
        // it (modern filled-spectrum look) without re-scanning the bins.
        if (traceY === null || traceY.length !== w) traceY = new Float32Array(w);
        if (traceSm === null || traceSm.length !== w) traceSm = new Float32Array(w);
        const ty = traceY;
        const lastBin = binEnd - 1;
        for (let x = 0; x < w; x++) {
          const b0 = binStart + Math.floor((x * bins) / w);
          const b1 = binStart + Math.floor(((x + 1) * bins) / w);
          // Column max preserves narrow carriers; a centre-sample interpolation
          // keeps the curve smooth (granular) when zoomed in past one bin/pixel.
          const fb = binStart + ((x + 0.5) / w) * bins;
          const i0 = Math.max(binStart, Math.min(lastBin, Math.floor(fb)));
          const i1 = Math.min(lastBin, i0 + 1);
          const frac = fb - i0;
          let y: number;
          if (popOn) {
            let mxSnr = -Infinity;
            for (let i = b0; i < b1; i++) {
              const snr = (panDb[i] ?? DB_FLOOR) - (floor![i] ?? DB_FLOOR);
              if (snr > mxSnr) mxSnr = snr;
            }
            const s0 = (panDb[i0] ?? DB_FLOOR) - (floor![i0] ?? DB_FLOOR);
            const s1 = (panDb[i1] ?? DB_FLOOR) - (floor![i1] ?? DB_FLOOR);
            const interp = s0 * (1 - frac) + s1 * frac;
            const snr = mxSnr === -Infinity ? interp : (bins >= w ? 0.6 * mxSnr + 0.4 * interp : interp);
            y = snrToY(Math.max(0, snr));
          } else {
            let peak = -Infinity;
            for (let i = b0; i < b1; i++) {
              const v = panDb[i] ?? DB_FLOOR;
              if (v > peak) peak = v;
            }
            const interp = (panDb[i0] ?? DB_FLOOR) * (1 - frac) + (panDb[i1] ?? DB_FLOOR) * frac;
            // Zoomed out (many bins/pixel) keep peaks; zoomed in, follow the
            // smooth interpolation so the trace reads as a continuous curve.
            const v = peak === -Infinity ? interp : (bins >= w ? 0.6 * peak + 0.4 * interp : interp);
            y = dbToY(v);
          }
          ty[x] = y;
        }

        // De-comb: 3-tap moving average so noise reads as a soft band, not a
        // picket fence, while real signal humps survive.
        const sm = traceSm;
        for (let x = 0; x < w; x++) {
          const a = ty[x === 0 ? 0 : x - 1]!;
          const b = ty[x]!;
          const c = ty[x === w - 1 ? w - 1 : x + 1]!;
          sm[x] = (a + b + c) / 3;
        }

        const baseY = plotBottom;

        // Heat trace — each column is filled from the baseline up to the trace
        // with the signal colour, its alpha scaled by how high the trace sits in
        // the (auto-ranged) panel. Strong signals glow bright; noise stays a dim
        // wash. This is the modern panadapter look applied to the filter window.
        for (let x = 0; x < w; x++) {
          const top = sm[x]!;
          const normH = (baseY - top) / plotH; // 0 at floor, 1 at panel top
          const a = 0.10 + 0.78 * Math.max(0, Math.min(1, normH)) ** 1.35;
          ctx.fillStyle = signal(a);
          ctx.fillRect(x, top, 1, baseY - top);
        }

        // Peak-hold decay envelope — a slow-falling ghost that remembers recent
        // maxima so transient / weak signals leave a visible trail. Reset when
        // the frequency window changes (held peaks would otherwise smear).
        if (peakHoldY === null || peakHoldY.length !== w) peakHoldY = new Float32Array(w);
        const ph = peakHoldY;
        const phKey = `${c.vfoHz}:${spanHz}:${w}`;
        const decay = PEAKHOLD_DECAY_PX * dpr;
        if (phKey !== peakHoldKey) {
          peakHoldKey = phKey;
          for (let x = 0; x < w; x++) ph[x] = sm[x]!;
        } else {
          for (let x = 0; x < w; x++) {
            const decayed = ph[x]! + decay; // larger Y = lower level
            ph[x] = decayed < sm[x]! ? decayed : sm[x]!; // hold the higher (smaller Y)
          }
        }
        ctx.strokeStyle = signal(0.5);
        ctx.lineWidth = 1 * dpr;
        ctx.beginPath();
        for (let x = 0; x < w; x++) {
          if (x === 0) ctx.moveTo(x, ph[x]!); else ctx.lineTo(x, ph[x]!);
        }
        ctx.stroke();

        // Live trace line on top — bright, crisp.
        ctx.lineWidth = 1.25 * dpr;
        ctx.strokeStyle = signal(0.95);
        ctx.beginPath();
        for (let x = 0; x < w; x++) {
          if (x === 0) ctx.moveTo(x, sm[x]!); else ctx.lineTo(x, sm[x]!);
        }
        ctx.stroke();

        // Pop-mode floor baseline: a faint flat rail at SNR=0 with an "NF" tag.
        if (popOn) {
          const yBase = snrToY(0);
          ctx.strokeStyle = fg2;
          ctx.globalAlpha = 0.3;
          ctx.lineWidth = 1 * dpr;
          ctx.beginPath();
          ctx.moveTo(0, yBase + 0.5);
          ctx.lineTo(w, yBase + 0.5);
          ctx.stroke();
          ctx.globalAlpha = 0.55;
          ctx.fillStyle = fg2;
          ctx.font = `${Math.round(8 * dpr)}px "SFMono-Regular", ui-monospace, monospace`;
          ctx.textBaseline = 'bottom';
          ctx.textAlign = 'start';
          ctx.fillText('NF', Math.round(3 * dpr), yBase - Math.round(1 * dpr));
          ctx.globalAlpha = 1;
        }
      }

      // VFO center line — subtle, in the plot area only.
      ctx.strokeStyle = COL_VFO_CENTER;
      ctx.lineWidth = 1 * dpr;
      ctx.beginPath();
      ctx.moveTo(w / 2, plotTop);
      ctx.lineTo(w / 2, plotTop + plotH);
      ctx.stroke();

      // Passband — accent-tinted filled rectangle between two glowing cut
      // walls, with a bright flat top and grab handles. (Departs from the older
      // neutral-silver "not a cyan box" treatment — red-light, see CLAUDE.md.)
      const passLeftPx = ((c.filterLowHz + spanHz / 2) / spanHz) * w;
      const passRightPx = ((c.filterHighHz + spanHz / 2) / spanHz) * w;
      const onScreen = passRightPx > 0 && passLeftPx < w;

      // Signal-edge markers — for each detected carrier in the window, measure
      // where it sinks into the noise (its visible extent) and make those edges
      // UNMISTAKABLE: an occupied-band wash, bright full-height edge guides with
      // glow, inward carets at the baseline, and a width label. The carrier the
      // filter is sitting on is drawn in accent; the rest in the signal colour.
      // Widths are tracked/held over time so the labels read as instruments,
      // not instantaneous detector noise.
      if (floor !== null && panDb && binsPerHz > 0) {
        const dCenter = Number(d.centerHz);
        const baseY = plotBottom;
        const half = panDb.length / 2;
        const nextBracketKey = `${d.centerHz}:${d.hzPerPixel}:${panDb.length}:${spanHz}`;
        if (nextBracketKey !== bracketKey) {
          bracketKey = nextBracketKey;
          bracketTracks = [];
        } else {
          bracketTracks = bracketTracks.filter((t) => now - t.lastSeenAt <= BRACKET_TRACK_TTL_MS);
        }
        // Measure on the time-averaged spectrum once it exists (stable width);
        // fall back to the live frame for the very first frames.
        const measSpec = avgDb && avgDb.length === panDb.length ? avgDb : panDb;
        const peaks = detectPeaks(panDb, dCenter, d.hzPerPixel)
          .filter((p) => p.snrDb >= BRACKET_MIN_SNR_DB)
          .slice(0, BRACKET_MAX); // detectPeaks returns strongest-first
        const drawnBands: Array<{ loPx: number; hiPx: number }> = [];
        const drawnLabels: Array<{ left: number; right: number; top: number; bottom: number }> = [];
        let labelCount = 0;
        let nodeCount = 0;
        const placeChip = (
          centerX: number,
          baseBy: number,
          chipW: number,
          chipH: number,
        ): { by: number; rect: { left: number; right: number; top: number; bottom: number } } | null => {
          const laneGap = Math.max(2, Math.round(2 * dpr));
          const lx = Math.max(chipW / 2 + 2, Math.min(w - chipW / 2 - 2, centerX));
          for (let lane = 0; lane < 3; lane++) {
            const by = Math.max(plotTop + chipH + 1, baseBy - lane * (chipH + laneGap));
            const rect = {
              left: lx - chipW / 2,
              right: lx + chipW / 2,
              top: by - chipH,
              bottom: by,
            };
            if (!drawnLabels.some((r) => rectsOverlap(rect, r))) return { by, rect };
          }
          return null;
        };
        for (const p of peaks) {
          const xC = ((p.hz - loHz) / spanHz) * w;
          if (xC < 0 || xC > w) continue;
          // Energy extent on the AVERAGED spectrum (the confidence-aware, gap-
          // tolerant walk snap uses), then refine to the ITU 99%-power occupied
          // bandwidth — the signal's true transmitted width, not noisy skirts.
          const ext = signalExtentHz(measSpec, dCenter, d.hzPerPixel, p.hz, EDGE_ANCHOR_HZ);
          if (!ext) continue;
          let loEdgeHz = ext.loHz;
          let hiEdgeHz = ext.hiHz;
          if (avgLin && avgLin.length === panDb.length) {
            const eLo = Math.max(0, Math.round((ext.loHz - dCenter) / d.hzPerPixel + half));
            const eHi = Math.min(panDb.length - 1, Math.round((ext.hiHz - dCenter) / d.hzPerPixel + half));
            const [obLo, obHi] = occupiedBandBins(avgLin, floor, eLo, eHi, OBW_FRACTION);
            loEdgeHz = dCenter + (obLo - half) * d.hzPerPixel;
            hiEdgeHz = dCenter + (obHi - half) * d.hzPerPixel;
          }
          const track = smoothedBracketMeasurement(bracketTracks, now, ext.crestHz, loEdgeHz, hiEdgeHz, p.snrDb);
          loEdgeHz = track.loHz;
          hiEdgeHz = track.hiHz;
          if (hiEdgeHz < loEdgeHz) {
            const swap = loEdgeHz;
            loEdgeHz = hiEdgeHz;
            hiEdgeHz = swap;
          }
          const xL = ((loEdgeHz - loHz) / spanHz) * w;
          const xR = ((hiEdgeHz - loHz) / spanHz) * w;
          if (drawnBands.some((r) => rangeOverlapRatio(xL, xR, r.loPx, r.hiPx) >= BRACKET_OVERLAP_RATIO)) {
            continue;
          }
          drawnBands.push({ loPx: xL, hiPx: xR });
          const bwHz = track.labelBwHz;
          const markerCrestHz = track.heldCrestHz;
          const markerCenterX = ((markerCrestHz - loHz) / spanHz) * w;
          const inBand = markerCrestHz - vfo >= c.filterLowHz && markerCrestHz - vfo <= c.filterHighHz;
          const col = inBand ? accent : signal;
          const prominence = Math.max(0, Math.min(1, (track.heldSnrDb - BRACKET_MIN_SNR_DB) / 30));
          const guideAlpha = inBand ? 0.95 : 0.34 + 0.34 * prominence;

          // 1) Occupied-band wash — brighten the detected extent above the heat
          //    so the band itself reads as a distinct block.
          if (traceSm) {
            ctx.fillStyle = col(inBand ? 0.16 : 0.07 + 0.06 * prominence);
            const xa = Math.max(0, Math.round(xL));
            const xb = Math.min(w, Math.round(xR));
            for (let x = xa; x < xb; x++) ctx.fillRect(x, traceSm[x]!, 1, baseY - traceSm[x]!);
          }

          // 2) Bright full-height edge guides with glow — the crisp left/right
          //    boundaries the eye locks onto.
          ctx.save();
          ctx.shadowColor = col(inBand ? 0.8 : 0.25 + 0.35 * prominence);
          ctx.shadowBlur = Math.round((inBand ? 6 : 3) * dpr);
          ctx.strokeStyle = col(guideAlpha);
          ctx.lineWidth = Math.max(1.5, 1.5 * dpr);
          for (const ex of [xL, xR]) {
            if (ex < 0 || ex > w) continue;
            ctx.beginPath();
            ctx.moveTo(Math.round(ex) + 0.5, plotTop);
            ctx.lineTo(Math.round(ex) + 0.5, baseY);
            ctx.stroke();
          }
          ctx.restore();

          // 3) Inward carets at the baseline so the edges point at the band.
          const cz = Math.round(4 * dpr);
          ctx.fillStyle = col(inBand ? 1 : 0.52 + 0.28 * prominence);
          if (xL >= 0 && xL <= w) {
            ctx.beginPath();
            ctx.moveTo(xL, baseY);
            ctx.lineTo(xL + cz, baseY - cz);
            ctx.lineTo(xL + cz, baseY + cz);
            ctx.closePath();
            ctx.fill();
          }
          if (xR >= 0 && xR <= w) {
            ctx.beginPath();
            ctx.moveTo(xR, baseY);
            ctx.lineTo(xR - cz, baseY - cz);
            ctx.lineTo(xR - cz, baseY + cz);
            ctx.closePath();
            ctx.fill();
          }

          // 4) EQ/analyzer node: the fast realtime readout is now the most
          //    prominent frequency, not a twitchy bandwidth number.
          const crestY = traceSm
            ? traceSm[Math.max(0, Math.min(w - 1, Math.round(markerCenterX)))]!
            : plotTop;
          if (nodeCount < EQ_NODE_MAX && track.confidence >= EQ_NODE_MIN_CONFIDENCE) {
            const rank = nodeCount + 1;
            const nodeY = Math.max(plotTop + Math.round(8 * dpr), Math.min(plotBottom - Math.round(8 * dpr), crestY));
            const nodeR = Math.max(4, Math.round(5 * dpr));
            ctx.save();
            ctx.strokeStyle = col(0.18 + 0.35 * prominence);
            ctx.lineWidth = Math.max(1, 1 * dpr);
            ctx.setLineDash([Math.max(2, Math.round(2 * dpr)), Math.max(3, Math.round(4 * dpr))]);
            ctx.beginPath();
            ctx.moveTo(Math.round(markerCenterX) + 0.5, nodeY + nodeR);
            ctx.lineTo(Math.round(markerCenterX) + 0.5, baseY - Math.round(3 * dpr));
            ctx.stroke();
            ctx.setLineDash([]);
            ctx.shadowColor = col(0.55 + 0.35 * prominence);
            ctx.shadowBlur = Math.round((4 + 4 * prominence) * dpr);
            ctx.fillStyle = 'rgba(12, 14, 18, 0.74)';
            ctx.strokeStyle = col(0.72 + 0.28 * prominence);
            ctx.lineWidth = Math.max(1.5, 1.5 * dpr);
            ctx.beginPath();
            ctx.arc(markerCenterX, nodeY, nodeR, 0, Math.PI * 2);
            ctx.fill();
            ctx.stroke();
            ctx.shadowBlur = 0;
            ctx.fillStyle = col(1);
            ctx.font = `800 ${Math.round(7 * dpr)}px "SFMono-Regular", ui-monospace, monospace`;
            ctx.textBaseline = 'middle';
            ctx.textAlign = 'center';
            ctx.fillText(String(rank), markerCenterX, nodeY + Math.round(0.5 * dpr));
            ctx.restore();

            const nodeLabel = `P${rank} ${formatCutOffset(markerCrestHz - vfo)}`;
            ctx.font = `700 ${Math.round(8 * dpr)}px "SFMono-Regular", ui-monospace, monospace`;
            ctx.textBaseline = 'bottom';
            ctx.textAlign = 'center';
            const nodeLw = ctx.measureText(nodeLabel).width;
            const nodePadX = Math.round(4 * dpr);
            const nodeChipW = nodeLw + nodePadX * 2;
            const nodeChipH = Math.round(12 * dpr);
            const nodePlaced = placeChip(markerCenterX, nodeY - nodeR - Math.round(3 * dpr), nodeChipW, nodeChipH);
            if (nodePlaced) {
              ctx.fillStyle = 'rgba(12, 14, 18, 0.58)';
              ctx.fillRect(nodePlaced.rect.left, nodePlaced.rect.top, nodeChipW, nodeChipH);
              ctx.fillStyle = col(0.82 + 0.18 * prominence);
              ctx.fillText(nodeLabel, (nodePlaced.rect.left + nodePlaced.rect.right) / 2, nodePlaced.by - Math.round(2 * dpr));
              drawnLabels.push(nodePlaced.rect);
            }
            ctx.textAlign = 'start';
            ctx.textBaseline = 'alphabetic';
            nodeCount++;
          }

          // 5) Slower active bandwidth lock. Only the in-passband signal gets
          //    this number; nearby signals are interpreted through the P-nodes.
          const canLabel = inBand && labelCount < BRACKET_LABEL_MAX && track.confidence >= BRACKET_LABEL_MIN_CONFIDENCE;
          if (canLabel) {
            const label = `BW LOCK ${formatFilterWidth(0, Math.round(bwHz))}`;
            ctx.font = `700 ${Math.round(9 * dpr)}px "SFMono-Regular", ui-monospace, monospace`;
            ctx.textBaseline = 'bottom';
            ctx.textAlign = 'center';
            const lw = ctx.measureText(label).width;
            const chipPadX = Math.round(5 * dpr);
            const chipW = lw + chipPadX * 2;
            const chipH = Math.round(14 * dpr);
            const baseBy = Math.max(plotTop + chipH + 1, crestY - Math.round(6 * dpr));
            const placed = placeChip((xL + xR) / 2, baseBy, chipW, chipH);
            if (placed) {
              const chipAlpha = 0.46 + 0.24 * track.confidence;
              ctx.fillStyle = `rgba(12, 14, 18, ${chipAlpha.toFixed(3)})`;
              ctx.fillRect(placed.rect.left, placed.rect.top, chipW, chipH);
              ctx.fillStyle = col(0.22 + 0.58 * track.confidence);
              ctx.fillRect(
                placed.rect.left,
                placed.rect.bottom - Math.max(1, Math.round(2 * dpr)),
                chipW * track.confidence,
                Math.max(1, Math.round(2 * dpr)),
              );
              ctx.fillStyle = col(0.75 + 0.25 * track.confidence);
              ctx.fillText(label, (placed.rect.left + placed.rect.right) / 2, placed.by - Math.round(3 * dpr));
              drawnLabels.push(placed.rect);
              labelCount++;
            }
            ctx.textAlign = 'start';
            ctx.textBaseline = 'alphabetic';
          }
        }
      }

      if (onScreen) {
        const pbTop = plotTop + Math.round(4 * dpr);
        const pbBottom = plotBottom;
        const Lx = passLeftPx;
        const Rx = passRightPx;
        // Clamp the fill rectangle to the canvas (edges can sit off-screen).
        const fillL = Math.max(0, Lx);
        const fillR = Math.min(w, Rx);

        // 1) Focus mask + transition skirts. The accepted passband stays clear
        //    while out-of-band spectrum is visually pushed back.
        if (fillL > 0) {
          const leftScrim = ctx.createLinearGradient(0, 0, fillL, 0);
          leftScrim.addColorStop(0, 'rgba(0, 0, 0, 0.46)');
          leftScrim.addColorStop(1, 'rgba(0, 0, 0, 0.22)');
          ctx.fillStyle = leftScrim;
          ctx.fillRect(0, plotTop, fillL, plotH);
        }
        if (fillR < w) {
          const rightScrim = ctx.createLinearGradient(fillR, 0, w, 0);
          rightScrim.addColorStop(0, 'rgba(0, 0, 0, 0.22)');
          rightScrim.addColorStop(1, 'rgba(0, 0, 0, 0.46)');
          ctx.fillStyle = rightScrim;
          ctx.fillRect(fillR, plotTop, w - fillR, plotH);
        }
        const skirtPx = Math.max(Math.round(10 * dpr), Math.min(Math.round(34 * dpr), Math.round((fillR - fillL) * 0.18)));
        if (Lx > 0 && Lx < w) {
          const leftSkirtX = Math.max(0, Lx - skirtPx);
          const leftSkirt = ctx.createLinearGradient(leftSkirtX, 0, Math.max(leftSkirtX + 1, Lx), 0);
          leftSkirt.addColorStop(0, accent(0.00));
          leftSkirt.addColorStop(1, accent(0.09));
          ctx.fillStyle = leftSkirt;
          ctx.fillRect(leftSkirtX, pbTop, Math.min(w, Lx) - leftSkirtX, pbBottom - pbTop);
        }
        if (Rx > 0 && Rx < w) {
          const rightSkirtR = Math.min(w, Rx + skirtPx);
          const rightSkirt = ctx.createLinearGradient(Math.min(w, Rx), 0, rightSkirtR, 0);
          rightSkirt.addColorStop(0, accent(0.09));
          rightSkirt.addColorStop(1, accent(0.00));
          ctx.fillStyle = rightSkirt;
          ctx.fillRect(Math.max(0, Rx), pbTop, rightSkirtR - Math.max(0, Rx), pbBottom - pbTop);
        }

        // 2) Transparent passband glass. The selected bandwidth should frame
        //    spectrum detail, not cover it; precision comes from the rail/walls.
        const pbGrad = ctx.createLinearGradient(0, pbTop, 0, pbBottom);
        pbGrad.addColorStop(0.0, accent(0.12));
        pbGrad.addColorStop(0.34, accent(0.055));
        pbGrad.addColorStop(0.72, accent(0.025));
        pbGrad.addColorStop(1.0, accent(0.00));
        ctx.fillStyle = pbGrad;
        ctx.fillRect(fillL, pbTop, Math.max(0, fillR - fillL), pbBottom - pbTop);
        if (fillR > fillL) {
          ctx.save();
          ctx.beginPath();
          ctx.rect(fillL, pbTop, fillR - fillL, pbBottom - pbTop);
          ctx.clip();
          const glow = ctx.createRadialGradient((Lx + Rx) / 2, pbTop, 0, (Lx + Rx) / 2, pbTop, Math.max(1, (fillR - fillL) * 0.65));
          glow.addColorStop(0, accent(0.07));
          glow.addColorStop(0.58, accent(0.02));
          glow.addColorStop(1, accent(0));
          ctx.fillStyle = glow;
          ctx.fillRect(fillL, pbTop, fillR - fillL, pbBottom - pbTop);
          ctx.restore();
        }

        // 3) Bright glowing flat top — a single horizontal line spanning the
        //    passband (the filter's flat top), with no angled tails at the ends.
        ctx.save();
        ctx.shadowColor = accent(0.75);
        ctx.shadowBlur = Math.round(10 * dpr);
        ctx.strokeStyle = accent(0.95);
        ctx.lineWidth = Math.max(1.5, 1.5 * dpr);
        ctx.beginPath();
        ctx.moveTo(Math.max(0, Lx), pbTop + 0.5);
        ctx.lineTo(Math.min(w, Rx), pbTop + 0.5);
        ctx.stroke();
        ctx.restore();

        // Width ruler, tucked below the top rail so the passband reads as a
        // measured object even when the DOM width pill is over the trace.
        const rulerY = pbTop + Math.round(11 * dpr);
        const rulerInset = Math.round(11 * dpr);
        const rulerL = Math.max(0, Lx + rulerInset);
        const rulerR = Math.min(w, Rx - rulerInset);
        if (rulerR - rulerL > Math.round(24 * dpr)) {
          ctx.strokeStyle = accent(0.42);
          ctx.lineWidth = 1 * dpr;
          ctx.beginPath();
          ctx.moveTo(rulerL, rulerY + 0.5);
          ctx.lineTo(rulerR, rulerY + 0.5);
          ctx.stroke();
          ctx.strokeStyle = accent(0.72);
          for (const x of [rulerL, (rulerL + rulerR) / 2, rulerR]) {
            ctx.beginPath();
            ctx.moveTo(Math.round(x) + 0.5, rulerY - Math.round(3 * dpr));
            ctx.lineTo(Math.round(x) + 0.5, rulerY + Math.round(3 * dpr));
            ctx.stroke();
          }
        }

        // 4) Exact cut walls — full-height accent lines mark the precise LOW/
        //    HIGH cut and close the passband rectangle's sides.
        ctx.save();
        ctx.shadowColor = accent(0.6);
        ctx.shadowBlur = Math.round(6 * dpr);
        ctx.strokeStyle = accent(0.85);
        ctx.lineWidth = Math.max(1, 1 * dpr);
        for (const wx of [Lx, Rx]) {
          ctx.beginPath();
          ctx.moveTo(Math.round(wx) + 0.5, pbTop);
          ctx.lineTo(Math.round(wx) + 0.5, pbBottom);
          ctx.stroke();
        }
        ctx.restore();

        // 5) Grab handles — rounded pills centred on each wall with two grip
        //    lines, so the drag affordance is obvious. The hovered edge brightens.
        const hoverEdge = hoverEdgeRef.current;
        const handleW = Math.round(7 * dpr);
        const handleH = Math.round(20 * dpr);
        const handleY = pbTop + (pbBottom - pbTop) / 2 - handleH / 2;
        const drawHandle = (wx: number, hot: boolean) => {
          const x = Math.round(wx) - handleW / 2;
          ctx.save();
          ctx.shadowColor = hot ? accent(0.72) : 'rgba(0, 0, 0, 0.55)';
          ctx.shadowBlur = hot ? Math.round(9 * dpr) : Math.round(4 * dpr);
          const handleGrad = ctx.createLinearGradient(0, handleY, 0, handleY + handleH);
          handleGrad.addColorStop(0, hot ? accent(1.0) : accent(0.88));
          handleGrad.addColorStop(0.48, hot ? accent(0.84) : accent(0.66));
          handleGrad.addColorStop(1, hot ? accent(0.72) : accent(0.52));
          ctx.fillStyle = handleGrad;
          const r = Math.round(2 * dpr);
          ctx.beginPath();
          ctx.roundRect(x, handleY, handleW, handleH, r);
          ctx.fill();
          ctx.strokeStyle = ink0(hot ? 0.70 : 0.42);
          ctx.lineWidth = 1 * dpr;
          ctx.stroke();
          ctx.restore();
          // Grip lines.
          ctx.strokeStyle = ink0(0.86);
          ctx.lineWidth = 1 * dpr;
          const gx = Math.round(wx);
          const g1 = handleY + handleH * 0.34;
          const g2 = handleY + handleH * 0.66;
          ctx.beginPath();
          ctx.moveTo(gx - Math.round(1.5 * dpr) + 0.5, g1);
          ctx.lineTo(gx - Math.round(1.5 * dpr) + 0.5, g2);
          ctx.moveTo(gx + Math.round(1.5 * dpr) + 0.5, g1);
          ctx.lineTo(gx + Math.round(1.5 * dpr) + 0.5, g2);
          ctx.stroke();
        };
        drawHandle(Lx, hoverEdge === 'lo');
        drawHandle(Rx, hoverEdge === 'hi');

        // LOW CUT / HIGH CUT callouts. Key (letter-spaced, muted) stacked
        // above value (bold, brighter) in the reserved top band. A hairline
        // connector ties each label to its wall top. Labels center on the
        // wall X and clamp to canvas edges so they never clip.
        const keyFontPx = Math.round(8 * dpr);
        const valFontPx = Math.round(10.5 * dpr);
        const keyFont = `600 ${keyFontPx}px "SFMono-Regular", ui-monospace, monospace`;
        const valFont = `600 ${valFontPx}px "SFMono-Regular", ui-monospace, monospace`;
        const padX = Math.round(4 * dpr);
        const keyY = Math.round(1 * dpr);
        const valY = keyY + keyFontPx + Math.round(1 * dpr);

        const drawCallout = (wallX: number, side: 'lo' | 'hi', value: string) => {
          if (wallX < 0 || wallX > w) return;
          const key = side === 'lo' ? 'LOW CUT' : 'HIGH CUT';

          // Measure both lines to find clamp bounds.
          ctx.font = valFont;
          const valW = ctx.measureText(value).width;
          ctx.letterSpacing = '0.15em';
          ctx.font = keyFont;
          const keyW = ctx.measureText(key).width;
          const halfMax = Math.max(valW, keyW) / 2;
          const cx = Math.max(halfMax + padX, Math.min(w - halfMax - padX, wallX));
          const chipPadX = Math.round(5 * dpr);
          const chipH = valY + valFontPx + Math.round(3 * dpr);
          const chipW = Math.min(w - padX * 2, halfMax * 2 + chipPadX * 2);
          const chipX = Math.max(padX, Math.min(w - chipW - padX, cx - chipW / 2));
          const chipCx = chipX + chipW / 2;

          ctx.save();
          ctx.fillStyle = 'rgba(7, 9, 13, 0.68)';
          ctx.strokeStyle = accent(0.20);
          ctx.lineWidth = 1 * dpr;
          ctx.beginPath();
          ctx.roundRect(chipX, 0, chipW, chipH, Math.round(3 * dpr));
          ctx.fill();
          ctx.stroke();
          ctx.restore();

          // Hairline from label chip down to the wall top.
          ctx.strokeStyle = COL_CUT_TICK;
          ctx.lineWidth = 1 * dpr;
          ctx.beginPath();
          ctx.moveTo(Math.round(wallX) + 0.5, chipH + Math.round(1 * dpr));
          ctx.lineTo(Math.round(wallX) + 0.5, pbTop);
          ctx.stroke();

          // Key (top, muted, letter-spaced).
          ctx.textBaseline = 'top';
          ctx.textAlign = 'center';
          ctx.fillStyle = colCutKey;
          ctx.fillText(key, chipCx, keyY);

          // Value (bold, brighter, no letter-spacing).
          ctx.letterSpacing = '0px';
          ctx.font = valFont;
          ctx.fillStyle = colCutVal;
          ctx.fillText(value, chipCx, valY);
        };

        drawCallout(passLeftPx, 'lo', formatCutOffset(c.filterLowHz));
        drawCallout(passRightPx, 'hi', formatCutOffset(c.filterHighHz));

        // Reset text state for subsequent draws (x-axis labels assume start).
        ctx.textAlign = 'start';
        ctx.letterSpacing = '0px';
      }

      // X-axis tick labels. One label every tickStep (scaled by span), centered
      // on the VFO. VFO sits at the middle tick.
      ctx.fillStyle = colTickLabel;
      ctx.font = `${Math.round(9.5 * dpr)}px "SFMono-Regular", ui-monospace, monospace`;
      ctx.textBaseline = 'middle';
      const labelY = plotTop + plotH + Math.round(axisH / 2);
      const nTicks = Math.floor(spanHz / tickStep) + 1; // inclusive both ends
      const tickOffsets: number[] = [];
      // Center-out so VFO tick is guaranteed; symmetric ticks either side.
      const halfTicks = Math.floor(nTicks / 2);
      for (let i = -halfTicks; i <= halfTicks; i++) tickOffsets.push(i * tickStep);
      tickOffsets.forEach((offHz) => {
        const absHz = vfo + offHz;
        const xPx = ((offHz + spanHz / 2) / spanHz) * w;
        if (xPx < 0 || xPx > w) return;
        const text = formatTickMhz(absHz);
        const m = ctx.measureText(text);
        // Brighter fill on the VFO (center) tick, muted on the rest.
        ctx.fillStyle = offHz === 0 ? colTickLabelCenter : colTickLabel;
        ctx.fillText(text, Math.max(2, Math.min(w - m.width - 2, xPx - m.width / 2)), labelY);
      });
    };

    // Allow the imperative handlers (wheel zoom) to force a redraw even though
    // nothing in the stores changed.
    const requestRedraw = () => {
      lastSeq = null;
      if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
    };
    redrawRef.current = requestRedraw;

    const unsubDisplay = useDisplayStore.subscribe(() => {
      if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
    });
    const unsubConn = useConnectionStore.subscribe((s, p) => {
      if (
        s.filterLowHz !== p.filterLowHz ||
        s.filterHighHz !== p.filterHighHz ||
        s.vfoHz !== p.vfoHz
      ) {
        requestRedraw();
      }
    });
    const unsubTheme = useThemeStore.subscribe((s, p) => {
      if (s.theme !== p.theme || s.overrides !== p.overrides) requestRedraw();
    });
    // Toggling Pop/Snap (or the marker colour) changes what we draw even when
    // the spectrum frame is unchanged.
    const unsubEnhance = useSignalEnhanceStore.subscribe((s, p) => {
      if (
        s.popEnabled !== p.popEnabled ||
        s.snapEnabled !== p.snapEnabled ||
        s.popFloorDb !== p.popFloorDb ||
        s.popSpanDb !== p.popSpanDb ||
        s.popGamma !== p.popGamma ||
        s.coherenceHoldGate !== p.coherenceHoldGate ||
        s.coherenceBoostDb !== p.coherenceBoostDb ||
        s.ridgeBoost !== p.ridgeBoost ||
        s.ridgeMaxBoostDb !== p.ridgeMaxBoostDb ||
        s.snapMinSnrDb !== p.snapMinSnrDb ||
        s.peakMinSnrDb !== p.peakMinSnrDb
      ) requestRedraw();
    });
    const unsubSettings = useDisplaySettingsStore.subscribe((s, p) => {
      if (s.rxTraceColor !== p.rxTraceColor) requestRedraw();
    });

    // Scroll wheel — granular fine-tune. Registered natively (not via React's
    // onWheel, which is passive on the root and silently drops preventDefault),
    // so the gesture adjusts the filter without scrolling the page. Ctrl/⌘+wheel
    // zooms the visible span; otherwise nudge the hovered edge (or the whole
    // passband when not over an edge) by the per-mode step, ×10 with Shift.
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const dir = e.deltaY > 0 ? 1 : -1;

      if (e.ctrlKey || e.metaKey) {
        const factor = dir > 0 ? 1.18 : 1 / 1.18;
        const next = Math.round(Math.max(MIN_SPAN_HZ, Math.min(MAX_SPAN_HZ, spanHzRef.current * factor)));
        if (next !== spanHzRef.current) {
          spanHzRef.current = next;
          setSpanTick((t) => t + 1);
          requestRedraw();
        }
        return;
      }

      const c = useConnectionStore.getState();
      const step = nudgeStepHz(c.mode) * (e.shiftKey ? 10 : 1) * dir;
      const edge = hoverEdgeRef.current;
      let lo = c.filterLowHz;
      let hi = c.filterHighHz;
      if (edge === 'lo') {
        lo = Math.min(c.filterHighHz - 50, c.filterLowHz + step);
      } else if (edge === 'hi') {
        hi = Math.max(c.filterLowHz + 50, c.filterHighHz + step);
      } else {
        // Inside / no edge → symmetric width change about the passband centre.
        const center = (c.filterLowHz + c.filterHighHz) / 2;
        const halfW = Math.max(25, Math.abs(c.filterHighHz - c.filterLowHz) / 2 + step);
        lo = Math.round(center - halfW);
        hi = Math.round(center + halfW);
      }
      if (hi <= lo + 50) return;
      const slot = presetIsFixed(c.filterPresetName) || !c.filterPresetName ? 'VAR1' : c.filterPresetName;
      useConnectionStore.setState({ filterLowHz: lo, filterHighHz: hi, filterPresetName: slot });
      setFilter(lo, hi, slot).then(c.applyState).catch(() => {});
    };
    canvas.addEventListener('wheel', onWheel, { passive: false });

    const ro = new ResizeObserver(() => requestRedraw());
    ro.observe(canvas);

    rafHandle = requestAnimationFrame(draw);
    return () => {
      if (rafHandle !== 0) cancelAnimationFrame(rafHandle);
      redrawRef.current = null;
      unsubDisplay();
      unsubConn();
      unsubTheme();
      unsubEnhance();
      unsubSettings();
      canvas.removeEventListener('wheel', onWheel);
      ro.disconnect();
      releaseFrameConsumer();
      releaseEstimator();
    };
  }, []);

  const flushPending = () => {
    const d = dragRef.current;
    if (!d) return;
    d.flushTimer = null;
    d.lastWriteAt = performance.now();
    setFilter(d.pendingLo, d.pendingHi, d.activeSlot).catch(() => {});
  };

  const schedule = () => {
    const d = dragRef.current;
    if (!d) return;
    const now = performance.now();
    const elapsed = now - d.lastWriteAt;
    if (elapsed >= DRAG_MIN_INTERVAL_MS) {
      flushPending();
    } else if (d.flushTimer == null) {
      d.flushTimer = window.setTimeout(flushPending, DRAG_MIN_INTERVAL_MS - elapsed);
    }
  };

  // Fit-to-signal: a click on bare spectrum that lands on a detected carrier
  // snaps the passband to that carrier's measured energy extent (signalExtentHz,
  // the same edge walk snap uses) plus a little margin. Returns true if it
  // fitted, so the caller skips starting a drag.
  const tryFitToSignal = (relX: number, rectW: number, spanHz: number): boolean => {
    const d = useDisplayStore.getState();
    if (!d.panDb || d.hzPerPixel <= 0 || getNoiseFloor() === null) return false;
    const c = useConnectionStore.getState();
    const vfo = Number(c.vfoHz);
    const winLoHz = vfo - spanHz / 2;
    const dCenter = Number(d.centerHz);
    const peaks = detectPeaks(d.panDb, dCenter, d.hzPerPixel).filter((p) => p.snrDb >= BRACKET_MIN_SNR_DB);
    let best: DetectedPeak | null = null;
    let bestDist = FIT_HIT_PX;
    for (const p of peaks) {
      const x = ((p.hz - winLoHz) / spanHz) * rectW;
      const dist = Math.abs(x - relX);
      if (dist < bestDist) { bestDist = dist; best = p; }
    }
    if (!best) return false;
    const ext = signalExtentHz(d.panDb, dCenter, d.hzPerPixel, best.hz, EDGE_ANCHOR_HZ);
    if (!ext) return false;
    // Keep the fit on the active mode's sideband — a signal on the wrong side of
    // the carrier is unreachable here without retuning, so bail rather than flip
    // the passband to the opposite sideband.
    const fitted = fitPassbandForMode(c.mode, ext.loHz - vfo, ext.hiHz - vfo, FIT_MARGIN_HZ);
    if (!fitted) return false;
    const { low, high } = fitted;
    const slot = presetIsFixed(c.filterPresetName) || !c.filterPresetName ? 'VAR1' : c.filterPresetName;
    useConnectionStore.setState({ filterLowHz: low, filterHighHz: high, filterPresetName: slot });
    setFilter(low, high, slot).then(c.applyState).catch(() => {});
    return true;
  };

  const onPointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (e.button !== 0) return;
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    if (rect.width <= 0) return;

    const spanHz = spanHzRef.current;
    const c = useConnectionStore.getState();
    const passLeftPx = ((c.filterLowHz + spanHz / 2) / spanHz) * rect.width;
    const passRightPx = ((c.filterHighHz + spanHz / 2) / spanHz) * rect.width;
    const relX = e.clientX - rect.left;

    let mode: DragMode;
    if (Math.abs(relX - passLeftPx) <= EDGE_HIT_PX) mode = 'lo';
    else if (Math.abs(relX - passRightPx) <= EDGE_HIT_PX) mode = 'hi';
    else if (relX > passLeftPx && relX < passRightPx) mode = 'inside';
    else {
      // Outside the passband: a click on a detected carrier fits the filter to
      // it; otherwise do nothing (no drag started).
      if (tryFitToSignal(relX, rect.width, spanHz)) e.preventDefault();
      return;
    }

    e.preventDefault();
    try { canvas.setPointerCapture(e.pointerId); } catch { /* ok */ }

    const activeSlot = presetIsFixed(c.filterPresetName) || !c.filterPresetName ? 'VAR1' : c.filterPresetName;

    // Snapshot detected carriers once so the magnetic edge-snap has stable
    // targets through the drag. The panel keeps the floor live, so detection is
    // available here without the operator toggling global Snap.
    let peaks: DetectedPeak[] = [];
    {
      const d = useDisplayStore.getState();
      if (d.panDb && d.hzPerPixel > 0 && getNoiseFloor() !== null) {
        peaks = detectPeaks(d.panDb, Number(d.centerHz), d.hzPerPixel);
      }
    }

    dragRef.current = {
      mode,
      rect,
      spanHz,
      activeSlot,
      startLoHz: c.filterLowHz,
      startHiHz: c.filterHighHz,
      startX: e.clientX,
      pendingLo: c.filterLowHz,
      pendingHi: c.filterHighHz,
      lastWriteAt: 0,
      flushTimer: null,
      pointerId: e.pointerId,
      peaks,
    };

    if (activeSlot !== c.filterPresetName) {
      useConnectionStore.setState({ filterPresetName: activeSlot });
    }
  };

  const onPointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    e.stopPropagation();

    const vfo = Number(useConnectionStore.getState().vfoHz);
    const hzPerPx = d.spanHz / d.rect.width;
    // Magnetic snap is active for edge drags unless Alt is held (free placement)
    // and unless there are no detected carriers.
    const snap = d.peaks.length > 0 && !e.altKey;
    let loHz = d.startLoHz;
    let hiHz = d.startHiHz;
    if (d.mode === 'lo') {
      const relX = e.clientX - d.rect.left;
      loHz = Math.round(relX * hzPerPx - d.spanHz / 2);
      if (snap) loHz = Math.round(magnetEdge(vfo + loHz, d.peaks) - vfo);
      if (loHz > d.startHiHz - 50) loHz = d.startHiHz - 50;
    } else if (d.mode === 'hi') {
      const relX = e.clientX - d.rect.left;
      hiHz = Math.round(relX * hzPerPx - d.spanHz / 2);
      if (snap) hiHz = Math.round(magnetEdge(vfo + hiHz, d.peaks) - vfo);
      if (hiHz < d.startLoHz + 50) hiHz = d.startLoHz + 50;
    } else {
      const dxHz = Math.round((e.clientX - d.startX) * hzPerPx);
      loHz = d.startLoHz + dxHz;
      hiHz = d.startHiHz + dxHz;
    }

    d.pendingLo = loHz;
    d.pendingHi = hiHz;
    useConnectionStore.setState({ filterLowHz: loHz, filterHighHz: hiHz });
    schedule();
  };

  const onPointerUp = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    e.stopPropagation();
    const canvas = canvasRef.current;
    if (canvas && canvas.hasPointerCapture(e.pointerId)) {
      try { canvas.releasePointerCapture(e.pointerId); } catch { /* ok */ }
    }
    if (d.flushTimer != null) {
      clearTimeout(d.flushTimer);
      d.flushTimer = null;
    }
    const lo = d.pendingLo;
    const hi = d.pendingHi;
    const slot = d.activeSlot;
    dragRef.current = null;
    const applyState = useConnectionStore.getState().applyState;
    setFilter(lo, hi, slot).then(applyState).catch(() => {});
  };

  const onPointerMoveHover = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (dragRef.current) return;
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const spanHz = spanHzRef.current;
    const c = useConnectionStore.getState();
    const passLeftPx = ((c.filterLowHz + spanHz / 2) / spanHz) * rect.width;
    const passRightPx = ((c.filterHighHz + spanHz / 2) / spanHz) * rect.width;
    const relX = e.clientX - rect.left;
    if (Math.abs(relX - passLeftPx) <= EDGE_HIT_PX) {
      hoverEdgeRef.current = 'lo';
      canvas.style.cursor = 'ew-resize';
    } else if (Math.abs(relX - passRightPx) <= EDGE_HIT_PX) {
      hoverEdgeRef.current = 'hi';
      canvas.style.cursor = 'ew-resize';
    } else if (relX > passLeftPx && relX < passRightPx) {
      hoverEdgeRef.current = 'inside';
      canvas.style.cursor = 'move';
    } else {
      hoverEdgeRef.current = null;
      canvas.style.cursor = 'default';
    }
  };

  // ── Editable width pill ─────────────────────────────────────────────────
  const widthHz = Math.abs(filterHighHz - filterLowHz);
  // Centre the pill horizontally over the passband (clamped to stay on-panel).
  const pbCenterHz = (filterLowHz + filterHighHz) / 2;
  const span = spanHzRef.current;
  const pillLeftPct = Math.max(10, Math.min(90, ((pbCenterHz + span / 2) / span) * 100));

  const beginEditWidth = () => {
    setWidthDraft(String(Math.round(widthHz)));
    setEditingWidth(true);
  };

  const commitWidth = () => {
    setEditingWidth(false);
    const next = Number.parseInt(widthDraft, 10);
    if (!Number.isFinite(next) || next < 50) return;
    const c = useConnectionStore.getState();
    let lo: number;
    let hi: number;
    if (isSymmetricMode(c.mode)) {
      lo = -Math.round(next / 2);
      hi = Math.round(next / 2);
    } else {
      // Preserve the passband centre (audio centre) and set the new width.
      const center = (c.filterLowHz + c.filterHighHz) / 2;
      lo = Math.round(center - next / 2);
      hi = Math.round(center + next / 2);
    }
    const slot = presetIsFixed(c.filterPresetName) || !c.filterPresetName ? 'VAR1' : c.filterPresetName;
    useConnectionStore.setState({ filterLowHz: lo, filterHighHz: hi, filterPresetName: slot });
    setFilter(lo, hi, slot).then(c.applyState).catch(() => {});
  };

  const onWidthKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') e.currentTarget.blur();
    else if (e.key === 'Escape') { setEditingWidth(false); }
  };

  return (
    <div className="filter-minipan-wrap">
      <canvas
        ref={canvasRef}
        className="filter-minipan-canvas"
        onPointerDown={onPointerDown}
        onPointerMove={(e) => {
          if (dragRef.current) onPointerMove(e);
          else onPointerMoveHover(e);
        }}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
      />
      {editingWidth ? (
        <input
          autoFocus
          type="number"
          min={50}
          step={nudgeStepHz(mode)}
          value={widthDraft}
          onChange={(e) => setWidthDraft(e.currentTarget.value)}
          onBlur={commitWidth}
          onKeyDown={onWidthKeyDown}
          aria-label="Filter passband width in Hz"
          className="filter-minipan-width-input mono"
          style={{ left: `${pillLeftPct}%` }}
        />
      ) : (
        <button
          type="button"
          className="filter-minipan-width-pill mono"
          title="Passband width — click to set exactly (Hz)"
          onClick={beginEditWidth}
          style={{ left: `${pillLeftPct}%` }}
        >
          {formatFilterWidth(filterLowHz, filterHighHz)}
        </button>
      )}
    </div>
  );
}
