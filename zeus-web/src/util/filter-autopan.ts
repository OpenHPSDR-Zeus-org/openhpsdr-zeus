// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. See
// ATTRIBUTIONS.md for the full provenance statement.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Filter / dial keep-in-view autopan (RX1).
//
// Under CTUN the hardware LO (display centre) is frozen while the dial roams,
// so tuning toward a screen edge can walk the receive filter — and the dial
// crosshair — off the panadapter/waterfall view. This module watches tuning
// events and, when the dial+filter extent crosses the view edge, glides the
// CTUN centre just enough to bring it back inside a margin ("edge-follow").
//
// Scope / behaviour decisions:
// - All receivers. RX1 plus every active secondary (RX2, RX3+) is checked
//   against its OWN displayed view, so a stitched/multi-RX layout keeps each
//   half in view independently. RX1 pans its hardware NCO; secondaries pan
//   their own DDC centre (panReceiverCenterTo → /api/receivers/{i}/lo, which is
//   a true independent DDC on Protocol 2 and a no-op on P1's shared NCO).
//   Outside CTUN the view already tracks the dial (offset ~0), so the geometry
//   never asks for a pan there — but we still gate on ctunEnabled to keep the
//   intent explicit.
// - Triggered by TUNING only (vfo / filter / mode / cw-pitch / zoom span),
//   never by a view-centre (radioLo) pan — so a deliberate ruler-pan away from
//   the RX to inspect other spectrum is preserved; only re-tuning pulls the
//   filter back into view.
// - Trailing debounce: a continuous CTUN drag rewrites vfo every frame and maps
//   the cursor against the frozen grab-time centre, so recentring mid-drag would
//   fight the gesture. Waiting for tuning to settle (SETTLE_MS) fires the autopan
//   after a drag releases and after each discrete wheel/key/CAT step.

import { useEffect } from 'react';
import { selectDisplaySliceByRxId, useDisplayStore } from '../state/display-store';
import { useConnectionStore } from '../state/connection-store';
import {
  getReceiverFilterHighHz,
  getReceiverFilterLowHz,
  getReceiverMode,
  getReceiverVfoHz,
} from '../state/receiver-state';
import * as viewCenter from '../state/view-center';
import { panReceiverCenterTo } from './ctun-zoom-center';

// Keep this fraction of the span between the dial/filter extent and the view
// edge, so the passband never sits flush against the rail.
const AUTOPAN_MARGIN_FRAC = 0.06;
// Trailing settle window after the last tuning event before autopan fires.
// Long enough that a continuous pointer drag (vfo changes every frame) does not
// recentre mid-gesture; short enough to feel immediate after a wheel/key step.
const SETTLE_MS = 90;

/**
 * Pure geometry: given the current view centre, span, dial, and filter, return
 * the view centre (Hz) that brings the dial+filter extent back inside the
 * margin, or null when it is already comfortably in view. Edge-follow — shifts
 * just enough to seat the offending edge on the margin line, not a full
 * recentre. Exported for unit testing.
 */
export function computeAutopanCenterHz(p: {
  viewCenterHz: number;
  spanHz: number;
  vfoHz: number;
  filterLowHz: number;
  filterHighHz: number;
  mode: string;
  cwPitchHz: number;
  marginHz: number;
}): number | null {
  const { viewCenterHz, spanHz, vfoHz, filterLowHz, filterHighHz, mode, cwPitchHz, marginHz } = p;
  if (!(spanHz > 0) || !Number.isFinite(viewCenterHz) || !Number.isFinite(vfoHz)) return null;

  // Filter edges are stored as audio offsets from the hardware LO, not the
  // dial. In CW the LO sits ±cwPitch from the VFO, so anchor the passband on
  // the LO (matches PassbandOverlay).
  const cwOffset = mode === 'CWU' ? -cwPitchHz : mode === 'CWL' ? cwPitchHz : 0;
  const loHz = vfoHz + cwOffset;
  const filterLoAbs = loHz + filterLowHz;
  const filterHiAbs = loHz + filterHighHz;
  // Keep the dial crosshair (vfoHz) visible too, not only the passband — for
  // SSB the carrier sits just outside the passband edge.
  const extentLo = Math.min(vfoHz, filterLoAbs);
  const extentHi = Math.max(vfoHz, filterHiAbs);

  const halfSpan = spanHz / 2;
  const margin = Math.max(0, Math.min(marginHz, halfSpan));

  // Extent too wide to fit inside the usable window (very narrow span / very
  // wide filter): best effort is to centre the extent.
  if (extentHi - extentLo >= spanHz - 2 * margin) {
    const mid = (extentLo + extentHi) / 2;
    return Math.abs(mid - viewCenterHz) > 0.5 ? mid : null;
  }

  const viewLo = viewCenterHz - halfSpan + margin;
  const viewHi = viewCenterHz + halfSpan - margin;
  let shift = 0;
  if (extentHi > viewHi) shift = extentHi - viewHi;
  else if (extentLo < viewLo) shift = extentLo - viewLo; // negative
  if (shift === 0) return null;
  return viewCenterHz + shift;
}

