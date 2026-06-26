// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Workspace-zoom cluster (ZOOM − | nn% | +) for the bottom transport bar. Steps
// the server-persisted WorkspaceZoomPct, which scales the panel-grid cell pitch
// in FlexWorkspace; the percent readout doubles as a reset-to-100% button.
// Optimistic store write + POST + applyState reconcile, mirroring how the
// spectral-zoom controls talk to the server. Lives in the footer beside the PA
// temperature chip so workspace-level status reads together.

import { useCallback, useEffect, useRef } from 'react';
import { Minus, Plus } from 'lucide-react';
import {
  setWorkspaceZoom,
  WORKSPACE_ZOOM_DEFAULT,
  WORKSPACE_ZOOM_MAX,
  WORKSPACE_ZOOM_MIN,
  WORKSPACE_ZOOM_STEP,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';

export function WorkspaceZoomControls() {
  const pct = useConnectionStore((s) => s.workspaceZoomPct);
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

  return (
    <div
      className="workspace-zoom-controls hide-mobile"
      role="group"
      aria-label="Workspace zoom"
    >
      <span className="k">ZOOM</span>
      <button
        type="button"
        className="workspace-zoom-btn"
        onClick={() => apply(pct - WORKSPACE_ZOOM_STEP)}
        disabled={pct <= WORKSPACE_ZOOM_MIN}
        title="Zoom workspace out"
        aria-label="Zoom workspace out"
      >
        <Minus size={12} strokeWidth={2.4} aria-hidden />
      </button>
      <button
        type="button"
        className="workspace-zoom-readout"
        onClick={() => apply(WORKSPACE_ZOOM_DEFAULT)}
        title="Reset workspace zoom to 100%"
        aria-label={`Workspace zoom ${pct}% — click to reset to 100%`}
      >
        {pct}%
      </button>
      <button
        type="button"
        className="workspace-zoom-btn"
        onClick={() => apply(pct + WORKSPACE_ZOOM_STEP)}
        disabled={pct >= WORKSPACE_ZOOM_MAX}
        title="Zoom workspace in"
        aria-label="Zoom workspace in"
      >
        <Plus size={12} strokeWidth={2.4} aria-hidden />
      </button>
    </div>
  );
}
