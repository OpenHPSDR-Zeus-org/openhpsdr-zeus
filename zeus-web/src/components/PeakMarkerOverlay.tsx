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

import { useEffect, useState } from 'react';
import { detectPeaks, peakAlpha, useSignalEnhanceStore, type DetectedPeak } from '../dsp/signal-estimator';
import { useDisplayStore } from '../state/display-store';
import { useDisplaySettingsStore } from '../state/display-settings-store';

// Snap-target markers: short ticks rising from the noise-floor baseline at each
// detected carrier, so the operator can see exactly where a snap click will
// land. Shown only while SNAP is engaged. Coloured to match the operator's RX
// trace colour (sanctioned amber peak-tick by default — signal-strength
// visualisation, varying alpha; see dev-conventions.md), with opacity scaled by
// the peak's SNR above the noise floor.
//
// Recompute is throttled to ~8 Hz: markers don't need the 30 Hz frame rate, and
// the spectrum surfaces deliberately keep React out of the per-frame path. The
// number of marker divs is bounded by PEAK_MAX_COUNT in the estimator.
const RECOMPUTE_MIN_INTERVAL_MS = 120;

type Snapshot = { peaks: DetectedPeak[]; centerHz: number; spanHz: number };

const EMPTY: Snapshot = { peaks: [], centerHz: 0, spanHz: 0 };

export function PeakMarkerOverlay() {
  const snapEnabled = useSignalEnhanceStore((s) => s.snapEnabled);
  const peakMinSnrDb = useSignalEnhanceStore((s) => s.peakMinSnrDb);
  const traceColor = useDisplaySettingsStore((s) => s.rxTraceColor);
  const [snap, setSnap] = useState<Snapshot>(EMPTY);

  useEffect(() => {
    if (!snapEnabled) {
      setSnap(EMPTY);
      return;
    }
    let lastAt = 0;
    const recompute = () => {
      const s = useDisplayStore.getState();
      if (!s.panDb || s.hzPerPixel <= 0) {
        setSnap(EMPTY);
        return;
      }
      const centerHz = Number(s.centerHz);
      const spanHz = s.panDb.length * s.hzPerPixel;
      setSnap({ peaks: detectPeaks(s.panDb, centerHz, s.hzPerPixel), centerHz, spanHz });
    };
    const unsub = useDisplayStore.subscribe((state, prev) => {
      if (state.lastSeq === prev.lastSeq) return;
      const now = performance.now();
      if (now - lastAt < RECOMPUTE_MIN_INTERVAL_MS) return;
      lastAt = now;
      recompute();
    });
    recompute();
    return unsub;
  }, [snapEnabled, peakMinSnrDb]);

  if (!snapEnabled || snap.peaks.length === 0 || snap.spanHz <= 0) return null;

  const startHz = snap.centerHz - snap.spanHz / 2;

  return (
    <>
      {snap.peaks.map((p) => {
        const pct = ((p.hz - startHz) / snap.spanHz) * 100;
        if (pct < -1 || pct > 101) return null;
        return (
          <div
            key={p.hz}
            className="pointer-events-none absolute bottom-0 z-[7] h-4 w-0.5 -translate-x-1/2"
            style={{
              left: `${pct}%`,
              background: traceColor,
              opacity: peakAlpha(p.snrDb),
              boxShadow: '0 0 3px rgba(0,0,0,0.9)',
            }}
          />
        );
      })}
    </>
  );
}