/**
 * Global hook: keeps each receiver's filter + dial crosshair inside its own
 * panadapter/waterfall view under CTUN by gliding the frozen centre when tuning
 * walks them off the edge. Covers RX1 and every active secondary. Mount once
 * (App / MobileApp).
 */
export function useFilterAutopan(): void {
  useEffect(() => {
    let timer: number | null = null;

    const check = () => {
      timer = null;
      const conn = useConnectionStore.getState();
      // Only CTUN roams the dial off the view centre; otherwise the view
      // already follows the dial and there is nothing to keep in view.
      if (!conn.ctunEnabled) return;
      const ds = useDisplayStore.getState();
      // RX1 (0) plus every secondary that currently has a slice. A slice with
      // no valid geometry (disabled / not yet streaming) is skipped below, so a
      // bare index list is enough — no need for an explicit enabled flag.
      const indices = [0, ...conn.receivers.map((r) => r.index).filter((i) => i >= 1)];
      for (const idx of indices) {
        const s = selectDisplaySliceByRxId(ds, idx);
        if (!(s.width > 0) || !(s.hzPerPixel > 0)) continue;
        const spanHz = s.width * s.hzPerPixel;
        const vc = viewCenter.viewCenterFor(idx);
        // Test against the COMMANDED centre (where the view is heading), not the
        // gliding viewHz — otherwise a normal tune glide briefly reads off-centre.
        const viewCenterHz = vc.isInitialized() ? vc.getTargetCenterHz() : Number(s.centerHz);
        const next = computeAutopanCenterHz({
          viewCenterHz,
          spanHz,
          vfoHz: getReceiverVfoHz(conn, idx),
          filterLowHz: getReceiverFilterLowHz(conn, idx),
          filterHighHz: getReceiverFilterHighHz(conn, idx),
          mode: getReceiverMode(conn, idx),
          cwPitchHz: conn.cwPitchHz,
          marginHz: spanHz * AUTOPAN_MARGIN_FRAC,
        });
        if (next != null) panReceiverCenterTo(idx, next);
      }
    };

    const schedule = () => {
      if (timer !== null) window.clearTimeout(timer);
      timer = window.setTimeout(check, SETTLE_MS);
    };

    // Tuning / geometry only — deliberately NOT radioLoHz / view-centre, so a
    // ruler-pan away from the RX is left where the operator put it. `receivers`
    // reference changes on any secondary vfo/filter/mode edit.
    const unsubConn = useConnectionStore.subscribe((s, prev) => {
      if (
        s.vfoHz !== prev.vfoHz ||
        s.filterLowHz !== prev.filterLowHz ||
        s.filterHighHz !== prev.filterHighHz ||
        s.mode !== prev.mode ||
        s.cwPitchHz !== prev.cwPitchHz ||
        s.ctunEnabled !== prev.ctunEnabled ||
        s.receivers !== prev.receivers
      ) {
        schedule();
      }
    });
    const unsubDisplay = useDisplayStore.subscribe((s, prev) => {
      // Any receiver's span change (RX1 flat fields, or the rx2 / extra slices).
      if (
        s.width !== prev.width ||
        s.hzPerPixel !== prev.hzPerPixel ||
        s.rx2 !== prev.rx2 ||
        s.extra !== prev.extra
      ) {
        schedule();
      }
    });

    return () => {
      if (timer !== null) window.clearTimeout(timer);
      unsubConn();
      unsubDisplay();
    };
  }, []);
}
