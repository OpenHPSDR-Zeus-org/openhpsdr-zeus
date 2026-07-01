// SPDX-License-Identifier: GPL-2.0-or-later
//
// Mirrors frontend-only DSP scene intelligence into the backend diagnostics
// snapshot. This keeps Smart NR and Signal Intelligence evidence auditable from
// /api/radio/diagnostics without streaming raw spectrum bins through REST.

import { useEffect } from 'react';
import { publishFrontendDspSceneDiagnostics } from '../api/client';
import { analyzeRxChain } from '../dsp/rx-chain-health';
import {
  estimateAdjacentNoiseProfile,
  estimateSceneTopPeaks,
  getNoiseFloor,
  getSignalConfidence,
  useSignalEnhanceStore,
} from '../dsp/signal-estimator';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { useTxStore } from '../state/tx-store';
import { startEfficientPolling } from '../util/efficient-polling';
import { buildFrontendDspSceneDiagnosticsPayload } from './dspSceneDiagnosticsPayload';

const PUBLISH_DEBOUNCE_MS = 250;
const RX_CHAIN_REFRESH_MS = 1_000;
const REFRESH_MS = 10_000;
const HIDDEN_REFRESH_MS = false;

function pageVisible(): boolean {
  return typeof document === 'undefined' || !document.hidden;
}

function shouldPublishDiagnostics(): boolean {
  return pageVisible() && useConnectionStore.getState().status === 'Connected';
}

function liveRxChainForDiagnostics() {
  const tx = useTxStore.getState();
  if (tx.moxOn || tx.tunOn) return null;
  const meters = useRxMetersStore.getState();
  const connection = useConnectionStore.getState();
  const rx = analyzeRxChain(
    {
      signalPk: meters.signalPk,
      signalAv: meters.signalAv,
      adcPk: meters.adcPk,
      adcAv: meters.adcAv,
      agcGain: meters.agcGain,
      agcEnvPk: meters.agcEnvPk,
      agcEnvAv: meters.agcEnvAv,
      fallbackDbm: tx.rxDbm,
    },
    {
      autoAgcEnabled: connection.autoAgcEnabled,
      autoAttEnabled: connection.autoAttEnabled,
    },
  );
  return rx.state === 'waiting' ? null : rx;
}

export function DspSceneDiagnosticsPublisher() {
  useEffect(() => {
    let timer: number | null = null;
    let lastKey = '';
    let abort: AbortController | null = null;

    const publish = async (force = false) => {
      if (!shouldPublishDiagnostics()) return;
      const conn = useConnectionStore.getState();
      const display = useDisplayStore.getState();
      const adjacentNoise = estimateAdjacentNoiseProfile({
        spectrum: display.panValid ? display.panDb : null,
        floor: getNoiseFloor(),
        confidence: getSignalConfidence(),
        centerHz: display.centerHz,
        hzPerPixel: display.hzPerPixel,
        dialHz: conn.vfoHz,
        filterLowHz: conn.filterLowHz,
        filterHighHz: conn.filterHighHz,
      });
      const topPeaks = estimateSceneTopPeaks({
        spectrum: display.panValid ? display.panDb : null,
        floor: getNoiseFloor(),
        confidence: getSignalConfidence(),
        centerHz: display.centerHz,
        hzPerPixel: display.hzPerPixel,
        dialHz: conn.vfoHz,
        limit: 8,
      });
      const payload = buildFrontendDspSceneDiagnosticsPayload(
        conn.mode,
        useSignalEnhanceStore.getState().sceneStatus,
        useSmartNrStore.getState().status,
        liveRxChainForDiagnostics(),
        adjacentNoise,
        topPeaks,
      );
      if (!payload) return;
      const key = JSON.stringify(payload);
      if (!force && key === lastKey) return;
      lastKey = key;
      abort?.abort();
      const ac = new AbortController();
      abort = ac;
      try {
        await publishFrontendDspSceneDiagnostics(payload, ac.signal);
      } catch {
        // Diagnostics publishing is best-effort. The local UI remains authoritative.
      } finally {
        if (abort === ac) abort = null;
      }
    };

    const schedule = () => {
      if (!shouldPublishDiagnostics()) return;
      if (timer !== null) window.clearTimeout(timer);
      timer = window.setTimeout(() => {
        timer = null;
        void publish(false);
      }, PUBLISH_DEBOUNCE_MS);
    };

    const unsubSignal = useSignalEnhanceStore.subscribe((state, prev) => {
      if (state.sceneStatus !== prev.sceneStatus) schedule();
    });
    const unsubSmart = useSmartNrStore.subscribe((state, prev) => {
      if (state.status !== prev.status) schedule();
    });
    const unsubMode = useConnectionStore.subscribe((state, prev) => {
      if (
        state.mode !== prev.mode ||
        state.autoAgcEnabled !== prev.autoAgcEnabled ||
        state.autoAttEnabled !== prev.autoAttEnabled
      ) {
        schedule();
      }
    });
    const unsubTx = useTxStore.subscribe((state, prev) => {
      if (
        state.moxOn !== prev.moxOn ||
        state.tunOn !== prev.tunOn
      ) {
        schedule();
      }
    });
    const stopRxRefresh = startEfficientPolling(
      () => publish(false),
      {
        intervalMs: RX_CHAIN_REFRESH_MS,
        hiddenIntervalMs: HIDDEN_REFRESH_MS,
        isEnabled: shouldPublishDiagnostics,
      },
    );
    const stopHeartbeat = startEfficientPolling(
      () => publish(true),
      {
        intervalMs: REFRESH_MS,
        hiddenIntervalMs: HIDDEN_REFRESH_MS,
        leading: false,
        isEnabled: shouldPublishDiagnostics,
      },
    );

    schedule();
    return () => {
      if (timer !== null) window.clearTimeout(timer);
      stopRxRefresh();
      stopHeartbeat();
      abort?.abort();
      unsubSignal();
      unsubSmart();
      unsubMode();
      unsubTx();
    };
  }, []);

  return null;
}
