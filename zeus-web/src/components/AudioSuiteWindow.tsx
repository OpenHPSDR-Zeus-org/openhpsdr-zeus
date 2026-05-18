// SPDX-License-Identifier: GPL-2.0-or-later
//
// AudioSuiteWindow — draggable floating window containing the audio
// plugin chain. Replaces the inline rendering that used to live in
// TxAudioToolsPanel (per Phase 2 of issue #332). Operators open it
// via the "Audio Suite" button on TX Audio Tools, drag tiles at the
// top to reorder the chain, toggle audition to hear the chain output
// in their RX playback path, and adjust per-plugin settings in the
// stacked panels below.
//
// Drag-and-drop is vanilla HTML5 (no npm dep per CLAUDE.md red-line
// on new deps). Window dragging uses Pointer Events with capture.
// All chrome is in Zeus tokens — no raw hex per the design rules in
// docs/lessons/dev-conventions.md.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { usePluginPanels } from '../plugins/runtime/usePluginPanels';
import type { RegisteredPluginPanel } from '../plugins/runtime/pluginRuntime';
import {
  AUDIO_SUITE_WINDOW_MIN_WIDTH,
  AUDIO_SUITE_WINDOW_MIN_HEIGHT,
  useAudioSuiteStore,
} from '../state/audio-suite-store';

const CHAIN_SLOT = 'tx-audio-tools.chain';

/** Edge codes for the resize handles — 4 edges + 4 corners. */
type ResizeEdge = 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw';

const RESIZE_HANDLE_PX = 6; // grab thickness for each edge

const CURSOR_FOR_EDGE: Record<ResizeEdge, string> = {
  n: 'ns-resize',
  s: 'ns-resize',
  e: 'ew-resize',
  w: 'ew-resize',
  ne: 'nesw-resize',
  sw: 'nesw-resize',
  nw: 'nwse-resize',
  se: 'nwse-resize',
};

/**
 * Compute the absolute-position style for a resize handle on the
 * given edge. Handles cover only their edge / corner — clicks in
 * the interior pass through to the window content. Corners get a
 * faint L-shaped border in --fg-3 as a discoverability hint so
 * operators see "there's a resize handle here" without the cursor
 * change being the only cue.
 */
function handleStyleFor(edge: ResizeEdge): React.CSSProperties {
  const base: React.CSSProperties = {
    position: 'absolute',
    zIndex: 1,
    cursor: CURSOR_FOR_EDGE[edge],
    touchAction: 'none',
  };
  const cornerBorder = '1px solid var(--fg-3)';
  switch (edge) {
    case 'n':  return { ...base, top: 0, left: RESIZE_HANDLE_PX, right: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX };
    case 's':  return { ...base, bottom: 0, left: RESIZE_HANDLE_PX, right: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX };
    case 'e':  return { ...base, top: RESIZE_HANDLE_PX, bottom: RESIZE_HANDLE_PX, right: 0, width: RESIZE_HANDLE_PX };
    case 'w':  return { ...base, top: RESIZE_HANDLE_PX, bottom: RESIZE_HANDLE_PX, left: 0, width: RESIZE_HANDLE_PX };
    case 'ne': return { ...base, top: 0, right: 0, width: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX,
                        borderTop: cornerBorder, borderRight: cornerBorder, opacity: 0.6 };
    case 'nw': return { ...base, top: 0, left: 0, width: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX,
                        borderTop: cornerBorder, borderLeft: cornerBorder, opacity: 0.6 };
    case 'se': return { ...base, bottom: 0, right: 0, width: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX,
                        borderBottom: cornerBorder, borderRight: cornerBorder, opacity: 0.6 };
    case 'sw': return { ...base, bottom: 0, left: 0, width: RESIZE_HANDLE_PX, height: RESIZE_HANDLE_PX,
                        borderBottom: cornerBorder, borderLeft: cornerBorder, opacity: 0.6 };
  }
}

/**
 * One resize handle. Invisible 6px region on its assigned edge /
 * corner; the cursor changes on hover so operators can see where
 * the grab regions are. Pointer Events with capture so the drag
 * keeps tracking even if the cursor leaves the handle while
 * dragging.
 */
