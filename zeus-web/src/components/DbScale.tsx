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

import { useCallback, useRef } from 'react';
import { useDisplaySettingsStore } from '../state/display-settings-store';
import { useTxStore } from '../state/tx-store';
import { useVfoLockStore } from '../state/vfo-lock-store';

// Tick stride in dB. Thetis defaults to 5 dB; our smaller canvas reads
// cleaner at 10 dB. Tick label rendered at every stride, minor line between.
const TICK_STRIDE_DB = 10;

// Draggable dB scale along the left edge of the panadapter. Vertical
// pointer drag shifts both dbMin and dbMax in lockstep — Thetis-style, see
// PanDisplay.cs:3684-3700. No independent min/max scaling; just an offset.
// Sits inside the Panadapter container as an absolutely-positioned column.
export function DbScale() {
  const rxDbMin = useDisplaySettingsStore((s) => s.dbMin);
  const rxDbMax = useDisplaySettingsStore((s) => s.dbMax);
  const txDbMin = useDisplaySettingsStore((s) => s.txDbMin);
  const txDbMax = useDisplaySettingsStore((s) => s.txDbMax);
  const shiftDbRange = useDisplaySettingsStore((s) => s.shiftDbRange);
  const shiftTxDbRange = useDisplaySettingsStore((s) => s.shiftTxDbRange);
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const keyed = moxOn || tunOn;

  // Pick the active range + shifter based on whether we're looking at TX or
  // RX pixels. Thetis-parity: each side has its own Grid Min/Max so the
  // operator can hide the silence-time TX floor without moving the RX
  // noise-floor view.
  const dbMin = keyed ? txDbMin : rxDbMin;
  const dbMax = keyed ? txDbMax : rxDbMax;
  const shift = keyed ? shiftTxDbRange : shiftDbRange;

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
      // Honour the VFO/panel lock — when engaged, vertical drag on the dB
      // scale must not shift gain. The mobile padlock toggles this; on
      // desktop the flag stays false unless wired up. Pinch-to-zoom and
      // wheel-zoom paths are unaffected (they live in pan-tune-gesture).
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
      // Content-follows-finger: drag DOWN (dy > 0) moves the trace DOWN on
      // the canvas, which means raising both dbMin and dbMax (a fixed signal
      // then sits lower in the visible range). Adding deltaDb achieves this.
      const dbPerPixel = (d.startDbMax - d.startDbMin) / d.containerHeight;
      const deltaDb = dySig * dbPerPixel;
      // Apply the *incremental* shift since the last call, not the total
      // shift since drag-start. The total-shift path read `dbMin` from the
      // closure to compute "how much further to move", but the closure can
      // be stale if a re-render hasn't committed yet — leading to
      // accumulated drift that, after a MOX/RX swap, presents as the two
      // panadapter ranges no longer being independent (issue #234).
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

  // Tick labels: every TICK_STRIDE_DB in the visible [dbMin..dbMax] range.
  // Placed as top% where top 0% = dbMax (visually at top), 100% = dbMin.
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
      aria-label="dB scale"
      aria-valuemin={Math.round(dbMin)}
      aria-valuemax={Math.round(dbMax)}
      aria-valuenow={Math.round((dbMin + dbMax) / 2)}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerUp}
      className="absolute left-0 top-5 bottom-0 z-10 w-10 cursor-ns-resize touch-none select-none bg-neutral-950/60"
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
