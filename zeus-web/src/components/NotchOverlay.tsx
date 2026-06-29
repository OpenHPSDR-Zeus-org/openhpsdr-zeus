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

import { useRef, type PointerEvent as ReactPointerEvent, type RefObject } from 'react';
import { useDisplayStore } from '../state/display-store';
import { useNotchStore } from '../state/notch-store';

// Renders manual-notch bands over a spectrum surface (panadapter or waterfall).
// Each band marks where WDSP is notching the RX audio; ✕ deletes it, and (when
// `resizable`) the operator can grab either edge and drag to resize the notch —
// the new width syncs straight back to WDSP. The band fill is pointer-transparent
// so tuning passes through; only the ✕ and the edge handles are interactive.
// New notches are painted by the pan-tune gesture while NOTCH is armed.
// Positioned by percent-of-span like SpotOverlay, so no DOM measurement is
// needed on resize.

type NotchOverlayProps = {
  /** Show the ✕ delete control on each band. */
  interactive?: boolean;
  /** Enable grab-to-resize edge handles. Needs `containerRef` for px→Hz. */
  resizable?: boolean;
  /** The spectrum surface container, for mapping a pointer X to a frequency. */
  containerRef?: RefObject<HTMLDivElement | null>;
};

type ResizeDrag = { id: string; fixedEdgeHz: number };