function ResizeHandle({ edge }: { edge: ResizeEdge }) {
  const x = useAudioSuiteStore((s) => s.x);
  const y = useAudioSuiteStore((s) => s.y);
  const width = useAudioSuiteStore((s) => s.width);
  const height = useAudioSuiteStore((s) => s.height);
  const setPosition = useAudioSuiteStore((s) => s.setPosition);
  const setSize = useAudioSuiteStore((s) => s.setSize);

  const dragRef = useRef<{
    pointerId: number;
    startX: number;
    startY: number;
    origX: number;
    origY: number;
    origW: number;
    origH: number;
  } | null>(null);

  const onPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      e.stopPropagation();
      e.preventDefault();
      e.currentTarget.setPointerCapture(e.pointerId);
      dragRef.current = {
        pointerId: e.pointerId,
        startX: e.clientX,
        startY: e.clientY,
        origX: x, origY: y,
        origW: width, origH: height,
      };
    },
    [x, y, width, height],
  );

  const onPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragRef.current;
      if (!d || d.pointerId !== e.pointerId) return;
      const dx = e.clientX - d.startX;
      const dy = e.clientY - d.startY;
      let nX = d.origX;
      let nY = d.origY;
      let nW = d.origW;
      let nH = d.origH;
      if (edge.includes('e')) nW = d.origW + dx;
      if (edge.includes('s')) nH = d.origH + dy;
      if (edge.includes('w')) { nX = d.origX + dx; nW = d.origW - dx; }
      if (edge.includes('n')) { nY = d.origY + dy; nH = d.origH - dy; }

      // Enforce minimums; when shrinking from the left / top, prevent
      // the window's x/y from drifting past the would-be max.
      if (nW < AUDIO_SUITE_WINDOW_MIN_WIDTH) {
        if (edge.includes('w')) nX = d.origX + d.origW - AUDIO_SUITE_WINDOW_MIN_WIDTH;
        nW = AUDIO_SUITE_WINDOW_MIN_WIDTH;
      }
      if (nH < AUDIO_SUITE_WINDOW_MIN_HEIGHT) {
        if (edge.includes('n')) nY = d.origY + d.origH - AUDIO_SUITE_WINDOW_MIN_HEIGHT;
        nH = AUDIO_SUITE_WINDOW_MIN_HEIGHT;
      }

      // Edges that include a dimension update push both position
      // and size in one render; setting them in order so the store
      // sees the combined change as a single React render.
      setPosition(nX, nY);
      setSize(nW, nH);
    },
    [edge, setPosition, setSize],
  );

  const onPointerUp = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragRef.current;
      if (!d || d.pointerId !== e.pointerId) return;
      try { e.currentTarget.releasePointerCapture(e.pointerId); } catch {}
      dragRef.current = null;
    },
    [],
  );

  return (
    <div
      data-no-drag
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      style={handleStyleFor(edge)}
    />
  );
}

const RESIZE_EDGES: ResizeEdge[] = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'];

/**
 * Short display name for a chain tile, derived from the plugin ID.
 * Recognised v1/v2 plugins get a hand-tuned label; others fall back
 * to the panel title or the trailing segment of the plugin ID.
 */
function shortLabelFor(pluginId: string, panelTitle: string): string {
  switch (pluginId) {
    case 'com.openhpsdr.zeus.samples.gate':       return 'GATE';
    case 'com.openhpsdr.zeus.samples.downexp':    return 'D-EXP';
    case 'com.openhpsdr.zeus.samples.tube':       return 'TUBE';
    case 'com.openhpsdr.zeus.samples.eq':         return 'EQ';
    case 'com.openhpsdr.zeus.samples.compressor': return 'COMP';
    case 'com.openhpsdr.zeus.samples.exciter':    return 'EXCITER';
    case 'com.openhpsdr.zeus.samples.bass':       return 'BASS';
    case 'com.openhpsdr.zeus.samples.reverb':     return 'REVERB';
    default:
      if (panelTitle && panelTitle.length > 0) return panelTitle.toUpperCase();
      const seg = pluginId.split('.').pop() ?? pluginId;
      return seg.toUpperCase().slice(0, 8);
  }
}

/**
 * Sort the chain panels into the canonical order from the store.
 * Plugins present locally but missing from the canonical order
 * (e.g. server hasn't pushed an update yet) sort to the end in
 * panel-id order for determinism.
 */
function sortChainPanels(
  panels: RegisteredPluginPanel[],
  canonicalOrder: string[],
): RegisteredPluginPanel[] {
  const orderIndex = new Map<string, number>();
  canonicalOrder.forEach((id, i) => orderIndex.set(id, i));
  return [...panels].sort((a, b) => {
    const ia = orderIndex.get(a.pluginId) ?? Number.POSITIVE_INFINITY;
    const ib = orderIndex.get(b.pluginId) ?? Number.POSITIVE_INFINITY;
    if (ia !== ib) return ia - ib;
    return a.panelId.localeCompare(b.panelId);
  });
}

