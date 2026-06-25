// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { useCallback, useEffect, useRef, useState } from 'react';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useTxStore } from '../state/tx-store';
import { useVfoLockStore } from '../state/vfo-lock-store';
import { useConnectionStore } from '../state/connection-store';
import { floorNormalizationOffsetDb, referenceFloorDb } from '../dsp/floor-normalization';
import { useRxDbWindowStore, type RxWfWindow } from '../state/rx-db-window-store';

const TICK_STRIDE_DB = 10;

// How often the master scale re-reads the median floor for its readout. The
// floor registry is plain module state (read per-frame by the renderer, not a
// reactive store), and the floors are EMA-smoothed so they drift slowly — a
// low-rate poll keeps the dB-above-floor labels current without re-rendering
// the slider on every spectrum tick.
const REF_FLOOR_POLL_MS = 500;

type WfDbScaleProps = {
  // Master mode: this single scale spans the whole multi-RX waterfall grid and
  // drives the GLOBAL window. Every pane derives its own window as
  // global + its measured floor offset (floor-normalization), so one drag moves
  // all bands together and their noise floors stay aligned — instead of editing
  // only the focused receiver's per-RX override (which leaves the rest behind).
  master?: boolean;
};

// Draggable dB scale on the waterfall, mirroring DbScale's look and feel
// but bound to the waterfall's independent dB window so panadapter and
// waterfall noise floors can be set separately. Mirrors DbScale's RX/TX
// swap so the operator can drag the waterfall scale during MOX/TUN without
// disturbing their RX waterfall view.
export function WfDbScale({ master = false }: WfDbScaleProps = {}) {
  // The RX waterfall scale follows the FOCUSED receiver: RX1 (index 0) edits
  // the global window; RX2+ each edit their own per-receiver window (persisted).
  // Select the reactive deps so the slider re-renders on focus / global-window /
  // override changes, then resolve this receiver's effective window.
  const focusedRxIndex = useConnectionStore((s) => s.focusedRxIndex);
  const globalRxMin = useDisplaySettingsStore((s) => s.wfDbMin);
  const globalRxMax = useDisplaySettingsStore((s) => s.wfDbMax);
  const rxOverride = useRxDbWindowStore((s) => s.overrides[focusedRxIndex]);
  const shiftGlobalRx = useDisplaySettingsStore((s) => s.shiftWfDbRange);
  const shiftRxWindow = useRxDbWindowStore((s) => s.shiftRxWfWindow);
  const clearAllRxWindows = useRxDbWindowStore((s) => s.clearAllRxWfWindows);
  const txDbMin = useDisplaySettingsStore((s) => s.wfTxDbMin);
  const txDbMax = useDisplaySettingsStore((s) => s.wfTxDbMax);
  const shiftTx = useDisplaySettingsStore((s) => s.shiftWfTxDbRange);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const keyed = moxOn || tunOn;

  // Effective RX window shown on the scale.
  // - Master mode: always the global window — it's the common reference every
  //   pane floor-normalizes against, so the ticks read the band-agnostic scale
  //   that all RX waterfalls share.
  // - Otherwise (in-tile, focus-bound): index 0 = global; an explicit override
  //   wins; else the global window floor-normalized to RX1.
  let rxWin: RxWfWindow;
  if (master || focusedRxIndex <= 0) {
    rxWin = { wfDbMin: globalRxMin, wfDbMax: globalRxMax };
  } else if (rxOverride) {
    rxWin = rxOverride;
  } else {
    const off = floorNormalizationOffsetDb(focusedRxIndex);
    rxWin = { wfDbMin: globalRxMin + off, wfDbMax: globalRxMax + off };
  }

  // RX-side shift. Master mode drives the GLOBAL window — every pane follows it
  // via its own measured floor offset, so all bands move together and stay
  // evened-out. It also drops any stale per-RX overrides (they'd opt a pane out
  // of normalization and leave it behind). Non-master routes to the global
  // window for RX1, else the focused RX's per-receiver override.
  const shiftRx = useCallback(
    (delta: number) => {
      if (master) {
        clearAllRxWindows();
        shiftGlobalRx(delta);
      } else if (focusedRxIndex <= 0) {
        shiftGlobalRx(delta);
      } else {
        shiftRxWindow(focusedRxIndex, delta);
      }
    },
    [master, focusedRxIndex, shiftGlobalRx, shiftRxWindow, clearAllRxWindows],
  );

  // Pick the active range + shifter based on whether we're keyed. Mirrors
  // DbScale.tsx — keyed uses the global TX waterfall window so the operator can
  // hide the silence-time TX floor without moving the RX noise-floor view.
  const dbMin = keyed ? txDbMin : rxWin.wfDbMin;
  const dbMax = keyed ? txDbMax : rxWin.wfDbMax;
  const shift = keyed ? shiftTx : shiftRx;

  // Master RX scale reads out as dB ABOVE the shared noise floor (floor = 0).
  // That's the only frame true for every pane at once — normalization pins each
  // band's floor to the same colour, so "+10 dB above floor" means the same
  // everywhere, whereas an absolute dBm number only matches one band. Poll the
  // median floor at a low rate (it's non-reactive module state). Null while
  // keyed/non-master or before any floor is known → fall back to absolute dB.
  const [refFloorDb, setRefFloorDb] = useState<number | null>(null);
  const relativeToFloor = master && !keyed && refFloorDb != null;
  useEffect(() => {
    if (!master || keyed) {
      setRefFloorDb(null);
      return;
    }
    const tick = () => setRefFloorDb(referenceFloorDb());
    tick();
    const id = window.setInterval(tick, REF_FLOOR_POLL_MS);
    return () => window.clearInterval(id);
  }, [master, keyed]);

  const dragState = useRef<{
    startY: number;
    startDbMin: number;
    startDbMax: number;
    pointerId: number;
    containerHeight: number;
    lastShiftApplied: number;
  } | null>(null);

  const onPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      // VFO/panel lock — see DbScale.tsx; same gate, separate widget.
      if (useVfoLockStore.getState().locked) return;
      const rect = e.currentTarget.getBoundingClientRect();
      dragState.current = {
        startY: e.clientY,
        startDbMin: dbMin,
        startDbMax: dbMax,
        pointerId: e.pointerId,
        containerHeight: rect.height,
        lastShiftApplied: 0,
      };
      e.currentTarget.setPointerCapture(e.pointerId);
    },
    [dbMin, dbMax],
  );

  const onPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragState.current;
      if (!d || e.pointerId !== d.pointerId) return;
      const dySig = e.clientY - d.startY;
      const dbPerPixel = (d.startDbMax - d.startDbMin) / d.containerHeight;
      const deltaDb = dySig * dbPerPixel;
      // Incremental, not total — see DbScale for the rationale; same
      // stale-closure drift bug (issue #234) lived here too.
      const incrementalShift = deltaDb - d.lastShiftApplied;
      if (Math.abs(incrementalShift) > 0.5) {
        shift(incrementalShift);
        d.lastShiftApplied = deltaDb;
      }
    },
    [shift],
  );

  const onPointerUp = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = dragState.current;
      if (!d || e.pointerId !== d.pointerId) return;
      e.currentTarget.releasePointerCapture(e.pointerId);
      dragState.current = null;
    },
    [],
  );

  // `label` is what the tick reads; `topPct` is its vertical position (always
  // from the absolute window). In relative mode we step from the floor so a tick
  // sits exactly on it (label 0) with clean +/-10 dB marks above and below;
  // otherwise we step on absolute multiples of 10 as before.
  const anchor = relativeToFloor ? refFloorDb! : 0;
  const firstK = Math.ceil((dbMin - anchor) / TICK_STRIDE_DB);
  const lastK = Math.floor((dbMax - anchor) / TICK_STRIDE_DB);
  const ticks: { label: number; topPct: number }[] = [];
  for (let k = firstK; k <= lastK; k++) {
    const absDb = anchor + k * TICK_STRIDE_DB;
    const topPct = ((dbMax - absDb) / (dbMax - dbMin)) * 100;
    ticks.push({ label: relativeToFloor ? k * TICK_STRIDE_DB : absDb, topPct });
  }

  return (
    <div
      role="slider"
      aria-label={relativeToFloor ? 'Waterfall dB scale (dB above noise floor)' : 'Waterfall dB scale'}
      aria-valuemin={Math.round(relativeToFloor ? dbMin - refFloorDb! : dbMin)}
      aria-valuemax={Math.round(relativeToFloor ? dbMax - refFloorDb! : dbMax)}
      aria-valuenow={Math.round((dbMin + dbMax) / 2 - (relativeToFloor ? refFloorDb! : 0))}
      title={
        relativeToFloor
          ? `Waterfall brightness — dB above the shared noise floor (≈${Math.round(refFloorDb!)} dBm). One drag evens out every RX band.`
          : undefined
      }
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      className="absolute left-0 top-0 bottom-0 z-10 w-10 cursor-ns-resize touch-none select-none bg-neutral-950/60"
    >
      {ticks.map((t) => (
        <div
          key={t.label}
          className="absolute left-0 right-0 flex items-center gap-1"
          style={{ top: `${t.topPct}%`, transform: 'translateY(-50%)' }}
        >
          <div className="h-px w-1.5 bg-neutral-500" />
          <div className="font-mono text-[9px] leading-none text-neutral-400">
            {relativeToFloor && t.label > 0 ? `+${t.label}` : t.label}
          </div>
        </div>
      ))}
      {/* Master mode: anchor caption — the absolute level the "0" tick (the
          shared noise floor) sits at, so the relative scale still has dBm
          context. */}
      {relativeToFloor && (
        <div
          className="absolute bottom-1 left-0 right-0 text-center font-mono text-[8px] leading-tight text-neutral-500"
          title="Median noise floor across all RX (the 0 dB reference)"
        >
          flr
          <br />
          {Math.round(refFloorDb!)}
        </div>
      )}
    </div>
  );
}