export function NotchOverlay({ interactive = false, resizable = false, containerRef }: NotchOverlayProps = {}) {
  const centerHz = useDisplayStore((s) => s.centerHz);
  const hzPerPixel = useDisplayStore((s) => s.hzPerPixel);
  const width = useDisplayStore((s) => s.panDb?.length ?? 0);
  const notches = useNotchStore((s) => s.notches);
  const pending = useNotchStore((s) => s.pending);
  const armed = useNotchStore((s) => s.armed);
  const removeNotch = useNotchStore((s) => s.removeNotch);
  const updateNotch = useNotchStore((s) => s.updateNotch);
  const drag = useRef<ResizeDrag | null>(null);

  if (!width || hzPerPixel <= 0) return null;

  const spanHz = width * hzPerPixel;
  const startHz = Number(centerHz) - spanHz / 2;
  const bandStyle = (cHz: number, wHz: number) => {
    const left = ((cHz - wHz / 2 - startHz) / spanHz) * 100;
    const w = (wHz / spanHz) * 100;
    return { left: `${left}%`, width: `${w}%` };
  };

  // Map a client X to an absolute frequency against the container's live width.
  const clientXToHz = (clientX: number): number | null => {
    const el = containerRef?.current;
    if (!el) return null;
    const rect = el.getBoundingClientRect();
    if (rect.width <= 0) return null;
    const s = useDisplayStore.getState();
    if (!s.panDb || s.hzPerPixel <= 0) return null;
    const span = s.panDb.length * s.hzPerPixel;
    const frac = (clientX - rect.left) / rect.width;
    return Number(s.centerHz) - span / 2 + frac * span;
  };

  // Grab an edge: the OPPOSITE edge stays pinned; pointer moves set the new one.
  const onEdgeDown = (n: { id: string; centerHz: number; widthHz: number }, side: 'lo' | 'hi') =>
    (e: ReactPointerEvent) => {
      e.stopPropagation();
      e.preventDefault();
      const fixedEdgeHz = side === 'lo' ? n.centerHz + n.widthHz / 2 : n.centerHz - n.widthHz / 2;
      drag.current = { id: n.id, fixedEdgeHz };
      try { (e.target as Element).setPointerCapture(e.pointerId); } catch { /* ok */ }
    };
  const onEdgeMove = (e: ReactPointerEvent) => {
    const d = drag.current;
    if (!d) return;
    const edgeHz = clientXToHz(e.clientX);
    if (edgeHz === null) return;
    updateNotch(d.id, (d.fixedEdgeHz + edgeHz) / 2, Math.abs(edgeHz - d.fixedEdgeHz));
  };
  const onEdgeUp = (e: ReactPointerEvent) => {
    if (!drag.current) return;
    drag.current = null;
    try { (e.target as Element).releasePointerCapture(e.pointerId); } catch { /* ok */ }
  };

  const editEnabled = armed;
  const showHandles = editEnabled && resizable && !!containerRef;
  const showDelete = editEnabled && interactive;

  return (
    <>
      {notches.map((n) => {
        const left = ((n.centerHz - n.widthHz / 2 - startHz) / spanHz) * 100;
        const w = (n.widthHz / spanHz) * 100;
        if (left + w < -1 || left > 101) return null;
        return (
          <div
            key={n.id}
            className="pointer-events-none absolute inset-y-0 z-[6]"
            style={{
              left: `${left}%`,
              width: `${w}%`,
              // Indicator band: a soft scrim + TX-red edges marking where WDSP
              // is notching. The audio (and post-notch trace) are already cut by
              // the real filter; this just shows the operator where.
              background: 'rgba(0,0,0,0.5)',
              borderLeft: '1px solid rgba(230,58,43,0.8)',
              borderRight: '1px solid rgba(230,58,43,0.8)',
            }}
          >
            {showHandles && (
              <>
                <div
                  onPointerDown={onEdgeDown(n, 'lo')}
                  onPointerMove={onEdgeMove}
                  onPointerUp={onEdgeUp}
                  onPointerCancel={onEdgeUp}
                  title="Drag to resize notch"
                  className="pointer-events-auto absolute inset-y-0 left-0 -translate-x-1/2 cursor-ew-resize"
                  style={{ width: 9 }}
                />
                <div
                  onPointerDown={onEdgeDown(n, 'hi')}
                  onPointerMove={onEdgeMove}
                  onPointerUp={onEdgeUp}
                  onPointerCancel={onEdgeUp}
                  title="Drag to resize notch"
                  className="pointer-events-auto absolute inset-y-0 right-0 translate-x-1/2 cursor-ew-resize"
                  style={{ width: 9 }}
                />
              </>
            )}
            {showDelete && (
              <button
                type="button"
                title={`Remove notch @ ${(n.centerHz / 1e6).toFixed(4)} MHz`}
                aria-label={`Remove notch @ ${(n.centerHz / 1e6).toFixed(4)} MHz`}
                onPointerDown={(e) => e.stopPropagation()}
                onClick={(e) => {
                  e.stopPropagation();
                  removeNotch(n.id);
                }}
                className="group pointer-events-auto absolute left-1/2 top-[22px] z-[12] grid h-[18px] w-[18px] -translate-x-1/2 place-items-center rounded-full border border-[#e63a2b]/60 bg-neutral-950/70 text-[#e63a2b] shadow-[0_1px_5px_rgba(0,0,0,0.55)] backdrop-blur-sm transition-all duration-150 hover:scale-110 hover:border-[#e63a2b] hover:bg-[#e63a2b] hover:text-white hover:shadow-[0_0_8px_rgba(230,58,43,0.6)]"
              >
                <svg viewBox="0 0 12 12" className="h-[9px] w-[9px]" aria-hidden="true">
                  <path
                    d="M2.5 2.5 L9.5 9.5 M9.5 2.5 L2.5 9.5"
                    stroke="currentColor"
                    strokeWidth="1.6"
                    strokeLinecap="round"
                  />
                </svg>
              </button>
            )}
          </div>
        );
      })}
      {pending && pending.widthHz > 0 && (
        <div
          className="pointer-events-none absolute inset-y-0 z-[6]"
          style={{
            ...bandStyle(pending.centerHz, pending.widthHz),
            background: 'rgba(230,58,43,0.18)',
            border: '1px dashed rgba(230,58,43,0.9)',
          }}
        />
      )}
    </>
  );
}
