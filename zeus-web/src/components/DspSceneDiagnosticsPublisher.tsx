// SPDX-License-Identifier: GPL-2.0-or-later
//
// Mirrors frontend-only DSP scene intelligence into the backend diagnostics
// snapshot. This keeps Smart NR and Signal Intelligence evidence auditable from
// /api/radio/diagnostics without streaming raw spectrum bins through REST.

import { useEffect } from 'react';
import { publishFrontendDspSceneDiagnostics } from '../api/client';
import { analyzeRxChain } from '../dsp/rx-chain-health';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useConnectionStore } from '../state/connection-store';
import { useRxMetersStore } from '../state/rx-meters-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { useTxStore } from '../state/tx-store';
import { buildFrontendDspSceneDiagnosticsPayload } from './dspSceneDiagnosticsPayload';

const PUBLISH_DEBOUNCE_MS = 250;
const RX_CHAIN_REFRESH_MS = 1_000;
const REFRESH_MS = 10_000;

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

    const publish = (force = false) => {
      const payload = buildFrontendDspSceneDiagnosticsPayload(
        useConnectionStore.getState().mode,
        useSignalEnhanceStore.getState().sceneStatus,
        useSmartNrStore.getState().status,
        liveRxChainForDiagnostics(),
      );
      if (!payload) return;
      const key = JSON.stringify(payload);
      if (!force && key === lastKey) return;
      lastKey = key;
      abort?.abort();
      const ac = new AbortController();
      abort = ac;
      publishFrontendDspSceneDiagnostics(payload, ac.signal).catch(() => {
        // Diagnostics publishing is best-effort. The local UI remains authoritative.
      });
    };

    const schedule = () => {
      if (timer !== null) window.clearTimeout(timer);
      timer = window.setTimeout(() => publish(false), PUBLISH_DEBOUNCE_MS);
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
    const unsubRxMeters = useRxMetersStore.subscribe((state, prev) => {
      if (
        state.signalPk !== prev.signalPk ||
        state.signalAv !== prev.signalAv ||
        state.adcPk !== prev.adcPk ||
        state.adcAv !== prev.adcAv ||
        state.agcGain !== prev.agcGain ||
        state.agcEnvPk !== prev.agcEnvPk ||
        state.agcEnvAv !== prev.agcEnvAv
      ) {
        schedule();
      }
    });
    const unsubTx = useTxStore.subscribe((state, prev) => {
      if (
        state.rxDbm !== prev.rxDbm ||
        state.moxOn !== prev.moxOn ||
        state.tunOn !== prev.tunOn
      ) {
        schedule();
      }
    });
    const rxRefreshId = window.setInterval(() => publish(false), RX_CHAIN_REFRESH_MS);
    const refreshId = window.setInterval(() => publish(true), REFRESH_MS);

    schedule();
    return () => {
      if (timer !== null) window.clearTimeout(timer);
      window.clearInterval(rxRefreshId);
      window.clearInterval(refreshId);
      abort?.abort();
      unsubSignal();
      unsubSmart();
      unsubMode();
      unsubRxMeters();
      unsubTx();
    };
  }, []);

  return null;
}
