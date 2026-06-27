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
// Zeus is an independent reimplementation in .NET — not a fork. See
// ATTRIBUTIONS.md for the full provenance statement.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { setRadioLo, setReceiverLo, type ZoomLevel } from '../api/client';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';
import { getReceiverVfoHz, KIWI_RECEIVER_INDEX, rxIndexOf, type ReceiverKey } from '../state/receiver-state';
import * as viewCenter from '../state/view-center';

const MAX_HZ = 60_000_000;

function clampHz(hz: number): number {
  if (!Number.isFinite(hz)) return 0;
  return Math.min(MAX_HZ, Math.max(0, hz));
}

function effectiveLoHz(mode: string, vfoHz: number, cwPitchHz: number): number {
  const pitch = Number.isFinite(cwPitchHz) ? cwPitchHz : 600;
  if (mode === 'CWU') return vfoHz - pitch;
  if (mode === 'CWL') return vfoHz + pitch;
  return vfoHz;
}

function currentViewCenterHz(): number {
  const s = selectDisplaySlice(useDisplayStore.getState(), 'A');
  const vc = viewCenter.viewCenterFor('A');
  return vc.isInitialized() ? vc.getTargetCenterHz() : Number(s.centerHz);
}

export function applyCtunZoomCenterAfterState(loHz: number | null): void {
  if (loHz == null || !useConnectionStore.getState().ctunEnabled) return;
  applyLocalCtunZoomCenter(loHz);
}

export function centerCtunForZoomIn(
  currentZoom: ZoomLevel,
  nextZoom: ZoomLevel,
  signal?: AbortSignal,
): number | null {
  if (nextZoom <= currentZoom) return null;
  const s = useConnectionStore.getState();
  if (!s.ctunEnabled) return null;

  const targetLoHz = clampHz(effectiveLoHz(s.mode, s.vfoHz, s.cwPitchHz));
  const loMoves = Math.abs(targetLoHz - s.radioLoHz) > 0.5;
  const viewMoves = Math.abs(targetLoHz - currentViewCenterHz()) > 0.5;
  if (!loMoves && !viewMoves) return null;

  applyLocalCtunZoomCenter(targetLoHz);
  if (loMoves) {
    setRadioLo(targetLoHz, signal)
      .then((state) => {
        if (signal?.aborted) return;
        applyLocalCtunZoomCenter(state.radioLoHz);
      })
      .catch(() => {});
  }
  return targetLoHz;
}

// On zoom-IN under CTUN, re-centre the Kiwi slice on its own dial. The global
// ZoomControl only re-centres RX1 (centerCtunForZoomIn above), so without this
// the Kiwi's frozen CTUN waterfall centre stays put while the span shrinks — its
// dial (and the passband overlay that hangs off it) drift to the pane edge as
// you zoom in (operator report: "zoom breaks the Kiwi filter + waterfall").
// No-op when zooming out, CTUN off, or the Kiwi slice isn't enabled. Mirrors the
// RX1 path but targets the Kiwi's independent waterfall centre via SetCenter.
export function centerKiwiForZoomIn(currentZoom: ZoomLevel, nextZoom: ZoomLevel): void {
  if (nextZoom <= currentZoom) return;
  const s = useConnectionStore.getState();
  if (!s.ctunEnabled) return;
  if (!s.receivers.some((r) => r.index === KIWI_RECEIVER_INDEX)) return;
  panReceiverCenterTo(KIWI_RECEIVER_INDEX, getReceiverVfoHz(s, KIWI_RECEIVER_INDEX));
}

// Coalesce overlapping autopan POSTs — a fast tune burst can queue several
// recentre commands; only the last one's applied LO should win.
let autopanAbort: AbortController | null = null;

/** Glide the CTUN view centre (hardware LO) to `loHz`, locally and on the
 *  backend. Shares applyLocalCtunZoomCenter so the view-centre tween and the
 *  radioLoHz store stay in lockstep exactly like the zoom-in recentre path.
 *  No-op when CTUN is off (the view follows the dial there, so there is
 *  nothing to recentre). Used by the filter-autopan keep-in-view logic. */
export function panCtunCenterTo(loHz: number): void {
  if (!useConnectionStore.getState().ctunEnabled) return;
  const next = clampHz(loHz);
  applyLocalCtunZoomCenter(next);
  autopanAbort?.abort();
  const ctrl = new AbortController();
  autopanAbort = ctrl;
  setRadioLo(next, ctrl.signal)
    .then((state) => {
      if (!ctrl.signal.aborted) applyLocalCtunZoomCenter(state.radioLoHz);
    })
    .catch(() => {});
}

// Per-receiver autopan POST coalescing — one in-flight recentre per RX index.
const secondaryLoAborts = new Map<number, AbortController>();

/** Glide ANY receiver's view centre to `hz` to keep its dial/filter in view.
 *  RX1 (index 0) recentres the hardware NCO via {@link panCtunCenterTo};
 *  secondary receivers glide their own view-centre tween optimistically and
 *  POST the new DDC centre (server no-ops on P1 / CTUN-off, where frames then
 *  reconcile the glide back). No-op outside CTUN — the view follows the dial
 *  there, so there is nothing to recentre. */
export function panReceiverCenterTo(receiver: ReceiverKey, hz: number): void {
  const idx = rxIndexOf(receiver);
  if (idx === 0) {
    panCtunCenterTo(hz);
    return;
  }
  if (!useConnectionStore.getState().ctunEnabled) return;
  const next = clampHz(hz);
  const vc = viewCenter.viewCenterFor(receiver);
  const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
  if (vc.isInitialized()) {
    vc.nudgeTargetHz(next - vc.getTargetCenterHz());
  } else {
    vc.snapTo(next, s.hzPerPixel > 0 ? s.hzPerPixel : undefined);
    vc.markOptimisticTune();
  }
  secondaryLoAborts.get(idx)?.abort();
  const ctrl = new AbortController();
  secondaryLoAborts.set(idx, ctrl);
  setReceiverLo(idx, next, ctrl.signal).catch(() => {});
}

function applyLocalCtunZoomCenter(loHz: number): void {
  const nextLoHz = clampHz(loHz);
  const s = selectDisplaySlice(useDisplayStore.getState(), 'A');
  const vc = viewCenter.viewCenterFor('A');
  if (vc.isInitialized()) {
    vc.nudgeTargetHz(nextLoHz - vc.getTargetCenterHz());
  } else {
    vc.snapTo(nextLoHz, s.hzPerPixel > 0 ? s.hzPerPixel : undefined);
    vc.markOptimisticTune();
  }
  useConnectionStore.setState({ radioLoHz: nextLoHz });
}
