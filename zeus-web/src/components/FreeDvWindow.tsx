// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDvWindow — draggable, resizable floating popup that hosts the FreeDV
// modem controls (the FreeDvPanel body). Selecting FreeDV mode pops this open
// (see App.tsx) instead of injecting a workspace tile, so the operator gets the
// submode / SYNC / SNR / TX-text sidechannel as an overlay they can drag aside
// rather than something that rearranges their console.
//
// Drag + resize use Pointer Events with capture, mirroring AudioSuiteWindow.
// All chrome is in Zeus tokens — no raw hex per docs/lessons/dev-conventions.md.

import { useCallback, useEffect, useRef } from 'react';
import { FreeDvPanel } from '../layout/panels/FreeDvPanel';
import {
  FREEDV_WINDOW_MIN_WIDTH,
  FREEDV_WINDOW_MIN_HEIGHT,
  useFreeDvWindowStore,
} from '../state/freedv-window-store';

/** Edge codes for the resize handles — 4 edges + 4 corners. */
type ResizeEdge = 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw';
const RESIZE_EDGES: ResizeEdge[] = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'];
const RESIZE_HANDLE_PX = 6;

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

function ResizeHandle({ edge }: { edge: ResizeEdge }) {
  const x = useFreeDvWindowStore((s) => s.x);
  const y = useFreeDvWindowStore((s) => s.y);
  const width = useFreeDvWindowStore((s) => s.width);
  const height = useFreeDvWindowStore((s) => s.height);
  const setPosition = useFreeDvWindowStore((s) => s.setPosition);
  const setSize = useFreeDvWindowStore((s) => s.setSize);

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

      if (nW < FREEDV_WINDOW_MIN_WIDTH) {
        if (edge.includes('w')) nX = d.origX + d.origW - FREEDV_WINDOW_MIN_WIDTH;
        nW = FREEDV_WINDOW_MIN_WIDTH;
      }
      if (nH < FREEDV_WINDOW_MIN_HEIGHT) {
        if (edge.includes('n')) nY = d.origY + d.origH - FREEDV_WINDOW_MIN_HEIGHT;
        nH = FREEDV_WINDOW_MIN_HEIGHT;
      }

      setPosition(nX, nY);
      setSize(nW, nH);
    },
    [edge, setPosition, setSize],
  );

  const onPointerUp = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragRef.current;
      if (!d || d.pointerId !== e.pointerId) return;
      try { e.currentTarget.releasePointerCapture(e.pointerId); } catch { /* best-effort */ }
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

export function FreeDvWindow() {
  const isOpen = useFreeDvWindowStore((s) => s.isOpen);
  const close = useFreeDvWindowStore((s) => s.close);
  const x = useFreeDvWindowStore((s) => s.x);
  const y = useFreeDvWindowStore((s) => s.y);
  const width = useFreeDvWindowStore((s) => s.width);
  const height = useFreeDvWindowStore((s) => s.height);
  const setPosition = useFreeDvWindowStore((s) => s.setPosition);

  // Escape closes the popup — standard modal/popup keyboard affordance.
  // Attached only while open so it doesn't fight other Escape handlers.
  useEffect(() => {
    if (!isOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') close();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [isOpen, close]);

  // Viewport-resize clamp — keep the window grabbable if the browser shrinks.
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
    },
    [x, y],
  );

  const onHeaderPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const ds = dragStateRef.current;
      if (!ds || ds.pointerId !== e.pointerId) return;
      const dx = e.clientX - ds.startX;
      const dy = e.clientY - ds.startY;
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
      try { e.currentTarget.releasePointerCapture(e.pointerId); } catch { /* best-effort */ }
      dragStateRef.current = null;
    },
    [],
  );

  if (!isOpen) return null;

  return (
    <div
      role="dialog"
      aria-label="FreeDV"
      style={{
        position: 'fixed',
        left: x,
        // Render-time clamp so a stale persisted y can't ship under the topbar.
        top: Math.max(64, y),
        width,
        height,
        // Above the Zeus topbar (zIndex 300), below modal dialogs (10000).
        zIndex: 420,
        display: 'flex',
        flexDirection: 'column',
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line)',
        borderRadius: 8,
        boxShadow: '0 12px 32px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255, 255, 255, 0.04)',
        color: 'var(--fg-0)',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
        overflow: 'hidden',
      }}
    >
      {RESIZE_EDGES.map((e) => <ResizeHandle key={e} edge={e} />)}

      {/* Header — drag handle + close. */}
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
          borderBottom: '1px solid var(--line)',
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
          FreeDV
        </span>
        <button
          type="button"
          data-no-drag
          onClick={close}
          aria-label="Close FreeDV window"
          title="Close"
          style={{
            marginLeft: 'auto',
            padding: '2px 10px',
            borderRadius: 4,
            border: '1px solid var(--line)',
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

      {/* Body — the FreeDV modem controls. FreeDvPanel already manages its own
          scrolling (overflowY:auto on the .dsp-cfg root); the flex:1 min-height:0
          wrapper gives it a bounded height to scroll within. */}
      <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        <FreeDvPanel />
      </div>
    </div>
  );
}
