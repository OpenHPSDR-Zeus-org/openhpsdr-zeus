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

import { useEffect, useRef } from 'react';
import { Minus, Plus } from 'lucide-react';
import {
  WORKSPACE_ZOOM_DEFAULT,
  WORKSPACE_ZOOM_MAX,
  WORKSPACE_ZOOM_MIN,
  WORKSPACE_ZOOM_STEP,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useWorkspaceZoom } from '../util/use-workspace-zoom';

export function WorkspaceZoomControls() {
  const pct = useConnectionStore((s) => s.workspaceZoomPct);
  const { apply, stepBy } = useWorkspaceZoom();

  // Scroll-wheel over the dedicated zoom widget adjusts it directly (no Ctrl
  // needed when the pointer is already on the control). Attached natively
  // because React routes onWheel through a passive root listener, so
  // preventDefault there is a no-op — and preventDefault is what stops the
  // page/browser zoom that a Ctrl+wheel would otherwise trigger here.
  const groupRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = groupRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      if (e.deltaY === 0) return;
      e.preventDefault();
      stepBy(e.deltaY < 0 ? 1 : -1);
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [stepBy]);

  return (
    <div
      ref={groupRef}
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
