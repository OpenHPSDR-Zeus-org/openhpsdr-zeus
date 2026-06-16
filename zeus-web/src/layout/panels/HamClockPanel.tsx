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
import { hamclockIframeUrl, useHamClockStore } from '../../state/hamclock-store';

// Fixed logical render size for the embedded HamClock dashboard. The iframe
// is always rendered at this resolution and the whole view is uniformly
// scaled (transform: scale) to fit the tile, so the dashboard keeps the same
// visual scale/aspect when the operator grows or shrinks the panel instead of
// reflowing into HamClock's mobile/tablet breakpoints. 1280×800 sits above
// HamClock's largest responsive breakpoint (1024px) so the full desktop
// multi-panel layout always renders; 16:10 matches the tall default tile span
// (DEFAULT_TILE_SPAN.hamclock = 12×24).
const HAMCLOCK_BASE_W = 1280;
const HAMCLOCK_BASE_H = 800;
// Floor so the dashboard never scales to nothing on a tiny tile; below this
// the wrapper's overflow:hidden simply crops.
const HAMCLOCK_MIN_SCALE = 0.2;

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
    return <ScaledHamClockFrame url={url} />;
  }

  return <HamClockPlaceholder status={status} onStart={() => void start()} />;
}

// Renders the HamClock iframe at a fixed logical base size and uniformly
// scales it (preserving aspect — letterboxed, never distorted) to fit the
// tile via a ResizeObserver on the wrapping host. This is what keeps the
// dashboard at a constant visual scale as the operator resizes the panel.
//
// The wrapper is also the seam for the resize-handle fix: an <iframe> is its
// own hit-test root and swallows the pointerdown that would start an RGL
// corner-resize. A transparent same-document grip sits over the tile's SE
// corner so the operator can always grab the resize affordance; once a
// drag/resize gesture begins, all-panels.css neutralises the iframe's
// pointer-events (`.resizing`/`.react-draggable-dragging iframe`) so the
// gesture keeps tracking over the iframe surface.
function ScaledHamClockFrame({ url }: { url: string }) {
  const hostRef = useRef<HTMLDivElement>(null);
  const [scale, setScale] = useState(1);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const ro = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      const { width, height } = entry.contentRect;
      if (width <= 0 || height <= 0) return;
      // Uniform scale: the smaller axis ratio so the whole dashboard fits
      // without distortion (the other axis letterboxes).
      const next = Math.min(width / HAMCLOCK_BASE_W, height / HAMCLOCK_BASE_H);
      setScale(Math.max(HAMCLOCK_MIN_SCALE, next));
    });
    ro.observe(host);
    return () => ro.disconnect();
  }, []);

  return (
    <div
      ref={hostRef}
      style={{
        flex: 1,
        width: '100%',
        height: '100%',
        minHeight: 0,
        position: 'relative',
        overflow: 'hidden', // clip the un-shrunk layout box + letterbox bars
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'var(--bg-1)', // letterbox bars read as panel base
      }}
    >
      <iframe
        title="HamClock"
        src={url}
        style={{
          width: HAMCLOCK_BASE_W,
          height: HAMCLOCK_BASE_H,
          flex: 'none', // don't let the flex host stretch the base size
          border: 'none',
          display: 'block',
          transform: `scale(${scale})`,
          transformOrigin: 'center center', // matches the flex centering
        }}
        // HamClock is a trusted local sidecar; allow scripts + same-origin so
        // its app (storage, its own /api fetches) works. allow-downloads lets
        // its Rig Bridge / rig-listener installer downloads through — the embed
        // sandbox blocks them otherwise (they work standalone, where there's no
        // sandbox).
        sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-modals allow-downloads"
        // Delegate Geolocation into the embed so HamClock's "Use my current
        // location" works (Permissions-Policy is deny-by-default for iframes).
        allow="geolocation"
      />
      {/* Same-document grip over the tile's SE corner. The iframe would
          otherwise eat the pointerdown that starts an RGL corner-resize; this
          transparent layer lives in the host document and aligns with the
          RGL resize handle (right/bottom 2px, 20×20 in all-panels.css) so the
          press lands on the parent and the resize starts. pointer-events on
          the iframe are dropped for the rest of the gesture via CSS. */}
      <div className="hamclock-resize-catch" aria-hidden />
    </div>
  );
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
