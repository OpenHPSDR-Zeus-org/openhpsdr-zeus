// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Shared workspace-zoom driver. `apply` sets an absolute percent (optimistic
// store write + debounced POST + applyState reconcile); `stepBy` nudges the
// current value by one WORKSPACE_ZOOM_STEP in the given direction. Exposed so
// the footer ZOOM buttons (WorkspaceZoomControls) and the Ctrl+wheel handler in
// FlexWorkspace drive the exact same persistence path. Rapid calls (a wheel
// spin) abort the in-flight POST and replace it, so only the final value lands
// on the server. Lives in its own module so a hook can be shared between two
// components without tripping react-refresh's "only export components" rule.

import { useCallback, useEffect, useRef } from 'react';
import {
  setWorkspaceZoom,
  WORKSPACE_ZOOM_DEFAULT,
  WORKSPACE_ZOOM_MAX,
  WORKSPACE_ZOOM_MIN,
  WORKSPACE_ZOOM_STEP,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';

export function useWorkspaceZoom() {
  const setWorkspaceZoomPct = useConnectionStore((s) => s.setWorkspaceZoomPct);
  const abortRef = useRef<AbortController | null>(null);
  useEffect(() => () => abortRef.current?.abort(), []);

  const apply = useCallback(
    (next: number) => {
      const clamped = Math.min(
        WORKSPACE_ZOOM_MAX,
        Math.max(WORKSPACE_ZOOM_MIN, Math.round(next)),
      );
      if (clamped === useConnectionStore.getState().workspaceZoomPct) return;
      setWorkspaceZoomPct(clamped); // optimistic — grid rescales immediately
      abortRef.current?.abort();
      const ctrl = new AbortController();
      abortRef.current = ctrl;
      setWorkspaceZoom(clamped, ctrl.signal)
        .then((s) => {
          if (!ctrl.signal.aborted) useConnectionStore.getState().applyState(s);
        })
        .catch(() => {
          // Network/abort error: keep the optimistic value; the 1 Hz state
          // poll reconciles to server truth on the next tick.
        });
    },
    [setWorkspaceZoomPct],
  );

  const stepBy = useCallback(
    (direction: number) => {
      const cur =
        useConnectionStore.getState().workspaceZoomPct || WORKSPACE_ZOOM_DEFAULT;
      apply(cur + Math.sign(direction) * WORKSPACE_ZOOM_STEP);
    },
    [apply],
  );

  return { apply, stepBy };
}
