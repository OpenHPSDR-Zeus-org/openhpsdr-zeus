// SPDX-License-Identifier: GPL-2.0-or-later
//
// Publishes the browser RX playback health that only the frontend can see:
// buffered audio, underruns, dropped scheduled samples, and main-thread vs
// render-schedule lateness.

import { useEffect } from 'react';
import { publishFrontendAudioPlaybackDiagnostics } from '../api/client';
import { getAudioClient } from '../audio/audio-client';
import { startEfficientPolling } from '../util/efficient-polling';
import { buildAudioPlaybackDiagnosticsPayload } from './audioPlaybackDiagnosticsPayload';

const PUBLISH_DEBOUNCE_MS = 250;
const PLAYING_REFRESH_MS = 2_000;
const IDLE_REFRESH_MS = 10_000;
const HIDDEN_REFRESH_MS = false;

function pageVisible(): boolean {
  return typeof document === 'undefined' || !document.hidden;
}

export function AudioPlaybackDiagnosticsPublisher() {
  useEffect(() => {
    const client = getAudioClient();
    let timer: number | null = null;
    let lastStateKey = '';
    let lastKey = '';
    let abort: AbortController | null = null;

    const refreshInterval = () =>
      client.diagnosticsSnapshot().playbackState === 'playing'
        ? PLAYING_REFRESH_MS
        : IDLE_REFRESH_MS;

    const publish = async (force = false) => {
      if (!pageVisible()) return;
      const snapshot = client.diagnosticsSnapshot();
      const key = JSON.stringify(snapshot);
      if (!force && key === lastKey) return;
      lastKey = key;
      const payload = buildAudioPlaybackDiagnosticsPayload(snapshot);
      abort?.abort();
      const ac = new AbortController();
      abort = ac;
      try {
        await publishFrontendAudioPlaybackDiagnostics(payload, ac.signal);
      } catch {
        // Diagnostics publishing is best-effort. The local audio client remains authoritative.
      } finally {
        if (abort === ac) abort = null;
      }
    };

    const schedule = () => {
      if (!pageVisible()) return;
      if (timer !== null) window.clearTimeout(timer);
      timer = window.setTimeout(() => {
        timer = null;
        void publish(false);
      }, PUBLISH_DEBOUNCE_MS);
    };

    const unsubscribe = client.subscribe((state) => {
      const stateKey = state.kind === 'error' ? `${state.kind}:${state.message}` : state.kind;
      if (stateKey === lastStateKey) return;
      lastStateKey = stateKey;
      schedule();
    });
    const stopRefresh = startEfficientPolling(
      () => publish(true),
      {
        intervalMs: refreshInterval,
        hiddenIntervalMs: HIDDEN_REFRESH_MS,
        leading: false,
        isEnabled: pageVisible,
      },
    );

    schedule();
    return () => {
      if (timer !== null) window.clearTimeout(timer);
      stopRefresh();
      abort?.abort();
      unsubscribe();
    };
  }, []);

  return null;
}
