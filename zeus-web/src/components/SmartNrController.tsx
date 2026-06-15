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
import {
  labelSmartNrProfile,
  recommendSmartNr,
  shapeSmartNrRecommendation,
  smartNrProfileKey,
  type SmartNrRecommendation,
} from '../dsp/smart-nr';
import { analyzeRxChain, type RxChainAnalysis } from '../dsp/rx-chain-health';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { useTxStore } from '../state/tx-store';

const SAMPLE_INTERVAL_MS = 1500;
const PROFILE_SWITCH_DWELL_SAMPLES = 6;
const APPLY_COOLDOWN_MS = 15000;

function round1(v: number): number {
  return Math.round(v * 10) / 10;
}

function sameNr(a: NrConfigDto, b: NrConfigDto): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

function shouldHoldForRxChain(rx: RxChainAnalysis): boolean {
  return rx.state === 'overload' || rx.state === 'underfilled' || rx.actionTone === 'protect';
}

export function SmartNrController() {
  useEffect(() => {
    let lastAt = 0;
    let lastAppliedAt = Number.NEGATIVE_INFINITY;
    let pendingProfileKey: string | null = null;
    let pendingCount = 0;
    let abort: AbortController | null = null;
    let releaseEstimatorConsumer: (() => void) | null = null;

    const resetPending = () => {
      pendingProfileKey = null;
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

    const setStatus = (
      rec: SmartNrRecommendation,
      shaped: NrConfigDto,
      pending: boolean,
      applied: boolean,
      rx: RxChainAnalysis | null = null,
      heldByRxChain = false,
    ) => {
      const c = rec.condition;
      const rxStatus = rx !== null && rx.state !== 'waiting' ? rx : null;
      useSmartNrStore.getState().setStatus({
        atUtc: new Date().toISOString(),
        profile: labelSmartNrProfile(shaped),
        reason: rec.reason,
        heldByRxChain,
        rxChainLabel: rxStatus?.label,
        rxChainRecommendation: rxStatus?.recommendation,
        rxChainTone: rxStatus?.actionTone,
        rxChainScore: rxStatus?.score,
        maxSnrDb: round1(c.maxSnrDb),
        occupancyPct: round1(c.occupancy6 * 100),
        coherentOccupancyPct: round1(c.coherentOccupancy6 * 100),
        impulsivePct: round1(c.impulsiveOccupancy12 * 100),
        peakCount: c.peakCount,
        coherentPeakCount: c.coherentPeakCount,
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

      const shaped = shapeSmartNrRecommendation(rec, settings);
      const rxMeters = useRxMetersStore.getState();
      const rx = analyzeRxChain({
        signalPk: rxMeters.signalPk,
        signalAv: rxMeters.signalAv,
        adcPk: rxMeters.adcPk,
        adcAv: rxMeters.adcAv,
        agcGain: rxMeters.agcGain,
        agcEnvPk: rxMeters.agcEnvPk,
        agcEnvAv: rxMeters.agcEnvAv,
        fallbackDbm: tx.rxDbm,
      });
      const heldByRxChain = shouldHoldForRxChain(rx);
      if (settings.automationMode === 'suggest') {
        setStatus(rec, shaped, false, false, rx, heldByRxChain);
        resetPending();
        return;
      }
      if (heldByRxChain) {
        setStatus(rec, shaped, false, false, rx, true);
        resetPending();
        return;
      }
      if (sameNr(conn.nr, shaped)) {
        setStatus(rec, shaped, false, false, rx);
        resetPending();
        return;
      }
      const targetProfileKey = smartNrProfileKey(shaped);
      const currentProfileKey = smartNrProfileKey(conn.nr);
      if (targetProfileKey === currentProfileKey) {
        setStatus(rec, shaped, false, false, rx);
        resetPending();
        return;
      }
      if (pendingProfileKey !== targetProfileKey) {
        pendingProfileKey = targetProfileKey;
        pendingCount = 1;
        setStatus(rec, shaped, true, false, rx);
        return;
      }
      pendingCount++;
      const requiredDwell = Math.max(settings.dwellSamples, PROFILE_SWITCH_DWELL_SAMPLES);
      const ready = pendingCount >= requiredDwell && now - lastAppliedAt >= APPLY_COOLDOWN_MS;
      setStatus(rec, shaped, !ready, ready, rx);
      if (ready) {
        lastAppliedAt = now;
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
