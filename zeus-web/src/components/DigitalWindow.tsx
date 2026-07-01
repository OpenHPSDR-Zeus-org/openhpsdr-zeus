// SPDX-License-Identifier: GPL-2.0-or-later
//
// DigitalWindow — the floating, draggable, always-on-top FT8/FT4/WSPR pop-out,
// modelled on FreeDvWindow but NOT resizable (fixed size, ~2× the FreeDV popup)
// and with NO open flag of its own: visibility is owned entirely by
// ft8-store.open / wspr-store.open (engaging the mode opens it, un-toggling
// closes it). Closing the window exits the digital mode (restores the prior
// freq+mode). The operator keeps their full Zeus console underneath — this
// pop-out carries ONLY the operating essentials (decode list + TX controls).
//
// Drag uses Pointer Events with capture, mirroring FreeDvWindow / AudioSuiteWindow.
// All chrome is in Zeus / --hud-* tokens — no raw hex.

import { useCallback, useEffect, useRef } from 'react';
import { useFt8Store } from '../state/ft8-store';
import { useWsprStore } from '../state/wspr-store';
import { exitDigital } from '../state/enter-digital';
import {
  DIGITAL_WINDOW_HEIGHT,
  DIGITAL_WINDOW_WIDTH,
  useDigitalWindowStore,
} from '../state/digital-window-store';
import { Ft8PopBody } from '../layout/ft8/Ft8PopBody';
import { WsprPopBody } from '../layout/ft8/WsprPopBody';
import { useLayoutStore } from '../state/layout-store';
import '../styles/ft8-theme.css';

export function DigitalWindow() {
  const ft8Open = useFt8Store((s) => s.open);
  const protocol = useFt8Store((s) => s.protocol);
  const wsprOpen = useWsprStore((s) => s.open);
  const x = useDigitalWindowStore((s) => s.x);
  const y = useDigitalWindowStore((s) => s.y);
  const setPosition = useDigitalWindowStore((s) => s.setPosition);
  // Settings replaces the workspace inline (not a modal), with no z-index
  // of its own — so this fixed-position, zIndex:420 popup would paint over
  // it. Hide while Settings is showing without exiting the digital mode,
  // so the popup reappears in place when Settings closes.
  const settingsViewOpen = useLayoutStore((s) => s.settingsViewOpen);

  const open = ft8Open || wsprOpen;

  // Escape closes the pop-out (= exit the digital mode). Attached only while
  // open so it doesn't fight other Escape handlers.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      // Don't tear down the session on an Escape meant to clear/cancel a field
      // (Call/Grid, settings inputs, the free-text macro).
      const t = e.target as HTMLElement | null;
      if (t?.closest('input, textarea, select, [contenteditable="true"]')) return;
      exitDigital();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open]);

  // Viewport-resize clamp — keep the window grabbable if the browser shrinks.
  useEffect(() => {
    if (!open) return;
    const onResize = () => {
      const minX = -DIGITAL_WINDOW_WIDTH + 80;
      const minY = 64;
      const maxX = window.innerWidth - 80;
      const maxY = window.innerHeight - 40;
      const nextX = Math.min(maxX, Math.max(minX, x));
      const nextY = Math.min(maxY, Math.max(minY, y));
      if (nextX !== x || nextY !== y) setPosition(nextX, nextY);
    };
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, [open, x, y, setPosition]);

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
      const minX = -DIGITAL_WINDOW_WIDTH + 80;
      const minY = 64;
      const maxX = window.innerWidth - 80;
      const maxY = window.innerHeight - 40;
      const nextX = Math.min(maxX, Math.max(minX, ds.offsetX + dx));
      const nextY = Math.min(maxY, Math.max(minY, ds.offsetY + dy));
      setPosition(nextX, nextY);
    },
    [setPosition],
  );

  const onHeaderPointerUp = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    const ds = dragStateRef.current;
    if (!ds || ds.pointerId !== e.pointerId) return;
    try {
      e.currentTarget.releasePointerCapture(e.pointerId);
    } catch {
      /* best-effort */
    }
    dragStateRef.current = null;
  }, []);

  if (!open || settingsViewOpen) return null;

  // Fixed size; height clamped to the viewport so the frame stays fully on-screen
  // and grabbable on a laptop (the body scrolls within). Not user-resizable.
  const height =
    typeof window !== 'undefined'
      ? Math.min(DIGITAL_WINDOW_HEIGHT, window.innerHeight - 96)
      : DIGITAL_WINDOW_HEIGHT;
  const title = wsprOpen ? 'WSPR' : `${protocol} DIGITAL`;

  return (
    <div
      className="digital-window"
      role="dialog"
      aria-label="FT8 / FT4 / WSPR"
      style={{
        position: 'fixed',
        left: x,
        // Render-time clamp so a stale persisted y can't ship under the topbar.
        top: Math.max(64, y),
        width: DIGITAL_WINDOW_WIDTH,
        height,
        // Above the Zeus topbar (zIndex 300), below modal dialogs (10000) — the
        // "always on top" mechanism, same as FreeDvWindow (no OS API).
        zIndex: 420,
        display: 'flex',
        flexDirection: 'column',
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line)',
        borderRadius: 8,
        boxShadow: '0 12px 32px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255, 255, 255, 0.04)',
        color: 'var(--hud-text)',
        fontFamily: "var(--font-narrow, 'Archivo Narrow', sans-serif)",
        overflow: 'hidden',
      }}
    >
      {/* Header — drag handle + close. */}
      <div
        onPointerDown={onHeaderPointerDown}
        onPointerMove={onHeaderPointerMove}
        onPointerUp={onHeaderPointerUp}
        onPointerCancel={onHeaderPointerUp}
        className="dw-header"
      >
        <span className="dw-header__title">{title}</span>
        <button
          type="button"
          data-no-drag
          onClick={() => exitDigital()}
          aria-label="Close digital window"
          title="Exit digital mode · Esc"
          className="dw-header__close"
        >
          ×
        </button>
      </div>

      {/* Body — switches by which store is open. */}
      <div className="dw-content">{wsprOpen ? <WsprPopBody /> : <Ft8PopBody />}</div>
    </div>
  );
}
