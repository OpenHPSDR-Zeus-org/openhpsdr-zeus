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

import { useEffect, type RefObject } from 'react';
import { setRadioLo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import * as viewCenter from '../state/view-center';

const MAX_HZ = 60_000_000;
const CLICK_SLOP_PX = 3;

function clampHz(hz: number): number {
  if (!Number.isFinite(hz)) return 0;
  return Math.min(MAX_HZ, Math.max(0, Math.round(hz)));
}

export function rulerDragTargetHz(
  startCenterHz: number,
  startX: number,
  currentX: number,
  widthPx: number,
  spanHz: number,
): number {
  if (!Number.isFinite(widthPx) || widthPx <= 0 || !Number.isFinite(spanHz) || spanHz <= 0) {
    return clampHz(startCenterHz);
  }
  return clampHz(startCenterHz - ((currentX - startX) / widthPx) * spanHz);
}

function readViewport(): { centerHz: number; spanHz: number } | null {
  const s = useDisplayStore.getState();
  const width = s.width || s.panDb?.length || 0;
  if (!width || s.hzPerPixel <= 0) return null;
  return {
    centerHz: viewCenter.isInitialized()
      ? viewCenter.getTargetCenterHz()
      : Number(s.centerHz),
    spanHz: width * s.hzPerPixel,
  };
}

export function useRulerPanGesture(
  ref: RefObject<HTMLElement | null>,
  active = true,
) {
  useEffect(() => {
    if (!active) return;
    const el = ref.current;
    if (!el) return;

    type Drag = {
      pointerId: number;
      startX: number;
      startCenterHz: number;
      spanHz: number;
      moved: boolean;
    };

    let drag: Drag | null = null;
    let pendingLoHz: number | null = null;
    let pendingRaf = 0;
    let pendingAbort: AbortController | null = null;

    const commandedLoHz = () =>
      pendingLoHz ?? (viewCenter.isInitialized()
        ? viewCenter.getTargetCenterHz()
        : Number(useDisplayStore.getState().centerHz));

    const reconcileAppliedLo = (appliedLoHz: number) => {
      const next = clampHz(appliedLoHz);
      useConnectionStore.setState({ radioLoHz: next });
      if (pendingLoHz !== null) return;
      const delta = next - commandedLoHz();
      if (delta !== 0) viewCenter.nudgeTargetHz(delta);
    };

    const flushPending = () => {
      pendingRaf = 0;
      const loHz = pendingLoHz;
      pendingLoHz = null;
      if (loHz == null) return;

      viewCenter.markOptimisticTune();
      useConnectionStore.setState({ radioLoHz: loHz });
      pendingAbort?.abort();
      const ctrl = new AbortController();
      pendingAbort = ctrl;
      setRadioLo(loHz, ctrl.signal)
        .then((state) => {
          if (ctrl.signal.aborted) return;
          useConnectionStore.getState().applyState(state, { trustVfo: false });
          reconcileAppliedLo(state.radioLoHz);
        })
        .catch(() => {});
    };

    const scheduleFlush = () => {
      if (pendingRaf === 0) pendingRaf = requestAnimationFrame(flushPending);
    };

    const queueLo = (nextLoHz: number) => {
      const loHz = clampHz(nextLoHz);
      if (loHz === pendingLoHz) return;
      viewCenter.nudgeTargetHz(loHz - commandedLoHz());
      useConnectionStore.setState({ radioLoHz: loHz });
      pendingLoHz = loHz;
      scheduleFlush();
    };

    const onPointerDown = (e: PointerEvent) => {
      if (e.button !== 0) return;
      const view = readViewport();
      if (!view) return;
      e.preventDefault();
      try { el.setPointerCapture(e.pointerId); } catch { /* ok */ }
      drag = {
        pointerId: e.pointerId,
        startX: e.clientX,
        startCenterHz: view.centerHz,
        spanHz: view.spanHz,
        moved: false,
      };
      el.style.cursor = 'grabbing';
    };

    const onPointerMove = (e: PointerEvent) => {
      const d = drag;
      if (!d || e.pointerId !== d.pointerId) return;
      const dx = e.clientX - d.startX;
      if (!d.moved && Math.abs(dx) <= CLICK_SLOP_PX) return;
      d.moved = true;
      const rect = el.getBoundingClientRect();
      queueLo(rulerDragTargetHz(d.startCenterHz, d.startX, e.clientX, rect.width, d.spanHz));
    };

    const onPointerUp = (e: PointerEvent) => {
      const d = drag;
      if (!d || e.pointerId !== d.pointerId) return;
      drag = null;
      el.style.cursor = 'grab';
      try { el.releasePointerCapture(e.pointerId); } catch { /* ok */ }
      if (!d.moved) return;
      const rect = el.getBoundingClientRect();
      queueLo(rulerDragTargetHz(d.startCenterHz, d.startX, e.clientX, rect.width, d.spanHz));
      if (pendingRaf !== 0) {
        cancelAnimationFrame(pendingRaf);
        flushPending();
      }
    };

    el.style.cursor = 'grab';
    el.addEventListener('pointerdown', onPointerDown);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerup', onPointerUp);
    el.addEventListener('pointercancel', onPointerUp);

    return () => {
      if (pendingRaf !== 0) cancelAnimationFrame(pendingRaf);
      pendingAbort?.abort();
      el.removeEventListener('pointerdown', onPointerDown);
      el.removeEventListener('pointermove', onPointerMove);
      el.removeEventListener('pointerup', onPointerUp);
      el.removeEventListener('pointercancel', onPointerUp);
    };
  }, [ref, active]);
}
