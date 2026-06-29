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

import { useEffect, type RefObject } from 'react';
import { setRadioLo } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { useVfoLockStore } from '../state/vfo-lock-store';
import {
  getReceiverVfoFromState,
  getReceiverVfoHz,
  isSecondaryReceiver,
  optimisticSetReceiverVfo,
  postReceiverVfo,
  type ReceiverKey,
} from '../state/receiver-state';
import * as viewCenter from '../state/view-center';

type SpectrumReceiver = ReceiverKey;

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

export function useRulerPanGesture(
  ref: RefObject<HTMLElement | null>,
  receiver: SpectrumReceiver = 'A',
  active = true,
) {
  useEffect(() => {
    if (!active) return;
    const el = ref.current;
    if (!el) return;

    // Receiver-aware seams. RX1's panadapter is centred on the hardware radio
    // LO, so dragging the ruler repositions that LO (setRadioLo / radioLoHz).
    // RX2's DDC follows VFO B with no separate "radio LO", so the B ruler
    // retunes VFO B (setVfoB / vfoBHz) — mirroring the B body-drag in
    // use-pan-tune-gesture. Each receiver glides its OWN view-centre instance.
    const secondary = isSecondaryReceiver(receiver);
    const vc = viewCenter.viewCenterFor(receiver);
    const fallbackCenterHz = () =>
      secondary
        ? getReceiverVfoHz(useConnectionStore.getState(), receiver)
        : Number(selectDisplaySlice(useDisplayStore.getState(), receiver).centerHz);
    const writeCenter = (hz: number) => {
      if (secondary) optimisticSetReceiverVfo(receiver, hz);
      else useConnectionStore.setState({ radioLoHz: hz });
    };
    const postCenter = (hz: number, signal?: AbortSignal) =>
      secondary ? postReceiverVfo(receiver, hz, signal) : setRadioLo(hz, signal);

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

    const readViewport = (): { centerHz: number; spanHz: number } | null => {
      const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
      const width = s.width || s.panDb?.length || 0;
      if (!width || s.hzPerPixel <= 0) return null;
      return {
        centerHz: vc.isInitialized() ? vc.getTargetCenterHz() : fallbackCenterHz(),
        spanHz: width * s.hzPerPixel,
      };
    };

    const commandedLoHz = () =>
      pendingLoHz ?? (vc.isInitialized() ? vc.getTargetCenterHz() : fallbackCenterHz());

    const reconcileAppliedLo = (appliedLoHz: number) => {
      const next = clampHz(appliedLoHz);
      writeCenter(next);
      if (pendingLoHz !== null) return;
      const delta = next - commandedLoHz();
      if (delta !== 0) vc.nudgeTargetHz(delta);
    };

    const flushPending = () => {
      pendingRaf = 0;
      const loHz = pendingLoHz;
      pendingLoHz = null;
      if (loHz == null) return;

      vc.markOptimisticTune();
      writeCenter(loHz);
      pendingAbort?.abort();
      const ctrl = new AbortController();
      pendingAbort = ctrl;
      postCenter(loHz, ctrl.signal)
        .then((state) => {
          if (ctrl.signal.aborted) return;
          useConnectionStore.getState().applyState(state, { trustVfo: false });
          reconcileAppliedLo(secondary ? getReceiverVfoFromState(state, receiver) : state.radioLoHz);
        })
        .catch(() => {});
    };

    const scheduleFlush = () => {
      if (pendingRaf === 0) pendingRaf = requestAnimationFrame(flushPending);
    };

    const queueLo = (nextLoHz: number) => {
      // VFO lock freezes the panadapter view entirely, so a ruler drag never
      // pans — whether it would move the dial (CTUN off / secondary) or only
      // the display window (CTUN on).
      if (useVfoLockStore.getState().locked) return;
      const loHz = clampHz(nextLoHz);
      if (loHz === pendingLoHz) return;
      vc.nudgeTargetHz(loHz - commandedLoHz());
      writeCenter(loHz);
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
  }, [ref, receiver, active]);
}
