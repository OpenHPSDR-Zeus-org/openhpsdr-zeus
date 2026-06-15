// SPDX-License-Identifier: GPL-2.0-or-later
//
// Mirrors frontend-only DSP scene intelligence into the backend diagnostics
// snapshot. This keeps Smart NR and Signal Intelligence evidence auditable from
// /api/radio/diagnostics without streaming raw spectrum bins through REST.

import { useEffect } from 'react';
import { publishFrontendDspSceneDiagnostics } from '../api/client';
import { useSignalEnhanceStore } from '../dsp/signal-estimator';
import { useConnectionStore } from '../state/connection-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { buildFrontendDspSceneDiagnosticsPayload } from './dspSceneDiagnosticsPayload';

const PUBLISH_DEBOUNCE_MS = 250;
const REFRESH_MS = 10_000;

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
      if (state.mode !== prev.mode) schedule();
    });
    const refreshId = window.setInterval(() => publish(true), REFRESH_MS);

    schedule();
    return () => {
      if (timer !== null) window.clearTimeout(timer);
      window.clearInterval(refreshId);
      abort?.abort();
      unsubSignal();
      unsubSmart();
      unsubMode();
    };
  }, []);

  return null;
}
