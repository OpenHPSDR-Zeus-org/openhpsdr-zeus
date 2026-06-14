// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useEffect } from 'react';
import { setNr, type NrConfigDto, type RadioStateDto } from '../api/client';
import { getNoiseFloor, getSignalConfidence, registerEstimatorConsumer } from '../dsp/signal-estimator';
import { recommendSmartNr, type SmartNrRecommendation } from '../dsp/smart-nr';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useSmartNrStore, type SmartNrSettings } from '../state/smart-nr-store';
import { useTxStore } from '../state/tx-store';

const SAMPLE_INTERVAL_MS = 1500;

function round1(v: number): number {
  return Math.round(v * 10) / 10;
}

function profileLabel(nr: NrConfigDto): string {
  if (nr.nbMode !== 'Off' && nr.snbEnabled) return `${nr.nbMode}+SNB`;
  if (nr.nrMode === 'Sbnr') return 'NR4';
  if (nr.nrMode === 'Emnr') return 'NR2';
  if (nr.nrMode === 'Anr') return 'NR1';
  if (nr.anfEnabled || nr.nbpNotchesEnabled) return 'Notch';
  return 'Light';
}

function sameNr(a: NrConfigDto, b: NrConfigDto): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function shapeRecommendation(rec: SmartNrRecommendation, settings: SmartNrSettings): NrConfigDto {
  const next: NrConfigDto = { ...rec.nr };
  const gain = Math.max(0, Math.min(1, settings.aggressiveness / 100));

  if (!settings.autoBlankerEnabled) {
    next.nbMode = 'Off';
    next.snbEnabled = false;
  } else if (next.nbMode !== 'Off') {
    next.nbThreshold = Math.min(next.nbThreshold, settings.maxBlankerThreshold);
  }

  if (!settings.autoNotchEnabled) {
    next.anfEnabled = false;
    next.nbpNotchesEnabled = false;
  }

  if (next.nrMode === 'Sbnr') {
    next.nr4ReductionAmount = Math.round(5 + gain * 8);
    next.nr4WhiteningFactor = rec.condition.weakSparse ? Math.round(4 + gain * 6) : Math.round(gain * 5);
    next.nr4PostFilterThreshold = Math.round(-10 + gain * 5);
  } else if (next.nrMode === 'Emnr') {
    next.emnrPost2Factor = Math.round(8 + gain * 10);
    next.emnrPost2Nlevel = Math.round(8 + gain * 10);
    next.emnrAeRun = gain >= 0.25;
  }

  if (settings.aggressiveness < 25 && !rec.condition.impulsiveNoise && !rec.condition.tonalInterference) {
    next.nrMode = 'Off';
  }

  return next;
}

export function SmartNrController() {
  useEffect(() => {
    let lastAt = 0;
    let pendingKey: string | null = null;
    let pendingCount = 0;
    let abort: AbortController | null = null;
    let releaseEstimatorConsumer: (() => void) | null = null;

    const resetPending = () => {
      pendingKey = null;
      pendingCount = 0;
    };
    const syncEstimatorConsumer = () => {
      const enabled = useSmartNrStore.getState().automationMode !== 'manual';
      if (enabled && releaseEstimatorConsumer === null) {
        releaseEstimatorConsumer = registerEstimatorConsumer();
      } else if (!enabled && releaseEstimatorConsumer !== null) {
        releaseEstimatorConsumer();
        releaseEstimatorConsumer = null;
      }
    };

    const setStatus = (rec: SmartNrRecommendation, shaped: NrConfigDto, pending: boolean, applied: boolean) => {
      const c = rec.condition;
      useSmartNrStore.getState().setStatus({
        atUtc: new Date().toISOString(),
        profile: profileLabel(shaped),
        reason: rec.reason,
        maxSnrDb: round1(c.maxSnrDb),
        occupancyPct: round1(c.occupancy6 * 100),
        peakCount: c.peakCount,
        pending,
        applied,
        nr: shaped,
      });
    };

    const applyNr = (nr: NrConfigDto) => {
      const conn = useConnectionStore.getState();
      abort?.abort();
      const ac = new AbortController();
      abort = ac;
      conn.setNr(nr);
      setNr(nr, ac.signal)
        .then((state: RadioStateDto) => {
          if (!ac.signal.aborted) useConnectionStore.getState().applyState(state);
        })
        .catch(() => {
          // Next poll or operator action will reconcile.
        });
    };

    const evaluate = () => {
      const now = Date.now();
      if (now - lastAt < SAMPLE_INTERVAL_MS) return;
      lastAt = now;

      const settings = useSmartNrStore.getState();
      if (settings.automationMode === 'manual') {
        resetPending();
        return;
      }
      const conn = useConnectionStore.getState();
      const tx = useTxStore.getState();
      if (conn.status !== 'Connected' || tx.moxOn || tx.tunOn) {
        resetPending();
        return;
      }

      const display = useDisplayStore.getState();
      const rec = recommendSmartNr({
        spectrum: display.panValid ? display.panDb : null,
        floor: getNoiseFloor(),
        confidence: getSignalConfidence(),
        current: conn.nr,
        mode: conn.mode,
      });
      if (!rec) return;

      const shaped = shapeRecommendation(rec, settings);
      const key = JSON.stringify(shaped);
      if (settings.automationMode === 'suggest') {
        setStatus(rec, shaped, false, false);
        resetPending();
        return;
      }
      if (sameNr(conn.nr, shaped)) {
        setStatus(rec, shaped, false, false);
        resetPending();
        return;
      }
      if (pendingKey !== key) {
        pendingKey = key;
        pendingCount = 1;
        setStatus(rec, shaped, true, false);
        return;
      }
      pendingCount++;
      const ready = pendingCount >= settings.dwellSamples;
      setStatus(rec, shaped, !ready, ready);
      if (ready) {
        applyNr(shaped);
        resetPending();
      }
    };

    const unsubDisplay = useDisplayStore.subscribe((state, prev) => {
      if (state.lastSeq !== prev.lastSeq) evaluate();
    });
    const unsubSettings = useSmartNrStore.subscribe((state, prev) => {
      if (state.automationMode !== prev.automationMode) syncEstimatorConsumer();
      if (state.automationMode === 'manual' && prev.automationMode !== 'manual') {
        state.setStatus(null);
        resetPending();
      }
    });
    syncEstimatorConsumer();

    return () => {
      abort?.abort();
      releaseEstimatorConsumer?.();
      unsubDisplay();
      unsubSettings();
    };
  }, []);

  return null;
}
