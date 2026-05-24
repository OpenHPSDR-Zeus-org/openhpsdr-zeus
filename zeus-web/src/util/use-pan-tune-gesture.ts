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

import { createContext, useContext, useEffect, type RefObject } from 'react';
import { setVfo, setZoom, ZOOM_MAX, ZOOM_MIN, type ZoomLevel } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useToolbarFavoritesStore } from '../state/toolbar-favorites-store';

const MAX_HZ = 60_000_000;
const CLICK_SLOP_PX = 3;
// Pan gestures (click + drag on pan/wf) snap to this step. Typed-freq input
// and band presets bypass it. Ham-friendly default; becomes user-settable
// once the UX exists.
const PAN_STEP_HZ = 500;
// Wheel tune step now follows the operator's TuningStepWidget choice
// (toolbar-favorites-store.stepHz). Read at event time inside the wheel
// handler so the latest value applies on every notch. Arrow-key tuning
// in use-keyboard-shortcuts.ts reads the same store, so wheel + arrows
// feel the same.
// Scroll-wheel notches normalise mouse clicks (~100px/tick) and trackpad
// deltas to one discrete tick per this many pixels of deltaY.
const WHEEL_NOTCH_PX = 40;

function snapHz(hz: number): number {
  if (!Number.isFinite(hz)) return 0;
  const snapped = Math.round(hz / PAN_STEP_HZ) * PAN_STEP_HZ;
  return Math.min(MAX_HZ, Math.max(0, snapped));
}

function clampHz(hz: number): number {
  if (!Number.isFinite(hz)) return 0;
  return Math.min(MAX_HZ, Math.max(0, hz));
}

function clampZoom(z: number): ZoomLevel {
  return Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, Math.round(z)));
}

// Optional map actions for alt / alt+shift + wheel. App wires these to the
// Leaflet world map; if absent (map not mounted), alt-wheel is swallowed.
export type SpectrumWheelActions = {
  onMapPan?: (dx: number, dy: number) => void;
  onMapZoom?: (delta: number) => void;
};

export const SpectrumWheelActionsContext = createContext<SpectrumWheelActions>({});

function readView(): { centerHz: number; spanHz: number; viewportOffsetHz: number } | null {
  const s = useDisplayStore.getState();
  if (!s.panDb || s.hzPerPixel <= 0) return null;
  return {
    // centerHz here is the radio's hardware NCO (== RadioLoHz) — that's what
    // the incoming frames are anchored to. The visible viewport centre is
    // centerHz + viewportOffsetHz.
    centerHz: Number(s.centerHz),
    spanHz: s.panDb.length * s.hzPerPixel,
    viewportOffsetHz: s.viewportOffsetHz,
  };
}

/**
 * Install click-to-tune and drag-to-pan pointer handlers on a spectrum canvas.
 * Both panadapter and waterfall share this so the user can tune from whichever
 * view they prefer. Values snap to PAN_STEP_HZ (500 Hz) — the per-gesture
 * default. Drags coalesce to one POST per animation frame; releases commit
 * final and re-sync from the server response.
 */
