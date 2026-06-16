// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef, type PointerEvent as ReactPointerEvent, type RefObject } from 'react';
import { selectDisplaySlice, useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';
import { setFilter } from '../api/client';
import { cancelDrawBusFrame, requestDrawBusFrame } from '../realtime/draw-bus';
import * as viewCenter from '../state/view-center';

// Edge-resize tuning constants — shared with the advanced filter mini-pan.
const DRAG_MIN_INTERVAL_MS = 50; // throttle for live setFilter writes during a drag
const MIN_PASSBAND_HZ = 50; // keep low/high from crossing

// Fixed presets (F1..F10) are read-only widths; resizing one diverts to the
// VAR1 variable slot, exactly like FilterMiniPan, so the operator's drag isn't
// silently discarded.
function presetIsFixed(name: string | null): boolean {
  return !!name && /^F([1-9]|10)$/.test(name);
}
function variableSlot(name: string | null): string {
  return presetIsFixed(name) || !name ? 'VAR1' : name;
}

// Translucent rectangle drawn inside the panadapter container to show the
// active receive filter passband, mapped from [filterLowHz, filterHighHz]
// relative to the VFO centre. Asymmetric by design: USB lives to the right
// of carrier, LSB to the left, CW narrow around zero, AM symmetric.
// Positioned by percentage of the total span so it tracks resize and tune
// without measuring DOM width.
type PassbandOverlayProps = {
  /** Enable grab-to-resize edge handles that sync the RX filter low/high cut. */
  resizable?: boolean;
  /** The spectrum surface container, for mapping a pointer X to a frequency. */
  containerRef?: RefObject<HTMLElement | null>;
  receiver?: 'A' | 'B';
};

type EdgeDrag = {
  side: 'lo' | 'hi';
  slot: string;
  pendingLo: number;
  pendingHi: number;
  lastWriteAt: number;
  flushTimer: number | null;
  pointerId: number;
};

export function PassbandOverlay({
  resizable = false,
  containerRef,
  receiver = 'A',
}: PassbandOverlayProps = {}) {
  const centerHz = useDisplayStore((s) => selectDisplaySlice(s, receiver).centerHz);
  const hzPerPixel = useDisplayStore((s) => selectDisplaySlice(s, receiver).hzPerPixel);
  // Header width — survives frames whose pan payload is invalid.
  const width = useDisplayStore((s) => selectDisplaySlice(s, receiver).width);
  const filterLowHz = useConnectionStore((s) => s.filterLowHz);
  const filterHighHz = useConnectionStore((s) => s.filterHighHz);
  const selectedVfoHz = useConnectionStore((s) =>
    receiver === 'B' ? s.vfoBHz : s.vfoHz,
  );

  const rectRef = useRef<HTMLDivElement | null>(null);
  const drag = useRef<EdgeDrag | null>(null);

  // Map a client X to a filter offset (Hz relative to the dial/VFO) against the
  // container's live width — the same settled geometry FilterCursorOverlay and
  // NotchOverlay use for their pointer math. Filter edges are stored as offsets
  // from the dial, so we subtract vfoHz from the absolute frequency under the
  // pointer.
  const clientXToOffsetHz = (clientX: number): number | null => {
    const el = containerRef?.current;
    if (!el) return null;
    const rect = el.getBoundingClientRect();
    if (rect.width <= 0) return null;
    const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
    const len = s.panDb?.length ?? s.width;
    if (!len || s.hzPerPixel <= 0) return null;
    const span = len * s.hzPerPixel;
    const frac = (clientX - rect.left) / rect.width;
    const c = useConnectionStore.getState();
    const visualCenter = Number(s.centerHz);
    const absHz = visualCenter - span / 2 + frac * span;
    return absHz - Number(receiver === 'B' ? c.vfoBHz : c.vfoHz);
  };

  // Throttled live write while dragging (50 ms), so a drag resizes the real
  // WDSP filter in near-real-time without flooding the backend.
  const flushPending = () => {
    const d = drag.current;
    if (!d) return;
    d.flushTimer = null;
    d.lastWriteAt = performance.now();
    setFilter(d.pendingLo, d.pendingHi, d.slot).catch(() => {});
  };
  const scheduleWrite = () => {
    const d = drag.current;
    if (!d) return;
    const elapsed = performance.now() - d.lastWriteAt;
    if (elapsed >= DRAG_MIN_INTERVAL_MS) flushPending();
    else if (d.flushTimer == null)
      d.flushTimer = window.setTimeout(flushPending, DRAG_MIN_INTERVAL_MS - elapsed);
  };

  const onEdgeDown = (side: 'lo' | 'hi') => (e: ReactPointerEvent) => {
    if (e.button !== 0) return;
    e.stopPropagation();
    e.preventDefault();
    const c = useConnectionStore.getState();
    const slot = variableSlot(c.filterPresetName);
    drag.current = {
      side,
      slot,
      pendingLo: c.filterLowHz,
      pendingHi: c.filterHighHz,
      lastWriteAt: 0,
      flushTimer: null,
      pointerId: e.pointerId,
    };
    if (slot !== c.filterPresetName) useConnectionStore.setState({ filterPresetName: slot });
    try { (e.target as Element).setPointerCapture(e.pointerId); } catch { /* ok */ }
  };
  const onEdgeMove = (e: ReactPointerEvent) => {
    const d = drag.current;
    if (!d || e.pointerId !== d.pointerId) return;
    const offset = clientXToOffsetHz(e.clientX);
    if (offset === null) return;
    let lo = d.pendingLo;
    let hi = d.pendingHi;
    if (d.side === 'lo') lo = Math.min(d.pendingHi - MIN_PASSBAND_HZ, Math.round(offset));
    else hi = Math.max(d.pendingLo + MIN_PASSBAND_HZ, Math.round(offset));
    d.pendingLo = lo;
    d.pendingHi = hi;
    useConnectionStore.setState({ filterLowHz: lo, filterHighHz: hi });
    scheduleWrite();
  };
  const onEdgeUp = (e: ReactPointerEvent) => {
    const d = drag.current;
    if (!d || e.pointerId !== d.pointerId) return;
    if (d.flushTimer != null) { clearTimeout(d.flushTimer); d.flushTimer = null; }
    const { pendingLo, pendingHi, slot } = d;
    drag.current = null;
    try { (e.target as Element).releasePointerCapture(e.pointerId); } catch { /* ok */ }
    const applyState = useConnectionStore.getState().applyState;
    setFilter(pendingLo, pendingHi, slot).then(applyState).catch(() => {});
  };

  const showHandles = resizable && !!containerRef;

  // Smooth motion (issue #597): the rect is positioned against the animated
  // view-center by a draw-bus callback — same clock as the trace, waterfall,
  // dial marker, and tick strip, zero React commits at display rate. The
  // passband rides the radio's center (filter edges are center-relative),
  // so during a glide it eases with the spectrum instead of teleporting at
  // 30 Hz frame arrival.
  useEffect(() => {
    const update = () => {
      const rect = rectRef.current;
      if (!rect) return;
      const s = selectDisplaySlice(useDisplayStore.getState(), receiver);
      if (!s.width || s.hzPerPixel <= 0) return;
      const spanHz = s.width * s.hzPerPixel;
      const conn = useConnectionStore.getState();
      const view = receiver === 'B'
        ? Number(s.centerHz)
        : viewCenter.isInitialized()
        ? viewCenter.getViewCenterHz()
        : Number(s.centerHz);
      // The passband hangs off the VIEW center — which is, by definition,
      // always rendered at the screen center (the orange zero line). So
      // during a tuning glide the filter stays PINNED to the line while the
      // spectrum slides underneath it; anchoring to the commanded target
      // instead made it lead off the line and ease back (operator feedback,
      // 2026-06-12).
      // Hang the passband off the dial, expressed as the dial's settled offset
      // from the display center — the same (vfo − targetCenter) the FreqAxis
      // marker uses. Outside CTUN the dial sits on the view center so this is
      // ~0 and the filter stays pinned to the zero line during a glide; under
      // CTUN the dial roams off-centre and the passband tracks it.
      const dialOffsetHz = receiver === 'B'
        ? conn.vfoBHz - view
        : viewCenter.isInitialized()
        ? conn.vfoHz - viewCenter.getTargetCenterHz()
        : 0;
      const passCenter = view + dialOffsetHz;
      const startHz = view - spanHz / 2;
      const leftPct = ((passCenter + conn.filterLowHz - startHz) / spanHz) * 100;
      const rightPct = ((passCenter + conn.filterHighHz - startHz) / spanHz) * 100;
      const widthPct = rightPct - leftPct;
      const visible = widthPct > 0 && leftPct <= 100 && rightPct >= 0;
      rect.style.display = visible ? '' : 'none';
      if (visible) {
        rect.style.left = `${leftPct}%`;
        rect.style.width = `${widthPct}%`;
      }
    };
    const schedule = () => requestDrawBusFrame(update);
    const unsubVc = viewCenter.subscribe(schedule);
    const unsubConn = useConnectionStore.subscribe((s, prev) => {
      if (
        s.filterLowHz !== prev.filterLowHz ||
        s.filterHighHz !== prev.filterHighHz ||
        s.vfoHz !== prev.vfoHz ||
        s.vfoBHz !== prev.vfoBHz
      ) {
        schedule();
      }
    });
    const unsubFrame = useDisplayStore.subscribe((s, prev) => {
      if (selectDisplaySlice(s, receiver).lastSeq !== selectDisplaySlice(prev, receiver).lastSeq) schedule();
    });
    schedule();
    return () => {
      unsubVc();
      unsubConn();
      unsubFrame();
      cancelDrawBusFrame(update);
    };
  }, [receiver]);

  if (!width || hzPerPixel <= 0) return null;

  const spanHz = width * hzPerPixel;
  const center = Number(centerHz);
  const startHz = center - spanHz / 2;

  // Initial (pre-draw-bus) geometry; the callback refines it next frame.
  const passLowHz = selectedVfoHz + filterLowHz;
  const passHighHz = selectedVfoHz + filterHighHz;
  const leftPct = ((passLowHz - startHz) / spanHz) * 100;
  const rightPct = ((passHighHz - startHz) / spanHz) * 100;
  const widthPct = rightPct - leftPct;

  if (widthPct <= 0) return null;

  return (
    <div
      ref={rectRef}
      aria-hidden
      className="pointer-events-none absolute inset-y-0 z-[5]"
      style={{
        left: `${leftPct}%`,
        width: `${widthPct}%`,
        background: 'rgba(255, 160, 40, 0.18)',
        borderLeft: '1px solid rgba(255, 160, 40, 0.6)',
        borderRight: '1px solid rgba(255, 160, 40, 0.6)',
      }}
    >
      {showHandles && (
        <>
          <div
            onPointerDown={onEdgeDown('lo')}
            onPointerMove={onEdgeMove}
            onPointerUp={onEdgeUp}
            onPointerCancel={onEdgeUp}
            title="Drag to set low-cut / passband width"
            className="pointer-events-auto absolute inset-y-0 left-0 -translate-x-1/2 cursor-ew-resize"
            style={{ width: 9 }}
          />
          <div
            onPointerDown={onEdgeDown('hi')}
            onPointerMove={onEdgeMove}
            onPointerUp={onEdgeUp}
            onPointerCancel={onEdgeUp}
            title="Drag to set high-cut / passband width"
            className="pointer-events-auto absolute inset-y-0 right-0 translate-x-1/2 cursor-ew-resize"
            style={{ width: 9 }}
          />
        </>
      )}
    </div>
  );
}
