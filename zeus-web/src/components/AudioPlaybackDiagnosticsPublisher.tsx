// SPDX-License-Identifier: GPL-2.0-or-later
//
// Publishes the browser RX playback health that only the frontend can see:
// buffered audio, underruns, dropped scheduled samples, and main-thread vs
// render-schedule lateness.

import { useEffect } from 'react';
import { publishFrontendAudioPlaybackDiagnostics } from '../api/client';
import { getAudioClient } from '../audio/audio-client';
import { buildAudioPlaybackDiagnosticsPayload } from './audioPlaybackDiagnosticsPayload';

const PUBLISH_DEBOUNCE_MS = 250;
const REFRESH_MS = 2_000;

export function AudioPlaybackDiagnosticsPublisher() {
  useEffect(() => {
    const client = getAudioClient();
    let timer: number | null = null;
    let lastKey = '';
    let abort: AbortController | null = null;

    const publish = (force = false) => {
      const snapshot = client.diagnosticsSnapshot();
      const key = JSON.stringify(snapshot);
      if (!force && key === lastKey) return;
      lastKey = key;
      const payload = buildAudioPlaybackDiagnosticsPayload(snapshot);
      abort?.abort();
      const ac = new AbortController();
      abort = ac;
      publishFrontendAudioPlaybackDiagnostics(payload, ac.signal).catch(() => {
        // Diagnostics publishing is best-effort. The local audio client remains authoritative.
      });
    };

    const schedule = () => {
      if (timer !== null) window.clearTimeout(timer);
      timer = window.setTimeout(() => publish(false), PUBLISH_DEBOUNCE_MS);
    };

    const unsubscribe = client.subscribe(() => schedule());
    const refreshId = window.setInterval(() => publish(true), REFRESH_MS);

    schedule();
    return () => {
      if (timer !== null) window.clearTimeout(timer);
      window.clearInterval(refreshId);
      abort?.abort();
      unsubscribe();
    };
  }, []);

  return null;
}
