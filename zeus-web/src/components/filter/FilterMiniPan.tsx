// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Filter visualization PRD §3.2.1 — mini-panadapter inside the advanced
// filter ribbon. Matches the mockup at docs/pics/filterpanel_mockup.png:
// light-gray spectrum trace, hollow blue passband rectangle with corner
// triangle handles, x-axis tick labels at 2 kHz intervals around the VFO.
//
// Uses Canvas 2D (not a second WebGL context) — at ~640×110 CSS pixels the
// 2D path hits the <2 ms/frame budget comfortably and avoids the complexity
// of scissor-clipping or sharing the main panadapter's GL context.

import { useEffect, useRef } from 'react';
import { registerFrameConsumer, useDisplayStore } from '../../state/display-store';
import { useConnectionStore } from '../../state/connection-store';
import { useThemeStore } from '../../state/theme-store';
import { setFilter } from '../../api/client';
import { formatCutOffset } from './filterPresets';

const RIBBON_SPAN_HZ = 12_000;        // 12 kHz span centered on VFO (matches mockup: 14.249..14.261)
const TICK_STEP_HZ = 2_000;           // label a tick every 2 kHz
const DB_FLOOR = -130;
const DB_CEIL = -30;
const DRAG_MIN_INTERVAL_MS = 50;
const EDGE_HIT_PX = 6;

// Palette — passband walls / dots / halo are neutral silvery (read on both
// themes), but the four *text* surfaces (LOW/HIGH CUT label + value, axis
// ticks, VFO centre tick) and the spectrum trace are resolved from
// --fg-0 / --fg-1 / --fg-2 at draw time so the Theme Settings token pickers
// drive them.
const COL_VFO_CENTER = 'rgba(200, 205, 215, 0.08)'; // very subtle neutral VFO line
const COL_PB_WASH = 'rgba(220, 232, 245, 0.035)';   // almost-invisible interior wash
const COL_WALL_HALO = 'rgba(220, 225, 232, 0.45)';  // soft neutral halo behind walls
const COL_DOT = 'rgba(245, 247, 250, 0.95)';        // bright white corner dots
const COL_CUT_TICK = 'rgba(220, 225, 232, 0.35)';   // hairline callout connecting label to wall

type DragMode = 'lo' | 'hi' | 'inside';

function presetIsFixed(name: string | null): boolean {
  return !!name && /^F([1-9]|10)$/.test(name);
}

// Format VFO-relative Hz offset as absolute-MHz with 3 decimals (e.g. 14.249).
// Used for x-axis tick labels.
function formatTickMhz(absHz: number): string {
  return (absHz / 1_000_000).toFixed(3);
}

