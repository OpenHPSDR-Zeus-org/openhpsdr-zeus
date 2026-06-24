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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Per-receiver waterfall noise-floor registry for cross-band normalization.
//
// Each waterfall pane (RX1..RXn) sits on a different band, so each has a
// different absolute noise floor — 40m might floor at -120 dB while 20m floors
// at -135 dB. With a single dB-scale slider that makes one pane look correct
// and the others washed-out or empty.
//
// Fix: every pane reports a smoothed estimate of its OWN noise floor here, and
// at draw time shifts its dB window so its floor lands at the same colour as
// the FOCUSED receiver's floor. The operator drags the slider while looking at
// whatever RX they've focused; that same window then applies, anchored to each
// pane's measured floor, across every band. The focused pane itself is never
// shifted (offset 0), so single-RX behaviour is unchanged.
//
// Indexed by 0-based rxIndex (RX1 = 0, RX2 = 1, RX3+ = 2..), matching
// connection-store `focusedRxIndex` and `receiver-state` `rxIndexOf`.

const floorByRx = new Map<number, number>();

// EMA smoothing for the per-receiver floor — fast enough to settle on a band
// change within a couple of seconds, slow enough that the colours don't jitter
// frame to frame as the instantaneous floor estimate wobbles.
const FLOOR_EMA_ALPHA = 0.1;

// Clamp on the normalization shift so a transient bad estimate (e.g. a pane
// momentarily full of a strong carrier) can never throw the window wildly.
const MAX_OFFSET_DB = 40;

// Reused scratch for the downsampled percentile sort — keeps the per-frame
// estimate allocation-free. 256 samples is plenty for a robust low percentile.
const scratch = new Float32Array(256);

/** Feed a fresh floor estimate (dB) for a receiver; smoothed via EMA. */
export function reportReceiverFloorDb(rxIndex: number, floorDb: number): void {
  if (!Number.isFinite(floorDb)) return;
  const prev = floorByRx.get(rxIndex);
  floorByRx.set(
    rxIndex,
    prev == null ? floorDb : prev + (floorDb - prev) * FLOOR_EMA_ALPHA,
  );
}

/** Smoothed noise floor (dB) for a receiver, or null if none reported yet. */
export function getReceiverFloorDb(rxIndex: number): number | null {
  return floorByRx.get(rxIndex) ?? null;
}

/** Forget all receiver floors (receiver teardown / test reset). */
export function resetReceiverFloors(): void {
  floorByRx.clear();
}

/**
 * dB-window offset to add to THIS pane's [dbMin, dbMax] so its noise floor
 * aligns to the focused receiver's floor. Returns 0 when this IS the focused
 * pane or when either floor is not yet known.
 */
export function floorNormalizationOffsetDb(
  thisRxIndex: number,
  focusedRxIndex: number,
): number {
  if (thisRxIndex === focusedRxIndex) return 0;
  const here = floorByRx.get(thisRxIndex);
  const ref = floorByRx.get(focusedRxIndex);
  if (here == null || ref == null) return 0;
  const off = here - ref;
  return off < -MAX_OFFSET_DB ? -MAX_OFFSET_DB : off > MAX_OFFSET_DB ? MAX_OFFSET_DB : off;
}

/**
 * Robust low-percentile (25th) floor estimate over a dB spectrum row,
 * downsampled to <=256 samples for cheapness. The 25th percentile sits in the
 * lower tail — robust against the deep nulls between carriers while ignoring
 * the signal energy above the floor. Returns null if no finite samples.
 * Allocation-free (reuses a module scratch buffer).
 */
export function estimateRowFloorDb(row: Float32Array): number | null {
  const n = row.length;
  if (n === 0) return null;
  const target = Math.min(scratch.length, n);
  const step = Math.max(1, Math.floor(n / target));
  let count = 0;
  for (let i = 0; i < n && count < scratch.length; i += step) {
    const v = row[i];
    if (v !== undefined && Number.isFinite(v)) scratch[count++] = v;
  }
  if (count === 0) return null;
  const view = scratch.subarray(0, count);
  view.sort();
  return view[Math.floor(count * 0.25)] ?? null;
}
