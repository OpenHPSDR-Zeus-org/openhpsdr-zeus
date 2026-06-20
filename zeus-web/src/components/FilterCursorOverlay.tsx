// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef, type RefObject } from 'react';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useConnectionStore } from '../state/connection-store';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { resolvePanTuneTarget } from '../util/use-pan-tune-gesture';

// Thetis-style click-tune cursor: a vertical guide line tracks the mouse
// across the spectrum surface and a translucent grey rectangle previews where
// the receive filter passband would land if the operator clicked here. The
// band is the live filter width [filterLowHz, filterHighHz] anchored to the
// cursor frequency — asymmetric for SSB (USB to the right of the cursor, LSB
// to the left), centred on the cursor for CW. A small readout follows the
// pointer showing the exact frequency the click will commit. In snap mode the
// cursor and passband jump to the same detected-signal target the click handler
// will use. Mirrors display.cs's
// "draw long cursor & filter overlay" block. Pointer-events:none so the
// underlying pan/tune gesture still owns clicks and drags.
type FilterCursorOverlayProps = {
  /** The positioned (relative) surface to track the pointer over. */
  containerRef: RefObject<HTMLElement | null>;
  /** Which receiver's spectrum geometry + snap to preview against. Default 'A';
   *  'B' drives the RX2 half so its hover crosshair tracks VFO B. */
  receiver?: 'A' | 'B';
};

// "14.074.00" — MHz with dot-grouped kHz/Hz, the readout style hams expect.
function formatTuneHz(hz: number): string {
  const mhz = Math.floor(hz / 1_000_000);
  const khz = Math.floor((hz % 1_000_000) / 1000);
  const rem = Math.floor(hz % 1000);
  return `${mhz}.${String(khz).padStart(3, '0')}.${String(rem).padStart(3, '0')}`;
}