export function FilterMiniPan() {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const dragRef = useRef<{
    mode: DragMode;
    rect: DOMRect;
    activeSlot: string;
    startLoHz: number;
    startHiHz: number;
    startX: number;
    pendingLo: number;
    pendingHi: number;
    lastWriteAt: number;
    flushTimer: number | null;
    pointerId: number;
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
    let lastSeq = -1;

    const draw = () => {
      rafHandle = 0;
      const d = useDisplayStore.getState();
      const c = useConnectionStore.getState();
      if (d.lastSeq === lastSeq) return;
      lastSeq = d.lastSeq;

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
      const colTrace = fg1;          // spectrum line — sits between primary and muted text
      const colTickLabel = fg2;
      const colTickLabelCenter = fg0;
      const colCutKey = fg2;
      const colCutVal = fg0;

      // Reserve the top ~22 px for LOW CUT / HIGH CUT wall callouts and the
      // bottom ~14 px for the x-axis labels so neither overlap the trace.
      const labelH = Math.round(22 * dpr);
      const axisH = Math.round(14 * dpr);
      const plotTop = labelH;
      const plotH = h - axisH - labelH;

      const vfo = Number(c.vfoHz);
      const panDb = d.panDb;
      const binsPerHz = d.hzPerPixel > 0 ? 1 / d.hzPerPixel : 0;

      if (panDb && binsPerHz > 0) {
        const displayCenter = Number(d.centerHz);
        const fullSpanHz = panDb.length * d.hzPerPixel;
        const fullStartHz = displayCenter - fullSpanHz / 2;
        const loHz = vfo - RIBBON_SPAN_HZ / 2;
        const binStart = Math.max(0, Math.floor((loHz - fullStartHz) * binsPerHz));
        const binEnd = Math.min(panDb.length, Math.ceil((loHz + RIBBON_SPAN_HZ - fullStartHz) * binsPerHz));

        // Decimated spectrum trace.
        const bins = binEnd - binStart;
        if (bins > 0) {
          ctx.lineWidth = 1 * dpr;
          ctx.strokeStyle = colTrace;
          ctx.beginPath();
          for (let x = 0; x < w; x++) {
            const b0 = binStart + Math.floor((x * bins) / w);
            const b1 = binStart + Math.floor(((x + 1) * bins) / w);
            let peak = -Infinity;
            for (let i = b0; i < b1; i++) {
              const v = panDb[i] ?? DB_FLOOR;
              if (v > peak) peak = v;
            }
            if (peak === -Infinity) peak = DB_FLOOR;
            const norm = (peak - DB_FLOOR) / (DB_CEIL - DB_FLOOR);
            const y = plotTop + plotH - Math.max(0, Math.min(1, norm)) * plotH;
            if (x === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          }
          ctx.stroke();
        }
      }

      // VFO center line — subtle, in the plot area only.
      ctx.strokeStyle = COL_VFO_CENTER;
      ctx.lineWidth = 1 * dpr;
      ctx.beginPath();
      ctx.moveTo(w / 2, plotTop);
      ctx.lineTo(w / 2, plotTop + plotH);
      ctx.stroke();

      // Passband — bright silver walls + soft neutral halo + corner dots.
      // NOT a cyan box: the interior is an almost-invisible light wash, the
      // two vertical walls are 2px with a white→light-gray vertical gradient,
      // and four 6×6 square dots punctuate the corners.
      const passLeftPx = ((c.filterLowHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * w;
      const passRightPx = ((c.filterHighHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * w;
      const onScreen = passRightPx > 0 && passLeftPx < w;
      if (onScreen) {
        const clampedL = Math.max(0, passLeftPx);
        const clampedR = Math.min(w, passRightPx);
        const pbTop = plotTop + Math.round(4 * dpr);
        const pbBottom = plotTop + plotH - Math.round(4 * dpr);
        const wallW = Math.max(2, Math.round(2 * dpr));
        const dotSize = Math.round(6 * dpr);
        const halo = Math.round(6 * dpr);

        // Interior wash.
        ctx.fillStyle = COL_PB_WASH;
        ctx.fillRect(
          Math.round(clampedL),
          pbTop,
          Math.max(0, Math.round(clampedR - clampedL)),
          pbBottom - pbTop,
        );

        // Walls — vertical gradient top→bottom, painted with a soft halo.
        const wallGrad = ctx.createLinearGradient(0, pbTop, 0, pbBottom);
        wallGrad.addColorStop(0.00, 'rgba(245, 247, 250, 0.95)');
        wallGrad.addColorStop(0.40, 'rgba(225, 228, 232, 0.85)');
        wallGrad.addColorStop(1.00, 'rgba(195, 200, 208, 0.55)');

        ctx.save();
        ctx.shadowColor = COL_WALL_HALO;
        ctx.shadowBlur = halo;
        ctx.fillStyle = wallGrad;
        // Left wall straddles the left edge (1px outside / 1px inside).
        ctx.fillRect(Math.round(clampedL) - Math.floor(wallW / 2), pbTop, wallW, pbBottom - pbTop);
        // Right wall straddles the right edge.
        ctx.fillRect(Math.round(clampedR) - Math.ceil(wallW / 2), pbTop, wallW, pbBottom - pbTop);
        ctx.restore();

        // Four corner dots — bright white squares flush to each wall corner.
        ctx.save();
        ctx.shadowColor = COL_WALL_HALO;
        ctx.shadowBlur = Math.round(4 * dpr);
        ctx.fillStyle = COL_DOT;
        const dotL = Math.round(clampedL) - Math.floor(dotSize / 2);
        const dotR = Math.round(clampedR) - Math.floor(dotSize / 2);
        ctx.fillRect(dotL, pbTop - Math.floor(dotSize / 2), dotSize, dotSize);
        ctx.fillRect(dotR, pbTop - Math.floor(dotSize / 2), dotSize, dotSize);
        ctx.fillRect(dotL, pbBottom - Math.ceil(dotSize / 2), dotSize, dotSize);
        ctx.fillRect(dotR, pbBottom - Math.ceil(dotSize / 2), dotSize, dotSize);
        ctx.restore();

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

          // Hairline from label bottom down to wall top (lands on the dot).
          ctx.strokeStyle = COL_CUT_TICK;
          ctx.lineWidth = 1 * dpr;
          ctx.beginPath();
          ctx.moveTo(Math.round(wallX) + 0.5, valY + valFontPx + Math.round(1 * dpr));
          ctx.lineTo(Math.round(wallX) + 0.5, pbTop - Math.floor(dotSize / 2));
          ctx.stroke();

          // Measure both lines to find clamp bounds.
          ctx.font = valFont;
          const valW = ctx.measureText(value).width;
          ctx.letterSpacing = '0.15em';
          ctx.font = keyFont;
          const keyW = ctx.measureText(key).width;
          const halfMax = Math.max(valW, keyW) / 2;
          const cx = Math.max(halfMax + padX, Math.min(w - halfMax - padX, wallX));

          // Key (top, muted, letter-spaced).
          ctx.textBaseline = 'top';
          ctx.textAlign = 'center';
          ctx.fillStyle = colCutKey;
          ctx.fillText(key, cx, keyY);

          // Value (bold, brighter, no letter-spacing).
          ctx.letterSpacing = '0px';
          ctx.font = valFont;
          ctx.fillStyle = colCutVal;
          ctx.fillText(value, cx, valY);
        };

        drawCallout(passLeftPx, 'lo', formatCutOffset(c.filterLowHz));
        drawCallout(passRightPx, 'hi', formatCutOffset(c.filterHighHz));

        // Reset text state for subsequent draws (x-axis labels assume start).
        ctx.textAlign = 'start';
        ctx.letterSpacing = '0px';
      }

      // X-axis tick labels. One label every TICK_STEP_HZ (2 kHz), centered
      // on the VFO. VFO sits at the middle tick.
      ctx.fillStyle = colTickLabel;
      ctx.font = `${Math.round(9.5 * dpr)}px "SFMono-Regular", ui-monospace, monospace`;
      ctx.textBaseline = 'middle';
      const labelY = plotTop + plotH + Math.round(axisH / 2);
      const nTicks = Math.floor(RIBBON_SPAN_HZ / TICK_STEP_HZ) + 1; // inclusive both ends
      const tickOffsets: number[] = [];
      // Center-out so VFO tick is guaranteed; symmetric ticks either side.
      const halfTicks = Math.floor(nTicks / 2);
      for (let i = -halfTicks; i <= halfTicks; i++) tickOffsets.push(i * TICK_STEP_HZ);
      tickOffsets.forEach((offHz) => {
        const absHz = vfo + offHz;
        const xPx = ((offHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * w;
        if (xPx < 0 || xPx > w) return;
        const text = formatTickMhz(absHz);
        const m = ctx.measureText(text);
        // Brighter fill on the VFO (center) tick, muted on the rest.
        ctx.fillStyle = offHz === 0 ? colTickLabelCenter : colTickLabel;
        ctx.fillText(text, Math.max(2, Math.min(w - m.width - 2, xPx - m.width / 2)), labelY);
      });
    };

    const unsubDisplay = useDisplayStore.subscribe(() => {
      if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
    });
    const unsubConn = useConnectionStore.subscribe((s, p) => {
      if (
        s.filterLowHz !== p.filterLowHz ||
        s.filterHighHz !== p.filterHighHz ||
        s.vfoHz !== p.vfoHz
      ) {
        lastSeq = -1;
        if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
      }
    });
    const unsubTheme = useThemeStore.subscribe((s, p) => {
      if (s.theme !== p.theme || s.overrides !== p.overrides) {
        lastSeq = -1;
        if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
      }
    });

    const ro = new ResizeObserver(() => {
      lastSeq = -1;
      if (rafHandle === 0) rafHandle = requestAnimationFrame(draw);
    });
    ro.observe(canvas);

    rafHandle = requestAnimationFrame(draw);
    return () => {
      if (rafHandle !== 0) cancelAnimationFrame(rafHandle);
      unsubDisplay();
      unsubConn();
      unsubTheme();
      ro.disconnect();
      releaseFrameConsumer();
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

  const onPointerDown = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (e.button !== 0) return;
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    if (rect.width <= 0) return;

    const c = useConnectionStore.getState();
    const passLeftPx = ((c.filterLowHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * rect.width;
    const passRightPx = ((c.filterHighHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * rect.width;
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
      activeSlot,
      startLoHz: c.filterLowHz,
      startHiHz: c.filterHighHz,
      startX: e.clientX,
      pendingLo: c.filterLowHz,
      pendingHi: c.filterHighHz,
      lastWriteAt: 0,
      flushTimer: null,
      pointerId: e.pointerId,
    };

    if (activeSlot !== c.filterPresetName) {
      useConnectionStore.setState({ filterPresetName: activeSlot });
    }
  };

  const onPointerMove = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const d = dragRef.current;
    if (!d || e.pointerId !== d.pointerId) return;
    e.stopPropagation();

    const hzPerPx = RIBBON_SPAN_HZ / d.rect.width;
    let loHz = d.startLoHz;
    let hiHz = d.startHiHz;
    if (d.mode === 'lo') {
      const relX = e.clientX - d.rect.left;
      loHz = Math.round(relX * hzPerPx - RIBBON_SPAN_HZ / 2);
      if (loHz > d.startHiHz - 50) loHz = d.startHiHz - 50;
    } else if (d.mode === 'hi') {
      const relX = e.clientX - d.rect.left;
      hiHz = Math.round(relX * hzPerPx - RIBBON_SPAN_HZ / 2);
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
    const c = useConnectionStore.getState();
    const passLeftPx = ((c.filterLowHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * rect.width;
    const passRightPx = ((c.filterHighHz + RIBBON_SPAN_HZ / 2) / RIBBON_SPAN_HZ) * rect.width;
    const relX = e.clientX - rect.left;
    if (Math.abs(relX - passLeftPx) <= EDGE_HIT_PX || Math.abs(relX - passRightPx) <= EDGE_HIT_PX) {
      canvas.style.cursor = 'ew-resize';
    } else if (relX > passLeftPx && relX < passRightPx) {
      canvas.style.cursor = 'move';
    } else {
      canvas.style.cursor = 'default';
    }
  };

  return (
    <canvas
      ref={canvasRef}
      style={{
        display: 'block',
        width: '100%',
        height: '100%',
        touchAction: 'none',
        background: 'transparent',
      }}
      onPointerDown={onPointerDown}
      onPointerMove={(e) => {
        if (dragRef.current) onPointerMove(e);
        else onPointerMoveHover(e);
      }}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
    />
  );
}
