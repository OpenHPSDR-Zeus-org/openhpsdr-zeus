// SPDX-License-Identifier: GPL-2.0-or-later
//
// HamClockPanel — workspace tile that embeds OpenHamClock (MIT,
// github.com/accius/openhamclock) in an <iframe>. Lives in its own
// auto-created "HamClock" layout (see hamclock-store.openWorkspace), filling
// the workspace edge-to-edge.
//
// OpenHamClock runs as a Zeus-supervised Node sidecar (HamClockService). This
// panel auto-starts the sidecar when mounted, polls status until it's Running,
// then shows the iframe; otherwise it shows install/start state and points the
// operator at Settings → HamClock.

import { useEffect, useRef, useState } from 'react';
import type { ActivationSpotDto } from '../../api/client';
import { openExternalUrl } from '../../components/report-problem/openExternalUrl';
import { hamclockIframeUrl, useHamClockStore } from '../../state/hamclock-store';
import { useSpotsStore } from '../../state/spots-store';

const HAMCLOCK_DX_TUNE_MESSAGE = 'zeus.hamclock.dxSpotTune';
const ZEUS_OPEN_EXTERNAL_MESSAGE = 'zeus.openExternal';

interface ZeusOpenExternalMessage {
  type: typeof ZEUS_OPEN_EXTERNAL_MESSAGE;
  url: string;
}

function isZeusOpenExternalMessage(value: unknown): value is ZeusOpenExternalMessage {
  if (!value || typeof value !== 'object') return false;
  const msg = value as Partial<ZeusOpenExternalMessage>;
  return (
    msg.type === ZEUS_OPEN_EXTERNAL_MESSAGE &&
    typeof msg.url === 'string' &&
    /^https?:\/\//i.test(msg.url)
  );
}

interface HamClockDxTuneMessage {
  type: typeof HAMCLOCK_DX_TUNE_MESSAGE;
  source?: string;
  freqHz: number;
  mode?: string;
  callsign?: string;
}

function isHamClockDxTuneMessage(value: unknown): value is HamClockDxTuneMessage {
  if (!value || typeof value !== 'object') return false;
  const msg = value as Partial<HamClockDxTuneMessage>;
  return (
    msg.type === HAMCLOCK_DX_TUNE_MESSAGE &&
    typeof msg.freqHz === 'number' &&
    Number.isFinite(msg.freqHz) &&
    msg.freqHz > 0
  );
}

function toActivationSpot(msg: HamClockDxTuneMessage): ActivationSpotDto {
  const activator =
    typeof msg.callsign === 'string' && msg.callsign.trim().length > 0
      ? msg.callsign.trim().toUpperCase()
      : 'DX';
  const mode =
    typeof msg.mode === 'string' && msg.mode.trim().length > 0
      ? msg.mode.trim().toUpperCase()
      : '';

  return {
    source: 'DX',
    activator,
    freqHz: Math.round(msg.freqHz),
    mode,
    reference: 'HamClock DX',
    name: null,
    location: null,
    grid: null,
    comments: null,
    spotter: null,
    spotTime: new Date().toISOString().replace(/\.\d{3}Z$/, ''),
  };
}

