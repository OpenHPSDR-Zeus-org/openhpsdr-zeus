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

import { setRadioLo, type ZoomLevel } from '../api/client';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';
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