export function AudioSuiteWindow() {
  const isOpen = useAudioSuiteStore((s) => s.isOpen);
  const close = useAudioSuiteStore((s) => s.close);
  const x = useAudioSuiteStore((s) => s.x);
  const y = useAudioSuiteStore((s) => s.y);
  const width = useAudioSuiteStore((s) => s.width);
  const height = useAudioSuiteStore((s) => s.height);
  const setPosition = useAudioSuiteStore((s) => s.setPosition);
  const setDragging = useAudioSuiteStore((s) => s.setDragging);
  const chainOrder = useAudioSuiteStore((s) => s.chainOrder);
  const reorderChain = useAudioSuiteStore((s) => s.reorderChain);
  const loadChainOrderFromServer = useAudioSuiteStore(
    (s) => s.loadChainOrderFromServer,
  );
  const auditionSupported = useAudioSuiteStore((s) => s.auditionSupported);
  const auditionEnabled = useAudioSuiteStore((s) => s.auditionEnabled);
  const setAuditionEnabled = useAudioSuiteStore((s) => s.setAuditionEnabled);
  const loadAuditionState = useAudioSuiteStore((s) => s.loadAuditionState);

  const allPanels = usePluginPanels();
  const chainPanels = useMemo(
    () => sortChainPanels(
      allPanels.filter((p) => p.slot === CHAIN_SLOT),
      chainOrder,
    ),
    [allPanels, chainOrder],
  );

  // Fetch server-side state on first open. Subsequent updates arrive
  // via the AudioChainOrder WS broadcast handler in ws-client.ts.
  useEffect(() => {
    if (!isOpen) return;
    loadChainOrderFromServer();
    loadAuditionState();
  }, [isOpen, loadChainOrderFromServer, loadAuditionState]);

  // Escape closes the window — standard modal/popup keyboard
  // affordance. Listener only attached while the window is open
  // so it doesn't fight other Escape handlers (e.g. closing the
  // panadapter cursor crosshair) when the suite is hidden.
  useEffect(() => {
    if (!isOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [isOpen, close]);

  // Viewport-resize clamp — if the operator shrinks their browser
  // window after the suite is positioned, the suite's stored x/y
  // could end up off-screen and unreachable (no header to grab,
  // no resize handle to grab either). Re-apply the same clamp
  // rules used during drag whenever the viewport size changes.
  useEffect(() => {
    if (!isOpen) return;
    const onResize = () => {
      const minX = -width + 80;
      const minY = 64;
      const maxX = window.innerWidth - 80;
      const maxY = window.innerHeight - 40;
      const nextX = Math.min(maxX, Math.max(minX, x));
      const nextY = Math.min(maxY, Math.max(minY, y));
      if (nextX !== x || nextY !== y) setPosition(nextX, nextY);
    };
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [isOpen, x, y, width, setPosition]);

  // --- Window dragging via Pointer Events --------------------------
  const dragStateRef = useRef<{
    pointerId: number;
    startX: number;
    startY: number;
    offsetX: number;
    offsetY: number;
  } | null>(null);

  const onHeaderPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      // Ignore drags initiated on header controls (close button etc).
      const target = e.target as HTMLElement;
      if (target.closest('[data-no-drag]')) return;
      e.currentTarget.setPointerCapture(e.pointerId);
      dragStateRef.current = {
        pointerId: e.pointerId,
        startX: e.clientX,
        startY: e.clientY,
        offsetX: x,
        offsetY: y,
      };
      setDragging(true);
    },
    [x, y, setDragging],
  );

  const onHeaderPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const ds = dragStateRef.current;
      if (!ds || ds.pointerId !== e.pointerId) return;
      const dx = e.clientX - ds.startX;
      const dy = e.clientY - ds.startY;
      // Clamp so the header can't drag off-screen (always leave at
      // least 80px visible on every edge so the operator can grab it).
      // minY = 64 keeps the header from sliding under the 60-px Zeus
      // topbar — the operator must always have somewhere to grab to
      // pull the window back down. zIndex on the window root puts us
      // above the topbar visually anyway, but enforcing the clamp
      // avoids covering the radio chrome unnecessarily.
      const minX = -width + 80;
      const minY = 64;
      const maxX = window.innerWidth - 80;
      const maxY = window.innerHeight - 40;
      const nextX = Math.min(maxX, Math.max(minX, ds.offsetX + dx));
      const nextY = Math.min(maxY, Math.max(minY, ds.offsetY + dy));
      setPosition(nextX, nextY);
    },
    [width, setPosition],
  );

  const onHeaderPointerUp = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const ds = dragStateRef.current;
      if (!ds || ds.pointerId !== e.pointerId) return;
      try { e.currentTarget.releasePointerCapture(e.pointerId); } catch {}
      dragStateRef.current = null;
      setDragging(false);
    },
    [setDragging],
  );

  // --- Tile drag-and-drop (HTML5 d&d) -----------------------------
  // Two pieces of drag state:
  //   - draggedFromRef: synchronous source-index access in the drop
  //     handler (set in dragStart, cleared in drop/dragEnd).
  //   - draggedFromIdx (state): triggers re-render so the SOURCE
  //     tile dims to opacity 0.4 during drag, giving the operator
  //     a visible "I'm moving this one" cue alongside the target
  //     highlight.
  const [dragOverIndex, setDragOverIndex] = useState<number | null>(null);
  const [draggedFromIdx, setDraggedFromIdx] = useState<number | null>(null);
  const draggedFromRef = useRef<number | null>(null);

  const onTileDragStart = (idx: number) => (e: React.DragEvent) => {
    draggedFromRef.current = idx;
    setDraggedFromIdx(idx);
    e.dataTransfer.effectAllowed = 'move';
    // Some browsers require a payload to start a drag.
    e.dataTransfer.setData('text/plain', String(idx));
  };

  const onTileDragOver = (idx: number) => (e: React.DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (dragOverIndex !== idx) setDragOverIndex(idx);
  };

  const onTileDragLeave = () => setDragOverIndex(null);

  const onTileDrop = (idx: number) => (e: React.DragEvent) => {
    e.preventDefault();
    const from = draggedFromRef.current;
    setDragOverIndex(null);
    setDraggedFromIdx(null);
    draggedFromRef.current = null;
    if (from === null || from === idx) return;
    void reorderChain(from, idx);
  };

  const onTileDragEnd = () => {
    setDragOverIndex(null);
    setDraggedFromIdx(null);
    draggedFromRef.current = null;
  };

  if (!isOpen) return null;

  return (
    <div
      role="dialog"
      aria-label="Audio Suite"
      style={{
        position: 'fixed',
        left: x,
        // Render-time clamp on top so a persisted y < 64 (e.g. from a
        // session before the topbar-clearance fix) doesn't ship the
        // window under the topbar. The drag clamp prevents new drags
        // from going there; this self-heals stuck stored positions on
        // first render after upgrade.
        top: Math.max(64, y),
        width,
        height,
        // Above the Zeus topbar (zIndex 300) so the operator's window
        // is never hidden by app chrome. Below modal dialogs
        // (AddPanelModal etc at zIndex 10000) so critical overlays
        // still win.
        zIndex: 400,
        display: 'flex',
        flexDirection: 'column',
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line-1)',
        borderRadius: 8,
        boxShadow: '0 12px 32px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255, 255, 255, 0.04)',
        color: 'var(--fg-0)',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
        overflow: 'hidden',
      }}
    >
      {/* Resize handles — 4 edges + 4 corners. Each is a 6px invisible
          grab region absolutely positioned on its edge; cursor changes
          on hover so the grab area is discoverable. zIndex:1 so they
          sit above the content but below dialogs / dropdowns. */}
      {RESIZE_EDGES.map((e) => <ResizeHandle key={e} edge={e} />)}

      {/* Header — drag handle. Brass-plate styling per the v3 Lifted
          Dark spec ([[project_audio_chain_visual_direction]]). */}
      <div
        onPointerDown={onHeaderPointerDown}
        onPointerMove={onHeaderPointerMove}
        onPointerUp={onHeaderPointerUp}
        onPointerCancel={onHeaderPointerUp}
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 12,
          padding: '8px 12px',
          background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
          borderBottom: '1px solid var(--line-1)',
          boxShadow: 'inset 0 2px 0 var(--power), inset 0 3px 8px rgba(255, 201, 58, 0.08)',
          cursor: 'grab',
          userSelect: 'none',
        }}
      >
        <span
          style={{
            color: 'var(--fg-1)',
            fontSize: 12,
            fontWeight: 600,
            letterSpacing: 1.4,
            textTransform: 'uppercase',
          }}
        >
          Audio Suite
        </span>

        {/* Audition toggle. Disabled when host mode is server (audition
            sink is a no-op in browser mode v1 per Phase 1 ADR). */}
        <button
          type="button"
          data-no-drag
          disabled={!auditionSupported}
          onClick={() => setAuditionEnabled(!auditionEnabled)}
          title={
            auditionSupported
              ? auditionEnabled
                ? 'Audition is ON — chain output is mixed into your RX playback'
                : 'Audition is OFF — click to hear the chain on your headphones'
              : 'Audition is desktop-only in this version'
          }
          style={{
            marginLeft: 'auto',
            padding: '4px 12px',
            borderRadius: 4,
            border: '1px solid ' + (auditionEnabled ? 'var(--tx)' : 'var(--line-1)'),
            background: auditionEnabled ? 'var(--tx)' : 'var(--bg-2)',
            color: auditionEnabled ? 'var(--fg-0)' : 'var(--fg-2)',
            cursor: auditionSupported ? 'pointer' : 'not-allowed',
            opacity: auditionSupported ? 1 : 0.5,
            fontSize: 11,
            fontWeight: 600,
            letterSpacing: 1,
            textTransform: 'uppercase',
            fontFamily: 'inherit',
          }}
        >
          Audition {auditionEnabled ? 'ON' : 'OFF'}
        </button>

        <button
          type="button"
          data-no-drag
          onClick={close}
          aria-label="Close Audio Suite window"
          title="Close"
          style={{
            padding: '2px 10px',
            borderRadius: 4,
            border: '1px solid var(--line-1)',
            background: 'var(--bg-2)',
            color: 'var(--fg-2)',
            cursor: 'pointer',
            fontSize: 14,
            fontWeight: 600,
            fontFamily: 'inherit',
            lineHeight: 1,
          }}
        >
          ×
        </button>
      </div>

      {/* Tile strip — drag to reorder. Vanilla HTML5 d&d. */}
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          padding: '8px 12px',
          background: 'var(--bg-1)',
          borderBottom: '1px solid var(--line-1)',
          flexWrap: 'wrap',
          fontSize: 10,
          letterSpacing: 1.2,
          textTransform: 'uppercase',
        }}
      >
        <span style={{ color: 'var(--fg-3)', marginRight: 4, fontWeight: 500 }}>
          Chain
        </span>
        {chainPanels.length === 0 && (
          <span style={{ color: 'var(--fg-3)', fontStyle: 'italic', textTransform: 'none' }}>
            No audio plugins installed — use Download Audio Suite on the TX Audio Tools panel.
          </span>
        )}
        {chainPanels.map((panel, idx) => {
          const label = shortLabelFor(panel.pluginId, panel.title);
          const isDragTarget = dragOverIndex === idx;
          const isDragSource = draggedFromIdx === idx;
          return (
            <div key={panel.pluginId} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              {idx > 0 && (
                <span aria-hidden style={{ color: 'var(--fg-3)' }}>›</span>
              )}
              <div
                draggable
                onDragStart={onTileDragStart(idx)}
                onDragOver={onTileDragOver(idx)}
                onDragLeave={onTileDragLeave}
                onDrop={onTileDrop(idx)}
                onDragEnd={onTileDragEnd}
                title={`${panel.pluginId} — drag to reorder`}
                style={{
                  padding: '4px 10px',
                  borderRadius: 3,
                  background: isDragTarget ? 'var(--accent)' : 'var(--bg-2)',
                  border: '1px dashed ' + (isDragTarget ? 'var(--accent)' : 'var(--line-1)'),
                  borderStyle: isDragSource ? 'dashed' : 'solid',
                  color: isDragTarget ? 'var(--fg-0)' : 'var(--fg-1)',
                  cursor: 'grab',
                  opacity: isDragSource ? 0.4 : 1,
                  fontSize: 10,
                  fontWeight: 500,
                  userSelect: 'none',
                }}
              >
                {label}
              </div>
            </div>
          );
        })}
      </div>

      {/* Plugin panels stacked vertically in chain order. */}
      <div
        style={{
          flex: 1,
          overflowY: 'auto',
          padding: 12,
          display: 'flex',
          flexDirection: 'column',
          gap: 12,
        }}
      >
        {chainPanels.map((panel) => {
          const Component = panel.component;
          return (
            <div
              key={`${panel.pluginId}::${panel.panelId}`}
              data-plugin-id={panel.pluginId}
            >
              <Component />
            </div>
          );
        })}
      </div>
    </div>
  );
}