export function HamClockPanel() {
  const status = useHamClockStore((s) => s.status);
  const loadStatus = useHamClockStore((s) => s.loadStatus);
  const start = useHamClockStore((s) => s.start);
  const loadSpotSettings = useSpotsStore((s) => s.loadSettings);
  const tuneToSpot = useSpotsStore((s) => s.tuneToSpot);
  const iframeRef = useRef<HTMLIFrameElement | null>(null);
  // Bumped to force a one-time iframe remount after a cold start (see below).
  const [reloadNonce, setReloadNonce] = useState(0);
  // false once we know HamClock was NOT already running when this panel
  // mounted (i.e. this panel cold-started the sidecar). null until status
  // first resolves; true means a returning session that needs no reload.
  const wasRunningOnMountRef = useRef<boolean | null>(null);
  const didColdStartReloadRef = useRef(false);

  // Auto-start the sidecar on mount if it's installed but not running, then
  // poll until it reaches Running so the iframe appears. Faster tick while an
  // install/start is in flight.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      await loadStatus();
      if (cancelled) return;
      const s = useHamClockStore.getState().status;
      wasRunningOnMountRef.current = s.running && s.port > 0;
      if (s.installed && !s.running && !s.busy && s.phase !== 'Starting') {
        await start();
      }
    })();
    return () => {
      cancelled = true;
    };
    // Run once on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const busy = status.busy || status.phase === 'Installing' || status.phase === 'Starting';
    if (!busy && status.running) return; // settled — no need to keep polling
    const id = window.setInterval(() => void loadStatus(), busy ? 1500 : 4000);
    return () => window.clearInterval(id);
  }, [loadStatus, status.busy, status.phase, status.running]);

  const running = status.running && status.port > 0;
  const url = running ? hamclockIframeUrl(status.port) : '';

  // Cold-start fix: on a brand-new install the backend reports Running as soon
  // as the sidecar's TCP socket accepts (HamClockService.WaitForHealthAsync) —
  // before OpenHamClock has fetched the upstream data/imagery it renders, so the
  // first paint shows broken content (a stray weather glyph). Returning sessions
  // have that data cached. When this panel cold-started the sidecar, do the one
  // reload the operator would otherwise do by hand, a few seconds after it goes
  // Running, by remounting the iframe via its key.
  useEffect(() => {
    if (!running) return;
    if (didColdStartReloadRef.current) return;
    if (wasRunningOnMountRef.current !== false) return; // already running → no reload needed
    didColdStartReloadRef.current = true;
    const id = window.setTimeout(() => setReloadNonce((n) => n + 1), 3000);
    return () => window.clearTimeout(id);
  }, [running]);

  useEffect(() => {
    if (!running || !url) return;

    let expectedOrigin = '';
    try {
      expectedOrigin = new URL(url).origin;
    } catch {
      return;
    }

    const onMessage = (event: MessageEvent<unknown>) => {
      if (event.source !== iframeRef.current?.contentWindow) return;
      if (event.origin !== expectedOrigin) return;

      // External links / Rig-Bridge downloads the iframe forwarded because the
      // Photino webview swallows window.open and has no download handler. Hand
      // them to the OS browser via the existing host bridge (which re-validates
      // http/https C#-side before launching).
      if (isZeusOpenExternalMessage(event.data)) {
        openExternalUrl(event.data.url);
        return;
      }

      if (!isHamClockDxTuneMessage(event.data)) return;
      const msg = event.data;

      void (async () => {
        if (!useSpotsStore.getState().settingsLoaded) {
          await loadSpotSettings();
        }
        await tuneToSpot(toActivationSpot(msg));
      })();
    };

    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [loadSpotSettings, running, tuneToSpot, url]);

  if (running) {
    return (
      <iframe
        key={reloadNonce}
        ref={iframeRef}
        title="HamClock"
        src={url}
        style={{ flex: 1, width: '100%', height: '100%', border: 'none', display: 'block', minHeight: 0 }}
        // HamClock is a trusted local sidecar; allow scripts + same-origin so
        // its app (storage, its own /api fetches) works.
        sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-modals allow-downloads"
      />
    );
  }

  return <HamClockPlaceholder status={status} onStart={() => void start()} />;
}

function HamClockPlaceholder({
  status,
  onStart,
}: {
  status: ReturnType<typeof useHamClockStore.getState>['status'];
  onStart: () => void;
}) {
  let message: string;
  if (status.error) message = status.error;
  else if (status.phase === 'Installing') message = 'Installing HamClock… (downloading + building)';
  else if (status.phase === 'Starting') message = 'Starting HamClock server…';
  else if (!status.installed) message = 'HamClock is not installed yet. Open Settings → HamClock to install it.';
  else message = 'Starting HamClock…';

  return (
    <div
      style={{
        flex: 1,
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 14,
        padding: 24,
        textAlign: 'center',
      }}
    >
      <div style={{ fontSize: 13, fontWeight: 700, letterSpacing: '0.14em', textTransform: 'uppercase', color: 'var(--fg-1)' }}>
        HamClock
      </div>
      <div style={{ fontSize: 12, color: status.error ? 'var(--tx)' : 'var(--fg-2)', maxWidth: 420, lineHeight: 1.5 }}>
        {message}
      </div>
      {status.installed && status.phase !== 'Installing' && status.phase !== 'Starting' && (
        <button type="button" className="btn sm active" disabled={status.busy} onClick={onStart}>
          {status.busy ? 'Starting…' : 'Start HamClock'}
        </button>
      )}
    </div>
  );
}