export function FilterCursorOverlay({ containerRef, receiver = 'A' }: FilterCursorOverlayProps) {
  const rootRef = useRef<HTMLDivElement | null>(null);
  const bandRef = useRef<HTMLDivElement | null>(null);
  const vLineRef = useRef<HTMLDivElement | null>(null);
  const readoutRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    let mouseX = 0;
    let mouseY = 0;
    let visible = false;
    let raf = 0;
    // Reading offsetWidth/Height forces a synchronous layout. The readout pill
    // only resizes when its text changes, so cache the dims and re-measure only
    // on a digit change instead of every animation frame.
    let lastReadoutText = '';
    let cachedLw = 0;
    let cachedLh = 0;

    const apply = () => {
      raf = 0;
      const root = rootRef.current;
      if (!root) return;
      // Fade rather than hard toggle — keep elements mounted so opacity can
      // animate; pointer-events stay off so nothing is intercepted.
      root.style.opacity = visible ? '1' : '0';
      if (!visible) return;

      const rectW = container.clientWidth;
      const rectH = container.clientHeight;

      // Read THIS half's spectrum geometry/snap (RX2: the B half tracks VFO B).
      // The click commits to THIS half's VFO at this frequency (tuneReceiver
      // follows the surface) — the preview reads the frequency under the cursor.
      const slice = selectDisplaySlice(useDisplayStore.getState(), receiver);
      const conn = useConnectionStore.getState();
      const hzPerPixel = slice.hzPerPixel;
      const len = slice.panDb?.length ?? slice.width;
      let cursorX = mouseX;
      let tuneHz: number | null = null;
      if (hzPerPixel > 0 && len > 0 && rectW > 0) {
        const spanHz = len * hzPerPixel;
        const frac = mouseX / rectW;
        const rawHz = Number(slice.centerHz) + (frac - 0.5) * spanHz;
        const target = resolvePanTuneTarget(rawHz, true, receiver);
        tuneHz = target.tuneHz;
        if (target.snappedToSignal && spanHz > 0) {
          const startHz = Number(slice.centerHz) - spanHz / 2;
          cursorX = ((target.tuneHz - startHz) / spanHz) * rectW;
          cursorX = Math.max(0, Math.min(rectW, cursorX));
        }
      }

      const vLine = vLineRef.current;
      if (vLine) vLine.style.transform = `translateX(${cursorX}px)`;

      // Filter passband preview, anchored to the cursor (not the VFO). Filter
      // edges are stored relative to the dial centre. NOTE: hzPerPixel is
      // Hz-per-FFT-bin, not Hz-per-CSS-pixel — the spectrum is stretched from
      // `len` bins across the container's CSS width (rectW). Converting through
      // the real span keeps this preview 1:1 with the PassbandOverlay (which is
      // percentage-of-span based); dividing by hzPerPixel directly rendered the
      // band off by the bins-to-CSS-pixel ratio.
      const band = bandRef.current;
      if (band) {
        if (hzPerPixel > 0 && len > 0 && rectW > 0) {
          const filterLowHz = receiver === 'B' ? conn.filterLowHzB : conn.filterLowHz;
          const filterHighHz = receiver === 'B' ? conn.filterHighHzB : conn.filterHighHz;
          const mode = receiver === 'B' ? conn.modeB : conn.mode;
          const hzPerCssPx = (len * hzPerPixel) / rectW;
          const lowPx = filterLowHz / hzPerCssPx;
          const highPx = filterHighHz / hzPerCssPx;
          const widthPx = Math.max(1, highPx - lowPx);
          const isCw = mode === 'CWL' || mode === 'CWU';
          // CW centres the passband on the spot you click (the tone lands at
          // the cursor); other modes keep the asymmetric carrier-relative band.
          const leftPx = isCw ? cursorX - widthPx / 2 : cursorX + lowPx;
          band.style.transform = `translateX(${leftPx}px)`;
          band.style.width = `${widthPx}px`;
          band.style.display = '';
        } else {
          band.style.display = 'none';
        }
      }

      // Frequency readout — exactly the value commitFinal() will snap to, so
      // the operator reads the destination before committing.
      const readout = readoutRef.current;
      if (readout) {
        if (tuneHz !== null) {
          const text = formatTuneHz(tuneHz);
          readout.style.display = '';
          if (text !== lastReadoutText) {
            readout.textContent = text;
            lastReadoutText = text;
            cachedLw = readout.offsetWidth;
            cachedLh = readout.offsetHeight;
          }
          // Edge-aware placement: nudge the pill to the cursor, flip side near
          // the right edge, and keep it clear of the top/bottom rails.
          const lw = cachedLw;
          const lh = cachedLh;
          let lx = cursorX + 12;
          if (lx + lw > rectW - 4) lx = cursorX - 12 - lw;
          lx = Math.max(4, Math.min(lx, rectW - lw - 4));
          let ly = mouseY + 14;
          if (ly + lh > rectH - 4) ly = mouseY - 14 - lh;
          ly = Math.max(4, Math.min(ly, rectH - lh - 4));
          readout.style.transform = `translate(${lx}px, ${ly}px)`;
        } else {
          readout.style.display = 'none';
        }
      }
    };

    const schedule = () => {
      if (raf === 0) raf = requestAnimationFrame(apply);
    };

    const onMove = (e: PointerEvent) => {
      // Hover preview is a mouse affordance; touch/pen tune directly on tap.
      if (e.pointerType !== 'mouse') {
        if (visible) {
          visible = false;
          schedule();
        }
        return;
      }
      const rect = container.getBoundingClientRect();
      mouseX = e.clientX - rect.left;
      mouseY = e.clientY - rect.top;
      visible = true;
      schedule();
    };
    const onLeave = () => {
      visible = false;
      schedule();
    };

    container.addEventListener('pointermove', onMove);
    container.addEventListener('pointerleave', onLeave);

    // Keep the preview honest while the operator hovers and changes filter
    // width or mode from elsewhere (filter buttons, mode switch), or as the
    // span/center shifts under a stationary cursor.
    const unsubConn = useConnectionStore.subscribe((s, prev) => {
      if (
        visible &&
        (s.filterLowHz !== prev.filterLowHz ||
          s.filterHighHz !== prev.filterHighHz ||
          s.filterLowHzB !== prev.filterLowHzB ||
          s.filterHighHzB !== prev.filterHighHzB ||
          s.mode !== prev.mode ||
          s.modeB !== prev.modeB)
      ) {
        schedule();
      }
    });
    const unsubDisplay = useDisplayStore.subscribe((s, prev) => {
      if (
        visible &&
        (s.centerHz !== prev.centerHz ||
          s.hzPerPixel !== prev.hzPerPixel ||
          s.width !== prev.width ||
          s.lastSeq !== prev.lastSeq)
      ) {
        schedule();
      }
    });
    const unsubEnhance = useSignalEnhanceStore.subscribe((s, prev) => {
      if (
        visible &&
        (s.snapEnabled !== prev.snapEnabled ||
          s.snapRadiusHz !== prev.snapRadiusHz ||
          s.snapMinSnrDb !== prev.snapMinSnrDb)
      ) {
        schedule();
      }
    });

    return () => {
      if (raf !== 0) cancelAnimationFrame(raf);
      container.removeEventListener('pointermove', onMove);
      container.removeEventListener('pointerleave', onLeave);
      unsubConn();
      unsubDisplay();
      unsubEnhance();
    };
  }, [containerRef, receiver]);

  return (
    <div ref={rootRef} aria-hidden className="filter-cursor">
      <div ref={bandRef} className="filter-cursor-band" />
      <div ref={vLineRef} className="filter-cursor-v" />
      <div ref={readoutRef} className="filter-cursor-readout" />
    </div>
  );
}
