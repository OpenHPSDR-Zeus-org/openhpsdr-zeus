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

// Compact zoom slider styled as a panel-head chip. Lives in the hero
// tile header (above the panadapter) so the operator always sees the
// current zoom level alongside HZ/PX.
//
// Send-on-change (no deferred mouseup commit): every step of the drag
// pushes via setZoom, with the previous in-flight request aborted so
// only the final value's echo survives. The optimistic setZoomLevel
// update means the thumb tracks user intent immediately and isn't
// yanked back by the next state broadcast — which is what made the
// old "commit on mouseup" pattern unreliable when the release point
// missed the input element.
export function ZoomControl() {
  const serverZoom = useConnectionStore((s) => s.zoomLevel);
  const setLocalZoom = useConnectionStore((s) => s.setZoomLevel);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  // rAF-coalesced live stream — keeps the panadapter zoom tracking the thumb
  // during a fast drag while capping POSTs to one per paint (zoom triggers a
  // backend panadapter recompute, so flooding is real if onChange ever fires
  // faster than rAF).
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
    <label
      className="mono"
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 6,
        // Blend with the gradient tile header — no chip background / border.
        background: 'transparent',
        border: 'none',
        padding: '0 2px',
        fontSize: 10,
      }}
    >
      <span className="k" style={{ color: 'var(--fg-2)', fontWeight: 600, letterSpacing: '0.06em', textTransform: 'uppercase', fontSize: 9 }}>ZOOM</span>
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
        aria-label="Zoom level"
        style={{
          width: 90,
          cursor: 'pointer',
          accentColor: 'var(--accent)',
          margin: 0,
        }}
      />
      <span
        className="v"
        style={{
          minWidth: 18,
          textAlign: 'right',
          color: 'var(--fg-0)',
          fontWeight: 700,
          fontVariantNumeric: 'tabular-nums',
        }}
      >
        {serverZoom}×
      </span>
    </label>
  );
}
