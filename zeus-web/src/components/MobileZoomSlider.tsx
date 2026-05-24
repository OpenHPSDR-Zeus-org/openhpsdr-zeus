// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useCallback } from 'react';
import { setZoom, ZOOM_MAX, ZOOM_MIN, type ZoomLevel } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useLiveSlider } from '../hooks/useLiveSlider';

/**
 * Vertical zoom slider pinned to the right edge of the panadapter on mobile.
 * Allows zooming without finger-dragging on the spectrum (which tunes frequency).
 * Hidden on desktop via CSS.
 *
 * Send-on-change (mirrors ZoomControl): every step of the drag pushes via
 * setZoom with the previous in-flight POST aborted. Optimistic setLocalZoom
 * means the thumb stays where the operator put it, even if a touchend lands
 * outside the input element.
 */
export function MobileZoomSlider() {
  const serverZoom = useConnectionStore((s) => s.zoomLevel);
  const setLocalZoom = useConnectionStore((s) => s.setZoomLevel);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  // rAF-coalesced live stream — see ZoomControl for rationale.
  const liveSlider = useLiveSlider<ZoomLevel>({
    send: useCallback(
      (v: ZoomLevel, signal: AbortSignal) =>
        setZoom(v, signal)
          .then((next) => {
            if (!signal.aborted) applyState(next);
          })
          .catch(() => {
            /* next state poll will reconcile */
          }),
      [applyState],
    ),
  });

  const onSlide = useCallback(
    (v: ZoomLevel) => {
      setLocalZoom(v);
      liveSlider.push(v);
    },
    [liveSlider, setLocalZoom],
  );

  return (
    <div
      className="mobile-zoom-slider"
      style={{
        position: 'absolute',
        right: 0,
        top: 0,
        bottom: 0,
        width: 32,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 10,
        pointerEvents: connected ? 'auto' : 'none',
        background: 'linear-gradient(90deg, transparent, rgba(255, 160, 40, 0.08))',
        borderLeft: '1px solid rgba(255, 160, 40, 0.15)',
      }}
    >
      <input
        type="range"
        min={ZOOM_MIN}
        max={ZOOM_MAX}
        step={1}
        value={serverZoom}
        disabled={!connected}
        onChange={(e) => onSlide(Number(e.currentTarget.value) as ZoomLevel)}
        onMouseUp={() => liveSlider.flush()}
        onTouchEnd={() => liveSlider.flush()}
        onKeyUp={() => liveSlider.flush()}
        style={{
          writingMode: 'vertical-lr',
          direction: 'rtl',
          height: '80%',
          cursor: 'pointer',
          accentColor: 'var(--accent)',
          opacity: connected ? 1 : 0.3,
        }}
        aria-label="Zoom level"
      />
      <span
        style={{
          position: 'absolute',
          bottom: 8,
          left: '50%',
          transform: 'translateX(-50%)',
          fontSize: 9,
          fontWeight: 700,
          color: 'var(--accent)',
          pointerEvents: 'none',
          textShadow: '0 0 4px rgba(255, 160, 40, 0.6)',
        }}
      >
        {serverZoom}×
      </span>
    </div>
  );
}
