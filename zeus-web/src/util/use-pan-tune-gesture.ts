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
import { setVfo, setVfoB, setZoom, ZOOM_MAX, ZOOM_MIN, type ZoomLevel } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { computeSnapToLineHz, getSnapHistorySpectrum, useSignalEnhanceStore } from '../dsp/signal-estimator';
import { armSnapLock } from './snap-lock';
import { useNotchStore } from '../state/notch-store';
import * as viewCenter from '../state/view-center';
import { useToolbarFavoritesStore } from '../state/toolbar-favorites-store';

const MAX_HZ = 60_000_000;
const CLICK_SLOP_PX = 3;
// Pan gestures (click + drag on pan/wf) snap to this step. Typed-freq input
// and band presets bypass it. Ham-friendly default; becomes user-settable
// once the UX exists.
const PAN_STEP_HZ = 500;
// Snap-to-signal reach: a click in snap mode grabs the nearest signal whose
// mode-aware tuning edge sits within this many screen pixels of the cursor
// (converted to Hz at the live scale). Past it, the click tunes normally — so
// clicking empty spectrum still lands where you clicked.
const SNAP_RADIUS_PX = 80;
// Notch painting (NOTCH armed): a drag narrower than this is treated as a
// click and drops a default-width notch on the carrier under the cursor.
const NOTCH_MIN_DRAG_HZ = 50;
const NOTCH_CLICK_WIDTH_HZ = 150;
// Wheel tune step now follows the operator's TuningStepWidget choice
// (toolbar-favorites-store.stepHz). Read at event time inside the wheel
// handler so the latest value applies on every notch. Arrow-key tuning
// in use-keyboard-shortcuts.ts reads the same store, so wheel + arrows
// feel the same.
// Scroll-wheel notches normalise mouse clicks (~100px/tick) and trackpad
// deltas to one discrete tick per this many pixels of deltaY.
const WHEEL_NOTCH_PX = 40;

