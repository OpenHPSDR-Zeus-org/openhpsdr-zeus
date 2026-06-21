// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { useCallback, useRef } from 'react';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useTxStore } from '../state/tx-store';
import { useVfoLockStore } from '../state/vfo-lock-store';

const TICK_STRIDE_DB = 10;

// Draggable dB scale on the waterfall, mirroring DbScale's look and feel
// but bound to the waterfall's independent dB window so panadapter and
// waterfall noise floors can be set separately. Mirrors DbScale's RX/TX
// swap so the operator can drag the waterfall scale during MOX/TUN without
// disturbing their RX waterfall view.
export function WfDbScale() {
  const rxDbMin = useDisplaySettingsStore((s) => s.wfDbMin);
  const rxDbMax = useDisplaySettingsStore((s) => s.wfDbMax);
  const txDbMin = useDisplaySettingsStore((s) => s.wfTxDbMin);
  const txDbMax = useDisplaySettingsStore((s) => s.wfTxDbMax);
  const shiftRx = useDisplaySettingsStore((s) => s.shiftWfDbRange);
  const shiftTx = useDisplaySettingsStore((s) => s.shiftWfTxDbRange);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const keyed = moxOn || tunOn;

  // Pick the active range + shifter based on whether we're keyed. Mirrors
  // DbScale.tsx — each side has its own waterfall Grid Min/Max so the
  // operator can hide the silence-time TX floor without moving the RX
  // noise-floor view.
  const dbMin = keyed ? txDbMin : rxDbMin;
  const dbMax = keyed ? txDbMax : rxDbMax;
  const shift = keyed ? shiftTx : shiftRx;

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

  const firstTick = Math.ceil(dbMin / TICK_STRIDE_DB) * TICK_STRIDE_DB;
  const lastTick = Math.floor(dbMax / TICK_STRIDE_DB) * TICK_STRIDE_DB;
  const ticks: { db: number; topPct: number }[] = [];
  for (let db = firstTick; db <= lastTick; db += TICK_STRIDE_DB) {
    const topPct = ((dbMax - db) / (dbMax - dbMin)) * 100;
    ticks.push({ db, topPct });
  }

  return (
    <div
      role="slider"
      aria-label="Waterfall dB scale"
      aria-valuemin={Math.round(dbMin)}
      aria-valuemax={Math.round(dbMax)}
      aria-valuenow={Math.round((dbMin + dbMax) / 2)}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      className="absolute left-0 top-0 bottom-0 z-10 w-10 cursor-ns-resize touch-none select-none bg-neutral-950/60"
    >
      {ticks.map((t) => (
        <div
          key={t.db}
          className="absolute left-0 right-0 flex items-center gap-1"
          style={{ top: `${t.topPct}%`, transform: 'translateY(-50%)' }}
        >
          <div className="h-px w-1.5 bg-neutral-500" />
          <div className="font-mono text-[9px] leading-none text-neutral-400">
            {t.db}
          </div>
        </div>
      ))}
    </div>
  );
}
