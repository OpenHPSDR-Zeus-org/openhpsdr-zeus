// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8WaterfallOverlay — a transparent capture layer laid over the FT8 receive
// panadapter/waterfall. It does three things, all reading the LIVE display-store
// slice geometry + the dial so they line up with the rendered spectrum:
//
//   1. Click-to-offset. The built-in canvas gesture (usePanTuneGesture) moves
//      the RF DIAL — wrong for FT8, which keeps the dial fixed and moves the
//      AUDIO OFFSET. Because pointer events go to the topmost element, this
//      overlay cleanly suppresses that gesture WITHOUT editing the shared
//      component. A click always moves the RX focus cursor; it also moves the TX
//      audio offset UNLESS HOLD TX FREQ is engaged (then only RX moves).
//   2. RX cursor (cyan) + TX marker (red) verticals.
//   3. Decode-marker ticks from useFt8Store, coloured to match the decode-table
//      row classes (CQ / directed-at-me / worked / new).
//
// Pure render from store state; the heavy spectrum render is the existing
// panadapter/waterfall underneath. No new subscriptions beyond the stores the
// table + VFO already use.

import { useEffect, useMemo, useRef, useSyncExternalStore } from 'react';
import { setZoom, ZOOM_MAX, ZOOM_MIN, type ZoomLevel } from '../../api/client';
import { selectDisplaySlice, useDisplayStore } from '../../state/display-store';
import { useConnectionStore } from '../../state/connection-store';
import { viewCenterFor } from '../../state/view-center';
import { useFt8Store } from '../../state/ft8-store';
import type { Ft8TxRunnerView } from '../../dsp/ft8-tx-runner';
import { classifyDecode, type Ft8RowClass } from './Ft8DecodeTable';
import {
  clampOffsetHz,
  offsetHzForClientX,
  pixelForOffsetHz,
  resolveWaterfallClick,
  snapOffsetToDecode,
  spanHzOf,
  type PassbandView,
} from '../../dsp/ft8-passband';

export interface Ft8WaterfallOverlayProps {
  runner: Ft8TxRunnerView;
  rxFocusHz: number;
  setRxFocusHz: (hz: number) => void;
  /** Operator call — enables the "directed at me" tick colour. */
  myCall?: string;
}

const TICK_VAR: Record<Ft8RowClass, string> = {
  cq: 'var(--hud-cq)',
  me: 'var(--hud-me)',
  worked: 'var(--hud-worked)',
  new: 'var(--hud-new)',
  normal: 'var(--hud-text-dim)',
};