export function usePanTuneGesture(
  canvasRef: RefObject<HTMLCanvasElement | null>,
) {
  const wheelActions = useContext(SpectrumWheelActionsContext);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    // Pure-pan drag (docs/prd/panfall_behavior.md): pointer-down captures
    // the viewport centre at gesture start, pointer-move slides the
    // viewportOffsetHz (no setVfo, no POST), pointer-up either leaves the
    // offset (drag stayed inside the IQ window) or POSTs /api/radio/lo to
    // adjoin a fresh sample window in the pan direction. vfoHz is NEVER
    // mutated by a drag.
    type Drag = {
      startX: number;
      // Viewport centre at gesture start (Hz). The new viewportOffsetHz on
      // every move is derived from this anchor — not from radioLoHz at the
      // current tick — so an in-flight band-change retune that fires during
      // a drag can't yank the spectrum out from under the finger.
      startViewportCenterHz: number;
      spanHz: number;
      moved: boolean;
    };
    type MapDrag = { lastX: number; lastY: number };
    type Pinch = {
      baseDist: number;     // pointer separation when the pinch began (px)
      baseZoom: number;     // zoom level when the pinch began
      pendingZoom: number | null;
      raf: number;
    };
    let drag: Drag | null = null;
    // alt-held pointer drag — delegates to the background map via the
    // SpectrumWheelActionsContext so it feels like M-hold drag without
    // swapping pointer-events on the spectrum stack.
    let mapDrag: MapDrag | null = null;
    // Live pointer roster for multi-touch (pinch-to-zoom on mobile). Single
    // pointer flows through the existing drag-to-tune path; ≥2 pointers
    // triggers pinch and suppresses any in-flight drag.
    const pointers = new Map<number, { x: number; y: number }>();
    let pinch: Pinch | null = null;
    let pendingHz: number | null = null;
    let pendingAbort: AbortController | null = null;
    let pendingRaf = 0;

    const pinchDistance = (): number => {
      const arr = Array.from(pointers.values());
      if (arr.length < 2) return 0;
      const a = arr[0];
      const b = arr[1];
      if (!a || !b) return 0;
      return Math.hypot(a.x - b.x, a.y - b.y);
    };

    const cancelPinchRaf = () => {
      if (pinch && pinch.raf !== 0) {
        cancelAnimationFrame(pinch.raf);
        pinch.raf = 0;
      }
    };

    // Wheel bookkeeping: accumulate deltas so trackpad micro-deltas feel
    // consistent, but emit at most one step per physical wheel event — one
    // notch on a mouse wheel should be one tune/zoom step, not three.
    let wheelAccum = 0;
    let zoomInflight: AbortController | null = null;

    const flushPending = () => {
      pendingRaf = 0;
      const hz = pendingHz;
      pendingHz = null;
      if (hz == null) return;
      useConnectionStore.setState({ vfoHz: hz });
      pendingAbort?.abort();
      const ctrl = new AbortController();
      pendingAbort = ctrl;
      setVfo(hz, ctrl.signal).catch(() => {});
    };

    const scheduleFlush = () => {
      if (pendingRaf === 0) pendingRaf = requestAnimationFrame(flushPending);
    };

    const commitFinal = (hz: number) => {
      const snapped = snapHz(hz);
      useConnectionStore.setState({ vfoHz: snapped });
      // Click-to-tune is an explicit tune-to-frequency action; per the PRD
      // it resets any held viewportOffsetHz so the dial visibly snaps back
      // to the panadapter centre.
      useDisplayStore.getState().setViewportOffsetHz(0);
      pendingAbort?.abort();
      pendingAbort = null;
      if (pendingRaf !== 0) {
        cancelAnimationFrame(pendingRaf);
        pendingRaf = 0;
      }
      pendingHz = null;
      setVfo(snapped)
        .then((s) => useConnectionStore.getState().applyState(s))
        .catch(() => {});
    };

    // Wheel-driven VFO nudge: fine-tune step, no snap to PAN_STEP_HZ. Coalesces
    // to one POST per rAF via the same pending pipeline as drag-to-pan.
    const nudgeVfo = (deltaHz: number) => {
      const cur = pendingHz ?? useConnectionStore.getState().vfoHz;
      pendingHz = clampHz(cur + deltaHz);
      scheduleFlush();
    };

    const nudgeZoom = (delta: number) => {
      if (delta === 0) return;
      const cur = useConnectionStore.getState().zoomLevel;
      const next = clampZoom(cur + delta);
      if (next === cur) return;
      useConnectionStore.getState().setZoomLevel(next);
      zoomInflight?.abort();
      const ctrl = new AbortController();
      zoomInflight = ctrl;
      setZoom(next, ctrl.signal)
        .then((s) => {
          if (!ctrl.signal.aborted) useConnectionStore.getState().applyState(s);
        })
        .catch(() => {});
    };

    const onPointerDown = (e: PointerEvent) => {
      if (e.button !== 0) return;
      pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
      // Two-finger pinch on mobile → zoom. Pinch always wins over an in-flight
      // single-pointer drag; we drop the drag state so the lifted finger
      // doesn't snap-tune on release.
      if (pointers.size >= 2) {
        if (drag) drag = null;
        if (mapDrag) mapDrag = null;
        canvas.style.cursor = '';
        if (!pinch) {
          pinch = {
            baseDist: pinchDistance(),
            baseZoom: useConnectionStore.getState().zoomLevel,
            pendingZoom: null,
            raf: 0,
          };
        }
        try { canvas.setPointerCapture(e.pointerId); } catch { /* ok */ }
        e.preventDefault();
        return;
      }
      // alt held → drag the background map instead of panning the spectrum.
      // Mirrors M-hold drag behavior without the pointer-events:none swap.
      if (e.altKey) {
        e.preventDefault();
        try { canvas.setPointerCapture(e.pointerId); } catch { /* ok */ }
        mapDrag = { lastX: e.clientX, lastY: e.clientY };
        canvas.style.cursor = 'grabbing';
        return;
      }
      const view = readView();
      if (!view) return;
      e.preventDefault();
      try {
        canvas.setPointerCapture(e.pointerId);
      } catch {
        /* synthetic events don't have an active pointer; real mouse/touch does */
      }
      drag = {
        startX: e.clientX,
        // Capture the viewport centre at gesture start (radioLoHz + any
        // existing offset). Anchoring to this absolute Hz makes the pan feel
        // stable even if radioLoHz changes mid-drag (band-change retune,
        // CAT, etc.).
        startViewportCenterHz: view.centerHz + view.viewportOffsetHz,
        spanHz: view.spanHz,
        moved: false,
      };
      canvas.style.cursor = 'grabbing';
    };

    const onPointerMove = (e: PointerEvent) => {
      const p = pointers.get(e.pointerId);
      if (p) {
        p.x = e.clientX;
        p.y = e.clientY;
      }
      if (pinch) {
        const d = pinchDistance();
        if (d > 0 && pinch.baseDist > 0) {
          const ratio = d / pinch.baseDist;
          // Linear ratio → integer zoom level. Round so a small wobble doesn't
          // chatter; the optimistic store update + setZoom still flush via
          // nudgeZoom's existing debounce.
          const target = clampZoom(pinch.baseZoom * ratio);
          if (pinch.pendingZoom !== target) {
            pinch.pendingZoom = target;
            if (pinch.raf === 0) {
              pinch.raf = requestAnimationFrame(() => {
                if (!pinch) return;
                pinch.raf = 0;
                const next = pinch.pendingZoom;
                if (next == null) return;
                const cur = useConnectionStore.getState().zoomLevel;
                if (next !== cur) nudgeZoom(next - cur);
              });
            }
          }
        }
        e.preventDefault();
        return;
      }
      if (mapDrag) {
        const dx = e.clientX - mapDrag.lastX;
        const dy = e.clientY - mapDrag.lastY;
        mapDrag.lastX = e.clientX;
        mapDrag.lastY = e.clientY;
        if (dx === 0 && dy === 0) return;
        // Negate: panBy shifts the view, but grab-drag should move the visible
        // content *with* the finger — so the view must shift the opposite way.
        wheelActions.onMapPan?.(-dx, -dy);
        return;
      }
      if (!drag) return;
      const dx = e.clientX - drag.startX;
      if (!drag.moved && Math.abs(dx) <= CLICK_SLOP_PX) return;
      drag.moved = true;
      const rect = canvas.getBoundingClientRect();
      if (rect.width <= 0) return;
      // Drag-to-tune (classic, CTUN pure-pan reverted): the frequency under the
      // cursor becomes the dial, streamed through the coalesced setVfo pipeline
      // so the radio retunes live as you drag. No viewport offset — RadioLoHz
      // follows the dial server-side, RX and TX both track it.
      const view = readView();
      if (!view) return;
      const frac = (e.clientX - rect.left) / rect.width;
      pendingHz = clampHz(
        Math.round(view.centerHz + view.viewportOffsetHz + (frac - 0.5) * view.spanHz),
      );
      scheduleFlush();
    };

    const onPointerUp = (e: PointerEvent) => {
      pointers.delete(e.pointerId);
      if (pinch) {
        if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
        if (pointers.size < 2) {
          // End of pinch — discard any in-flight rAF and let the user lift +
          // re-touch to start a fresh drag-to-tune. Re-entering drag from a
          // post-pinch single finger leads to a jump tune, which is worse
          // than an enforced clean break.
          cancelPinchRaf();
          pinch = null;
          canvas.style.cursor = 'grab';
        }
        return;
      }
      if (mapDrag) {
        mapDrag = null;
        canvas.style.cursor = 'grab';
        if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
        return;
      }
      const d = drag;
      if (!d) return;
      drag = null;
      canvas.style.cursor = 'grab';
      if (canvas.hasPointerCapture(e.pointerId)) {
        canvas.releasePointerCapture(e.pointerId);
      }
      const rect = canvas.getBoundingClientRect();
      if (rect.width <= 0) return;
      // Drag or click — both tune to the frequency at the release point. CTUN
      // pure-pan (leave-offset / retune-NCO-on-release) is gone: a release
      // always commits a dial tune so RX and TX track where you let go.
      const view = readView();
      if (!view) return;
      const frac = (e.clientX - rect.left) / rect.width;
      commitFinal(view.centerHz + view.viewportOffsetHz + (frac - 0.5) * view.spanHz);
    };

    const onWheel = (e: WheelEvent) => {
      if (e.deltaY === 0 && e.deltaX === 0) return;
      // Always swallow — we don't want the page or a parent container to
      // scroll while the cursor is over the spectrum.
      e.preventDefault();

      const alt = e.altKey;
      const shift = e.shiftKey;

      // Normalise delta units to pixels. Most browsers emit DOM_DELTA_PIXEL
      // (0); some Firefox mouse-wheel builds still emit LINE (1) or PAGE (2).
      const unit = e.deltaMode === 1 ? 40 : e.deltaMode === 2 ? 800 : 1;
      // Many browsers remap shift+wheel to the horizontal axis (deltaY → 0,
      // deltaX carries the motion); prefer whichever axis is non-zero.
      const primary = (e.deltaY !== 0 ? e.deltaY : e.deltaX) * unit;
      wheelAccum += primary;
      if (Math.abs(wheelAccum) < WHEEL_NOTCH_PX) return;
      // One step per emission, regardless of how large the accumulated delta
      // is. A single mouse notch should produce exactly one step — not
      // multiple. Reset the accumulator so momentum-scroll bursts on
      // trackpads don't build up a queue.
      const dir = wheelAccum > 0 ? -1 : 1;
      wheelAccum = 0;

      // Spectrum zoom (shift+wheel) keeps the wheel-forward = zoom OUT
      // convention. Map zoom (alt+wheel) inverts it to match the standard
      // web-map gesture (wheel forward = zoom IN, like Google/Leaflet).
      if (alt) {
        wheelActions.onMapZoom?.(dir);
        return;
      }
      if (shift) {
        nudgeZoom(-dir);
        return;
      }
      nudgeVfo(dir * useToolbarFavoritesStore.getState().stepHz);
    };

    canvas.style.cursor = 'grab';
    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);
    // passive:false so preventDefault() can stop page scrolling.
    canvas.addEventListener('wheel', onWheel, { passive: false });

    return () => {
      if (pendingRaf !== 0) cancelAnimationFrame(pendingRaf);
      cancelPinchRaf();
      pendingAbort?.abort();
      zoomInflight?.abort();
      canvas.removeEventListener('pointerdown', onPointerDown);
      canvas.removeEventListener('pointermove', onPointerMove);
      canvas.removeEventListener('pointerup', onPointerUp);
      canvas.removeEventListener('pointercancel', onPointerUp);
      canvas.removeEventListener('wheel', onWheel);
    };
  }, [canvasRef, wheelActions]);
}