// Grid snap helper for ordinary click/drag tuning. Snap-to-signal goes through
// resolvePanTuneTarget so click and hover share the same carrier target.
export function snapHz(hz: number): number {
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

export type SpectrumReceiver = 'A' | 'B';

export type PanTuneTarget = {
  tuneHz: number;
  snappedToSignal: boolean;
  fromLive: boolean;
  anchorBodyHz: number;
};

export function resolvePanTuneTarget(
  lineHz: number,
  includeHistory = true,
  receiver: SpectrumReceiver = 'A',
): PanTuneTarget {
  const fallbackHz = snapHz(lineHz);
  const fallback = {
    tuneHz: fallbackHz,
    snappedToSignal: false,
    fromLive: false,
    anchorBodyHz: Number.isFinite(lineHz) ? lineHz : fallbackHz,
  };
  if (!Number.isFinite(lineHz)) return fallback;

  const enhance = useSignalEnhanceStore.getState();
  if (!enhance.snapEnabled) return fallback;

  const ds = selectDisplaySlice(useDisplayStore.getState(), receiver);
  if (!ds.panDb || ds.hzPerPixel <= 0) return fallback;

  const maxRadiusHz = Math.min(ds.hzPerPixel * SNAP_RADIUS_PX, enhance.snapRadiusHz);
  const mode = useConnectionStore.getState().mode;
  const centerHz = Number(ds.centerHz);
  const liveHz = computeSnapToLineHz(
    ds.panDb,
    centerHz,
    ds.hzPerPixel,
    mode,
    lineHz,
    maxRadiusHz,
  );
  if (liveHz != null) {
    return {
      tuneHz: clampHz(Math.round(liveHz)),
      snappedToSignal: true,
      fromLive: true,
      anchorBodyHz: lineHz,
    };
  }

  if (includeHistory && receiver === 'A') {
    const history = getSnapHistorySpectrum();
    if (history) {
      const historyHz = computeSnapToLineHz(history, centerHz, ds.hzPerPixel, mode, lineHz, maxRadiusHz);
      if (historyHz != null) {
        return {
          tuneHz: clampHz(Math.round(historyHz)),
          snappedToSignal: true,
          fromLive: false,
          anchorBodyHz: lineHz,
        };
      }
    }
  }

  return fallback;
}

type VfoNudgeController = {
  commandedHz: () => number;
  nudgeVfo: (deltaHz: number) => void;
  cancel: () => void;
};

export function createVfoNudgeController(receiver: SpectrumReceiver = 'A'): VfoNudgeController {
  const receiverIsB = receiver === 'B';
  let pendingHz: number | null = null;
  let pendingAbort: AbortController | null = null;
  let pendingRaf = 0;

  const readVfo = () => {
    const s = useConnectionStore.getState();
    return receiverIsB ? s.vfoBHz : s.vfoHz;
  };
  const writeVfo = (hz: number) => {
    useConnectionStore.setState(receiverIsB ? { vfoBHz: hz } : { vfoHz: hz });
  };
  const postVfo = (hz: number, signal?: AbortSignal) =>
    receiverIsB ? setVfoB(hz, signal) : setVfo(hz, signal);
  const tunesOffCenter = () => receiverIsB || useConnectionStore.getState().ctunEnabled;
  const commandedHz = () => pendingHz ?? readVfo();

  const flushPending = () => {
    pendingRaf = 0;
    const hz = pendingHz;
    pendingHz = null;
    if (hz == null) return;
    viewCenter.markOptimisticTune();
    writeVfo(hz);
    pendingAbort?.abort();
    const ctrl = new AbortController();
    pendingAbort = ctrl;
    postVfo(hz, ctrl.signal).catch(() => {});
  };

  const scheduleFlush = () => {
    if (pendingRaf === 0) pendingRaf = requestAnimationFrame(flushPending);
  };

  const nudgeVfo = (deltaHz: number) => {
    const cur = commandedHz();
    const next = clampHz(cur + deltaHz);
    if (tunesOffCenter()) {
      viewCenter.markOptimisticTune();
    } else {
      viewCenter.nudgeTargetHz(next - cur);
    }
    writeVfo(next);
    pendingHz = next;
    scheduleFlush();
  };

  const cancel = () => {
    if (pendingRaf !== 0) {
      cancelAnimationFrame(pendingRaf);
      pendingRaf = 0;
    }
    pendingAbort?.abort();
    pendingAbort = null;
    pendingHz = null;
  };

  return { commandedHz, nudgeVfo, cancel };
}

export type PanTuneGestureOptions = {
  touchMode?: 'normal' | 'pinch-only';
  tuneReceiver?: SpectrumReceiver;
};

function readView(receiver: SpectrumReceiver = 'A'): { centerHz: number; spanHz: number } | null {
  const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
  if (!s.panDb || s.hzPerPixel <= 0) return null;
  return {
    centerHz: Number(s.centerHz),
    spanHz: s.panDb.length * s.hzPerPixel,
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
  receiver: SpectrumReceiver = 'A',
  options: PanTuneGestureOptions = {},
) {
  const wheelActions = useContext(SpectrumWheelActionsContext);
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const tuneReceiver = options.tuneReceiver ?? receiver;
    const receiverIsB = tuneReceiver === 'B';
    const touchPinchOnly = options.touchMode === 'pinch-only';

    type Drag = { startX: number; startHz: number; spanHz: number; moved: boolean };
    type MapDrag = { lastX: number; lastY: number };
    type Pinch = {
      baseDist: number;     // pointer separation when the pinch began (px)
      baseZoom: number;     // zoom level when the pinch began
      pendingZoom: number | null;
      raf: number;
    };
    let drag: Drag | null = null;
    // Notch painting: while NOTCH is armed, a pointer drag defines a notch
    // band (start..current frequency) instead of tuning. startHz is captured
    // at pointerdown; the live preview is published to the notch store for
    // NotchOverlay to render.
    let notchDrag: { startHz: number } | null = null;
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

    // The commanded-frequency chain: pendingHz when a POST is queued, else
    // the optimistic store value. All view-center nudges are DELTAS against
    // this chain (never against the lagging frame center), so the display
    // target always mirrors exactly what was commanded — including clamp
    // effects at the band edges — and CW pitch offsets cancel (issue #597
    // adversary #2/#12).
    const readVfo = () => {
      const s = useConnectionStore.getState();
      return receiverIsB ? s.vfoBHz : s.vfoHz;
    };
    const writeVfo = (hz: number) => {
      useConnectionStore.setState(receiverIsB ? { vfoBHz: hz } : { vfoHz: hz });
    };
    const postVfo = (hz: number, signal?: AbortSignal) =>
      receiverIsB ? setVfoB(hz, signal) : setVfo(hz, signal);
    const tunesOffCenter = () => receiverIsB || useConnectionStore.getState().ctunEnabled;

    const commandedHz = () => pendingHz ?? readVfo();

    const flushPending = () => {
      pendingRaf = 0;
      const hz = pendingHz;
      pendingHz = null;
      if (hz == null) return;
      viewCenter.markOptimisticTune();
      writeVfo(hz);
      pendingAbort?.abort();
      const ctrl = new AbortController();
      pendingAbort = ctrl;
      postVfo(hz, ctrl.signal).catch(() => {});
    };

    const scheduleFlush = () => {
      if (pendingRaf === 0) pendingRaf = requestAnimationFrame(flushPending);
    };

    // `exact` skips the PAN_STEP_HZ grid — used by snap-to-signal so the dial
    // lands on the measured carrier, not the nearest round 500 Hz.
    const commitFinal = (hz: number, exact = false) => {
      const snapped = exact ? clampHz(hz) : snapHz(hz);
      // CTUN: tune the dial off-centre without moving the view. The hardware
      // NCO stays frozen on the backend, so the frame center doesn't move and
      // the dial marker (vfo − targetCenter) roams off the zero line. We stamp
      // the optimistic-tune clock (poll-guard) but deliberately skip the
      // view-center nudge that would recentre the display.
      if (tunesOffCenter()) {
        viewCenter.markOptimisticTune();
      } else {
        // Delta against the commanded chain — NOT an absolute write into the
        // view-center (frame centers are dial ∓ cw-pitch in CW; absolutes
        // would oscillate the display by ±pitch on every CW commit).
        viewCenter.nudgeTargetHz(snapped - commandedHz());
      }
      writeVfo(snapped);
      pendingAbort?.abort();
      pendingAbort = null;
      if (pendingRaf !== 0) {
        cancelAnimationFrame(pendingRaf);
        pendingRaf = 0;
      }
      pendingHz = null;
      postVfo(snapped)
        .then((s) => useConnectionStore.getState().applyState(s))
        .catch(() => {});
    };

    // Wheel-driven VFO nudge: fine-tune step, no snap to PAN_STEP_HZ. Coalesces
    // to one POST per rAF via the same pending pipeline as drag-to-pan.
    const nudgeVfo = (deltaHz: number) => {
      const cur = commandedHz();
      const next = clampHz(cur + deltaHz);
      // CTUN: wheel-tune the dial off-centre with the view frozen (same model
      // as commitFinal). Outside CTUN, nudge the view target by the effective
      // delta (post-clamp) so the display can never run past a clamped band
      // edge; the optimistic store write happens in the SAME synchronous block
      // as the target nudge so the dial marker's (vfo − target) offset is never
      // transiently stale (it is pinned to the center line during glides).
      if (tunesOffCenter()) {
        viewCenter.markOptimisticTune();
      } else {
        viewCenter.nudgeTargetHz(next - cur);
      }
      writeVfo(next);
      pendingHz = next;
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
      if (touchPinchOnly && e.pointerType === 'touch') {
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
      const view = readView(receiver);
      if (!view) return;
      // NOTCH armed: paint a notch band instead of tuning. Capture the start
      // frequency; move/up build the band.
      if (useNotchStore.getState().armed) {
        e.preventDefault();
        try { canvas.setPointerCapture(e.pointerId); } catch { /* ok */ }
        const rect = canvas.getBoundingClientRect();
        const frac = rect.width > 0 ? (e.clientX - rect.left) / rect.width : 0.5;
        notchDrag = { startHz: view.centerHz + (frac - 0.5) * view.spanHz };
        canvas.style.cursor = 'crosshair';
        return;
      }
      e.preventDefault();
      try {
        canvas.setPointerCapture(e.pointerId);
      } catch {
        /* synthetic events don't have an active pointer; real mouse/touch does */
      }
      drag = {
        startX: e.clientX,
        startHz: view.centerHz,
        spanHz: view.spanHz,
        moved: false,
      };
      canvas.style.cursor = 'crosshair';
    };

    const onPointerMove = (e: PointerEvent) => {
      const p = pointers.get(e.pointerId);
      if (p) {
        p.x = e.clientX;
        p.y = e.clientY;
      }
      if (notchDrag) {
        const rect = canvas.getBoundingClientRect();
        const view = readView(receiver);
        if (!view || rect.width <= 0) return;
        const frac = (e.clientX - rect.left) / rect.width;
        const curHz = view.centerHz + (frac - 0.5) * view.spanHz;
        useNotchStore.getState().setPending({
          centerHz: (notchDrag.startHz + curHz) / 2,
          widthHz: Math.abs(curHz - notchDrag.startHz),
        });
        return;
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
      // CTUN/RX2: drag sweeps the selected dial across the frozen spectrum. The frame center
      // doesn't move (NCO frozen on the backend), so drag.startHz — the view
      // center captured at grab — is stationary; resolve the live pointer X to
      // a frequency against it and tune there, leaving the view put.
      if (tunesOffCenter()) {
        const frac = (e.clientX - rect.left) / rect.width;
        const cursorHz = snapHz(drag.startHz + (frac - 0.5) * drag.spanHz);
        if (cursorHz !== pendingHz) {
          viewCenter.markOptimisticTune();
          writeVfo(cursorHz);
          pendingHz = cursorHz;
          scheduleFlush();
        }
        return;
      }
      const newHz = snapHz(drag.startHz - (dx / rect.width) * drag.spanHz);
      // Pixel→Hz mapping stays display-relative (drag.startHz is the frame
      // center at grab — unchanged semantics); the view-center moves by the
      // COMMANDED delta, initialised from the live commanded value, never
      // from the lagging frame center (adversary #12: prevents a one-time
      // backward jump when a drag starts mid-wheel-glide). The display then
      // glides between the unchanged 500 Hz snap points.
      if (newHz !== pendingHz) {
        viewCenter.nudgeTargetHz(newHz - commandedHz());
        // Atomic with the nudge — keeps the marker's (vfo − target) offset
        // consistent within the frame (see nudgeVfo).
        writeVfo(newHz);
        pendingHz = newHz;
        scheduleFlush();
      }
    };

    const onPointerUp = (e: PointerEvent) => {
      pointers.delete(e.pointerId);
      if (notchDrag) {
        const start = notchDrag;
        notchDrag = null;
        canvas.style.cursor = 'crosshair';
        if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
        const ns = useNotchStore.getState();
        ns.setPending(null);
        // pointercancel discards the in-progress notch; a real release commits.
        if (e.type !== 'pointercancel') {
          const rect = canvas.getBoundingClientRect();
          const view = readView(receiver);
          if (view && rect.width > 0) {
            const frac = (e.clientX - rect.left) / rect.width;
            const curHz = view.centerHz + (frac - 0.5) * view.spanHz;
            const widthHz = Math.abs(curHz - start.startHz);
            if (widthHz >= NOTCH_MIN_DRAG_HZ) {
              ns.addNotch((start.startHz + curHz) / 2, widthHz);
            } else {
              // Tiny drag / click → drop a default-width notch on the carrier.
              ns.addNotch(start.startHz, NOTCH_CLICK_WIDTH_HZ);
            }
          }
        }
        return;
      }
      if (pinch) {
        if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
        if (pointers.size < 2) {
          // End of pinch — discard any in-flight rAF and let the user lift +
          // re-touch to start a fresh drag-to-tune. Re-entering drag from a
          // post-pinch single finger leads to a jump tune, which is worse
          // than an enforced clean break.
          cancelPinchRaf();
          pinch = null;
          canvas.style.cursor = 'crosshair';
        }
        return;
      }
      if (mapDrag) {
        mapDrag = null;
        canvas.style.cursor = 'crosshair';
        if (canvas.hasPointerCapture(e.pointerId)) canvas.releasePointerCapture(e.pointerId);
        return;
      }
      const d = drag;
      if (!d) return;
      drag = null;
      canvas.style.cursor = 'crosshair';
      if (canvas.hasPointerCapture(e.pointerId)) {
        canvas.releasePointerCapture(e.pointerId);
      }
      const rect = canvas.getBoundingClientRect();
      if (rect.width <= 0) return;
      if (d.moved) {
        if (tunesOffCenter()) {
          // CTUN/RX2: commit the selected dial at the release-point frequency (cursor-
          // relative against the frozen view center), matching the drag-move
          // sweep above.
          const frac = (e.clientX - rect.left) / rect.width;
          commitFinal(d.startHz + (frac - 0.5) * d.spanHz);
        } else {
          const dx = e.clientX - d.startX;
          commitFinal(d.startHz - (dx / rect.width) * d.spanHz);
        }
      } else {
        // click-to-tune: resolve the clicked frequency against the live view.
        const view = readView(receiver);
        if (!view) return;
        const frac = (e.clientX - rect.left) / rect.width;
        const clickHz = view.centerHz + (frac - 0.5) * view.spanHz;
        // Snap-to-signal: when enabled, favour the signal body under/near the
        // cursor and return that signal's mode-aware tuning frequency. The
        // shared resolver also drives the hover preview so it cannot advertise
        // one target and commit another.
        const target = resolvePanTuneTarget(clickHz, true, receiver);
        if (target.snappedToSignal) {
          commitFinal(target.tuneHz, true);
          // Only a LIVE signal can be tracked frame-to-frame; a waterfall-memory
          // hit is by definition not on screen right now, so it tunes once but
          // does not arm the self-correcting lock.
          if (target.fromLive && !receiverIsB && receiver === 'A') {
            armSnapLock({
              dialHz: target.tuneHz,
              anchorBodyHz: target.anchorBodyHz,
              mode: useConnectionStore.getState().mode,
            });
          }
          return;
        }
        commitFinal(clickHz);
      }
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

    canvas.style.cursor = 'crosshair';
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
  }, [canvasRef, receiver, wheelActions, options.touchMode, options.tuneReceiver]);
}