export function Ft8WaterfallOverlay({
  runner,
  rxFocusHz,
  setRxFocusHz,
  myCall,
}: Ft8WaterfallOverlayProps) {
  // Live slice geometry (receiver A) as primitives so the selectors stay
  // referentially stable — selectDisplaySlice allocates a fresh object.
  const sliceCenterHz = useDisplayStore((s) => Number(selectDisplaySlice(s, 'A').centerHz));
  const hzPerPixel = useDisplayStore((s) => selectDisplaySlice(s, 'A').hzPerPixel);
  const width = useDisplayStore((s) => selectDisplaySlice(s, 'A').width);

  // CRITICAL: the panadapter and waterfall render the spectrum against the
  // ANIMATED view-center tween (view-center.ts, issue #597), not the raw frame
  // centerHz. Reading the raw center here would make the markers snap while the
  // trace glides on every reframe (entry / band QSY / WF OFFSET). Subscribe to
  // the same tween so the markers ease in lock-step with the spectrum; fall back
  // to the slice center until the tween is initialised. Object.is(NaN, NaN) is
  // true, so the parked-and-uninitialised snapshot is stable (no render loop).
  const vc = useMemo(() => viewCenterFor('A'), []);
  const tweenCenterHz = useSyncExternalStore(vc.subscribe, () =>
    vc.isInitialized() ? vc.getViewCenterHz() : NaN,
  );
  const centerHz = Number.isFinite(tweenCenterHz) ? tweenCenterHz : sliceCenterHz;
  const dialHz = useConnectionStore((s) => s.vfoHz);
  const rows = useFt8Store((s) => s.rows);

  const view: PassbandView = { centerHz, hzPerPixel, width };
  const haveGeometry = spanHzOf(view) > 0;

  const holdTxFreq = runner.qso.holdTxFreq;

  // Decodes worth marking: the two most-recent slots present. FT8 churns the
  // band every slot, so older ticks would clutter without adding information.
  const recent = useMemo(() => {
    if (rows.length === 0) return [];
    const slots = Array.from(new Set(rows.map((r) => r.slotStartUnixMs)))
      .sort((a, b) => b - a)
      .slice(0, 2);
    const keep = new Set(slots);
    return rows.filter((r) => keep.has(r.slotStartUnixMs));
  }, [rows]);

  const recentOffsets = useMemo(() => recent.map((r) => clampOffsetHz(r.freqHz)), [recent]);

  // Wheel-to-zoom parity. The overlay is the topmost element over the canvases,
  // so the panadapter's own wheel-to-zoom never fires and the page would scroll.
  // Take over: step the zoom the same lever ZoomControl uses (scroll up = zoom
  // in). A native non-passive listener is required because React's delegated
  // onWheel is passive, so preventDefault() there is a no-op.
  const overlayRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = overlayRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const cur = useConnectionStore.getState().zoomLevel;
      const next = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, cur + (e.deltaY < 0 ? 1 : -1))) as ZoomLevel;
      if (next === cur) return;
      useConnectionStore.getState().setZoomLevel(next);
      setZoom(next)
        .then((s) => useConnectionStore.getState().applyState(s))
        .catch(() => {
          /* next state poll reconciles */
        });
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, []);

  const onClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (!haveGeometry) return;
    const rect = e.currentTarget.getBoundingClientRect();
    if (rect.width <= 0) return;
    const raw = offsetHzForClientX(e.clientX, rect.left, rect.width, view, dialHz);
    const offset = snapOffsetToDecode(raw, recentOffsets, rect.width, view);
    const result = resolveWaterfallClick(offset, holdTxFreq);
    setRxFocusHz(result.rxFocusHz);
    // Double-guarded: setTxFreq itself no-ops while HOLD TX FREQ is engaged.
    if (result.txOffsetHz != null) runner.setTxFreq(result.txOffsetHz);
  };

  // Marker left-position as a percentage of the element width (pixelForOffsetHz
  // scales linearly, so passing 100 yields a percent directly — no rect read at
  // render time).
  const pct = (offsetHz: number): number => pixelForOffsetHz(offsetHz, 100, view, dialHz);
  const visible = (p: number): boolean => p >= 0 && p <= 100;

  const rxPct = pct(rxFocusHz);
  const txPct = pct(runner.audioHz);

  return (
    <div
      ref={overlayRef}
      className="ft8-wf-overlay"
      // Suppress the canvas dial-tune gesture underneath: as the topmost element
      // the overlay receives the pointer events the gesture would otherwise see.
      onPointerDown={(e) => e.stopPropagation()}
      onClick={onClick}
      role="presentation"
    >
      {haveGeometry && (
        <>
          {recent.map((r) => {
            const p = pct(clampOffsetHz(r.freqHz));
            if (!visible(p)) return null;
            const cls = classifyDecode(r, myCall);
            return (
              <span
                key={r.id}
                className="ft8-wf-decode-tick"
                style={{ left: `${p}%`, background: TICK_VAR[cls] }}
                title={`${r.text}  ·  ${Math.round(r.freqHz)} Hz  ·  ${
                  r.snrDb >= 0 ? `+${r.snrDb.toFixed(0)}` : r.snrDb.toFixed(0)
                } dB`}
              />
            );
          })}
          {visible(rxPct) && (
            <span
              className="ft8-wf-rx-cursor"
              style={{ left: `${rxPct}%` }}
              title={`RX focus ${Math.round(rxFocusHz)} Hz`}
            />
          )}
          {visible(txPct) && (
            <span
              className="ft8-wf-tx-marker"
              style={{ left: `${txPct}%` }}
              title={`TX ${Math.round(runner.audioHz)} Hz${holdTxFreq ? ' · HOLD' : ''}`}
            />
          )}
        </>
      )}
    </div>
  );
}
