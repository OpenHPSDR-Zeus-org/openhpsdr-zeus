// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.2.1 — mini-panadapter inside the advanced
// filter ribbon. This renderer is the high-end "smooth flowing lines" filter
// window: an instrument well with a faint reference grid, a dynamically
// auto-ranged spectrum trace (so weak signals always fill the panel), a 3-tap
// de-comb so the line reads as a soft continuous curve, a heat-fill under the
// trace in the RX trace colour, a slow-decay peak-hold ghost, and a glassy
// accent passband with focus scrims, feathered edge skirts, a glowing flat
// top, a width ruler, exact cut walls, and rounded grab handles. A live,
// click-to-edit width pill floats over the passband.
//
// VISUAL-ONLY: this is the rendering surface; it carries no AGC / Smart-NR /
// snap / fit-to-signal behaviour. Every filter write goes through the single
// coalescing `flushPending` guard (one POST in flight at a time — see the
// _fftwPlannerGate / #646 crash-safety work) so a fast passband drag can never
// flood rapid POST /api/filter and re-expose the WDSP/FFTW planner double-free.
//
// Uses Canvas 2D (not a second WebGL context) — at this size the 2D path hits
// the <2 ms/frame budget comfortably and avoids scissor-clipping or sharing the
// main panadapter's GL context.

import { useEffect, useRef, useState } from 'react';
import { registerFrameConsumer, useDisplayStore } from '../../state/display-store';
import { useConnectionStore } from '../../state/connection-store';
import { useThemeStore } from '../../state/theme-store';
import { useDisplaySettingsStore } from '../../state/display-settings-store';
import { setFilter } from '../../api/client';
import { formatCutOffset, formatFilterWidth, nudgeStepHz } from './filterPresets';
import type { RxMode } from '../../api/client';

const DEFAULT_SPAN_HZ = 12_000;       // initial visible window around VFO
const MIN_SPAN_HZ = 3_000;            // Ctrl+wheel zoom-in floor
const MAX_SPAN_HZ = 48_000;           // Ctrl+wheel zoom-out ceiling
const TICK_STEP_HZ = 2_000;           // base axis-label spacing (scaled by span)
const DB_FLOOR = -130;
const DB_CEIL = -30;
const DRAG_MIN_INTERVAL_MS = 50;
const EDGE_HIT_PX = 6;
const PEAKHOLD_DECAY_PX = 0.45;       // peak-hold envelope fall rate (px/frame ≈ 13 px/s)

// Palette — passband walls / dots / halo are neutral silvery (read on both
// themes), but the text surfaces (LOW/HIGH CUT label + value, axis ticks, VFO
// centre tick) and the spectrum trace are resolved from --fg-* / --accent /
// the RX-trace colour at draw time so the Theme Settings token pickers drive
// them.
const COL_VFO_CENTER = 'rgba(200, 205, 215, 0.14)'; // subtle neutral VFO line
const COL_CUT_TICK = 'rgba(220, 225, 232, 0.35)';   // hairline callout connecting label to wall

type DragMode = 'lo' | 'hi' | 'inside';

function presetIsFixed(name: string | null): boolean {
  return !!name && /^F([1-9]|10)$/.test(name);
}

function isSymmetricMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
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
// alpha variants at draw time. Lets the accent passband and the heat trace
// follow the live --accent / RX-trace tokens (and any Theme Settings override)
// without hard-coding hex.
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
  return [12, 95, 156]; // --accent fallback
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
    inFlight: boolean;
    dirty: boolean;
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

    let rafHandle = 0;
    let lastSeq: number | null = null;
    let traceY: Float32Array | null = null; // reused per-column trace Y buffer
    let traceSm: Float32Array | null = null; // reused smoothed-trace buffer
    let peakHoldY: Float32Array | null = null; // per-column peak-hold envelope (Y)
    let peakHoldKey = ''; // geometry key; reset the envelope when it changes
    let autoLoDb = NaN; // EMA of the visible-window floor (dynamic vertical scale)
    let autoHiDb = NaN; // EMA of the visible-window peak

    const draw = () => {
      rafHandle = 0;
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
      const [ar, ag, ab] = parseRgb(cs.getPropertyValue('--accent') || '#0c5f9c');
      const accent = (a: number) => `rgba(${ar}, ${ag}, ${ab}, ${a})`;
      // Heat-fill / trace colour follows the RX trace token (amber #FFA028 by
      // default — sanctioned signal-strength colour).
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

      // Window geometry shared by the trace.
      const loHz = vfo - spanHz / 2;
      let binStart = 0;
      let binEnd = 0;
      if (panDb && binsPerHz > 0) {
        const displayCenter = Number(d.centerHz);
        const fullSpanHz = panDb.length * d.hzPerPixel;
        const fullStartHz = displayCenter - fullSpanHz / 2;
        binStart = Math.max(0, Math.floor((loHz - fullStartHz) * binsPerHz));
        binEnd = Math.min(panDb.length, Math.ceil((loHz + spanHz - fullStartHz) * binsPerHz));
      }
      const bins = binEnd - binStart;

      // Dynamic vertical auto-range: scan the visible window, track an
      // EMA-smoothed floor→peak, and map THAT to the plot height. The noise
      // floor hugs the bottom and any signal fills the panel regardless of
      // absolute level — so weak signals and their width are always easy to see.
      // EMA keeps it from jumping frame-to-frame. A minimum span stops a quiet
      // window from amplifying noise into a full-height mess.
      if (panDb && bins > 0) {
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

      const dbToY = (v: number) => {
        const norm = (v - loDb) / dbSpan;
        return plotTop + plotH - Math.max(0, Math.min(1, norm)) * plotH;
      };

      if (panDb && bins > 0) {
        // Spectrum trace — peak-hold per pixel column. Ys are recorded into a
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
          let peak = -Infinity;
          for (let i = b0; i < b1; i++) {
            const v = panDb[i] ?? DB_FLOOR;
            if (v > peak) peak = v;
          }
          const interp = (panDb[i0] ?? DB_FLOOR) * (1 - frac) + (panDb[i1] ?? DB_FLOOR) * frac;
          // Zoomed out (many bins/pixel) keep peaks; zoomed in, follow the
          // smooth interpolation so the trace reads as a continuous curve.
          const v = peak === -Infinity ? interp : (bins >= w ? 0.6 * peak + 0.4 * interp : interp);
          ty[x] = dbToY(v);
        }

        // De-comb: 3-tap moving average so noise reads as a soft band, not a
        // picket fence, while real signal humps survive.
        const sm = traceSm;
        for (let x = 0; x < w; x++) {
          const a = ty[x === 0 ? 0 : x - 1]!;
          const b = ty[x]!;
          const cc = ty[x === w - 1 ? w - 1 : x + 1]!;
          sm[x] = (a + b + cc) / 3;
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
      }

      // VFO center line — subtle, in the plot area only.
      ctx.strokeStyle = COL_VFO_CENTER;
      ctx.lineWidth = 1 * dpr;
      ctx.beginPath();
      ctx.moveTo(w / 2, plotTop);
      ctx.lineTo(w / 2, plotTop + plotH);
      ctx.stroke();

      // Passband — accent-tinted filled rectangle between two glowing cut
      // walls, with a bright flat top and grab handles.
      const passLeftPx = ((c.filterLowHz + spanHz / 2) / spanHz) * w;
      const passRightPx = ((c.filterHighHz + spanHz / 2) / spanHz) * w;
      const onScreen = passRightPx > 0 && passLeftPx < w;

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
          leftSkirt.addColorStop(1, accent(0.20));
          ctx.fillStyle = leftSkirt;
          ctx.fillRect(leftSkirtX, pbTop, Math.min(w, Lx) - leftSkirtX, pbBottom - pbTop);
        }
        if (Rx > 0 && Rx < w) {
          const rightSkirtR = Math.min(w, Rx + skirtPx);
          const rightSkirt = ctx.createLinearGradient(Math.min(w, Rx), 0, rightSkirtR, 0);
          rightSkirt.addColorStop(0, accent(0.20));
          rightSkirt.addColorStop(1, accent(0.00));
          ctx.fillStyle = rightSkirt;
          ctx.fillRect(Math.max(0, Rx), pbTop, rightSkirtR - Math.max(0, Rx), pbBottom - pbTop);
        }

        // 2) Filled passband — a clean rectangle between the two cut walls.
        //    Accent vertical gradient plus a center glow gives the selected
        //    bandwidth a live, glassy read without changing its exact geometry.
        const pbGrad = ctx.createLinearGradient(0, pbTop, 0, pbBottom);
        pbGrad.addColorStop(0.0, accent(0.34));
        pbGrad.addColorStop(0.34, accent(0.18));
        pbGrad.addColorStop(0.72, accent(0.08));
        pbGrad.addColorStop(1.0, accent(0.02));
        ctx.fillStyle = pbGrad;
        ctx.fillRect(fillL, pbTop, Math.max(0, fillR - fillL), pbBottom - pbTop);
        if (fillR > fillL) {
          ctx.save();
          ctx.beginPath();
          ctx.rect(fillL, pbTop, fillR - fillL, pbBottom - pbTop);
          ctx.clip();
          const glow = ctx.createRadialGradient((Lx + Rx) / 2, pbTop, 0, (Lx + Rx) / 2, pbTop, Math.max(1, (fillR - fillL) * 0.65));
          glow.addColorStop(0, accent(0.22));
          glow.addColorStop(0.58, accent(0.06));
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
        // above value (bold, brighter) on a readability chip in the reserved
        // top band. A hairline connector ties each label to its wall top.
        // Labels center on the wall X and clamp to canvas edges so they never
        // clip.
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
    const unsubSettings = useDisplaySettingsStore.subscribe((s, p) => {
      if (s.rxTraceColor !== p.rxTraceColor) requestRedraw();
    });

    // Scroll wheel — Ctrl/⌘+wheel zooms the visible span (a pure visual change,
    // no filter write). Plain wheel is left to the page so the panel never
    // POSTs from a scroll gesture.
    const onWheel = (e: WheelEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      e.preventDefault();
      const dir = e.deltaY > 0 ? 1 : -1;
      const factor = dir > 0 ? 1.18 : 1 / 1.18;
      const next = Math.round(Math.max(MIN_SPAN_HZ, Math.min(MAX_SPAN_HZ, spanHzRef.current * factor)));
      if (next !== spanHzRef.current) {
        spanHzRef.current = next;
        setSpanTick((t) => t + 1);
        requestRedraw();
      }
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
      unsubSettings();
      canvas.removeEventListener('wheel', onWheel);
      ro.disconnect();
      releaseFrameConsumer();
    };
  }, []);

  // Single filter-write path. CRASH-SAFETY (#646): exactly one POST /api/filter
  // is in flight at a time. While one is outstanding, the latest pending edges
  // are coalesced and sent on completion — a fast drag can never flood rapid
  // POSTs and re-expose the WDSP/FFTW planner double-free (the backend
  // _fftwPlannerGate is the defence-in-depth half of the same fix).
  const flushPending = () => {
    const d = dragRef.current;
    if (!d) return;
    d.flushTimer = null;
    if (d.inFlight) { d.dirty = true; return; }   // coalesce: one POST at a time
    d.inFlight = true;
    d.lastWriteAt = performance.now();
    setFilter(d.pendingLo, d.pendingHi, d.activeSlot)
      .catch(() => {})
      .finally(() => {
        const dd = dragRef.current;
        if (!dd) return;
        dd.inFlight = false;
        if (dd.dirty) { dd.dirty = false; flushPending(); }  // send the latest
      });
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
    else return;

    e.preventDefault();
    try { canvas.setPointerCapture(e.pointerId); } catch { /* ok */ }

    const activeSlot = presetIsFixed(c.filterPresetName) || !c.filterPresetName ? 'VAR1' : c.filterPresetName;

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
      inFlight: false,
      dirty: false,
    };

    if (activeSlot !== c.filterPresetName) {
      useConnectionStore.setState({ filterPresetName: activeSlot });
    }
  };

  const onPointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    e.stopPropagation();

    const hzPerPx = d.spanHz / d.rect.width;
    let loHz = d.startLoHz;
    let hiHz = d.startHiHz;
    if (d.mode === 'lo') {
      const relX = e.clientX - d.rect.left;
      loHz = Math.round(relX * hzPerPx - d.spanHz / 2);
      if (loHz > d.startHiHz - 50) loHz = d.startHiHz - 50;
    } else if (d.mode === 'hi') {
      const relX = e.clientX - d.rect.left;
      hiHz = Math.round(relX * hzPerPx - d.spanHz / 2);
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

  // Width-pill commit is a single one-shot write (gesture-end, not per-frame),
  // so it cannot flood; it routes through setFilter directly like the legacy
  // pointer-up path.
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
