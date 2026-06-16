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

import { useEffect } from 'react';
import { hamclockIframeUrl, useHamClockStore } from '../../state/hamclock-store';

export function HamClockPanel() {
  const status = useHamClockStore((s) => s.status);
  const loadStatus = useHamClockStore((s) => s.loadStatus);
  const start = useHamClockStore((s) => s.start);

  // Auto-start the sidecar on mount if it's installed but not running, then
  // poll until it reaches Running so the iframe appears. Faster tick while an
  // install/start is in flight.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      await loadStatus();
      if (cancelled) return;
      const s = useHamClockStore.getState().status;
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

  if (running) {
    return (
      <iframe
        title="HamClock"
        src={url}
        style={{ flex: 1, width: '100%', height: '100%', border: 'none', display: 'block', minHeight: 0 }}
        // HamClock is a trusted local sidecar; allow scripts + same-origin so
        // its app (storage, its own /api fetches) works. allow-downloads lets
        // its Rig Bridge / rig-listener installer downloads through — the embed
        // sandbox blocks them otherwise (they work standalone, where there's no
        // sandbox).
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
