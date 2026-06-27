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
// at draw time shifts its dB window so its floor lands at the same colour in
// every band. The common anchor is the MEDIAN of all reported floors — no pane
// is privileged, so adding/removing a band doesn't yank a reference pane around,
// and one outlier band (dead, or stuffed with a contest signal) can't drag the
// anchor the way a mean would. The single master dB slider then sets that shared
// window relative to the median floor; each pane offsets from it by its own
// measured floor, so they all stay evened-out. With one receiver the median IS
// that receiver's floor (offset 0), so single-RX behaviour is unchanged.
//
// Indexed by 0-based rxIndex (RX1 = 0, RX2 = 1, RX3+ = 2..), matching
// connection-store `focusedRxIndex` and `receiver-state` `rxIndexOf`.

const floorByRx = new Map<number, number>();

// The KiwiSDR slice receiver (reserved index — mirrors receiver-state
// KIWI_RECEIVER_INDEX) is a FOREIGN remote receiver: a different site, antenna
// and ADC calibration than the local hardware DDCs, so its absolute dB scale is
// unrelated to theirs. It must normalise TO the hardware noise-floor anchor, but
// must NOT help DEFINE it — co-anchoring drags the hardware panes toward the
// Kiwi's (often very different) floor and, with just RX1 + Kiwi, leaves BOTH only
// half-aligned, so the Kiwi pane washes out bright instead of matching RX1's dark
// floor (operator report). Held as a local literal to avoid a dsp→state import
// cycle.
const KIWI_RX_INDEX = 7;

// EMA smoothing for the per-receiver floor — fast enough to settle on a band
// change within a couple of seconds, slow enough that the colours don't jitter
// frame to frame as the instantaneous floor estimate wobbles.
const FLOOR_EMA_ALPHA = 0.1;

// Clamp on the normalization shift so a transient bad estimate (e.g. a pane
// momentarily full of a strong carrier) can never throw the window wildly.
// Real inter-band noise-floor spread is large — a busy 40m evening floors tens
// of dB above a dead 10m/15m band — so this has to be wide enough to fully
// align those extremes, while the robust 25th-percentile estimate (below) keeps
// a carrier-stuffed pane from abusing the headroom.
const MAX_OFFSET_DB = 80;

// Reused scratch for the downsampled percentile sort — keeps the per-frame
// estimate allocation-free. 256 samples is plenty for a robust low percentile.
const scratch = new Float32Array(256);

// Reused scratch for the median over reported floors. Sized well past any
// realistic DDC count; floors beyond it are ignored (the median of the first
// N is still representative). Keeps referenceFloorDb() allocation-free on the
// per-frame redraw path.
const refScratch = new Float64Array(32);

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

/** Forget one receiver's floor (pane removed) so it leaves the median anchor. */
export function forgetReceiverFloor(rxIndex: number): void {
  floorByRx.delete(rxIndex);
}

/** Forget all receiver floors (receiver teardown / test reset). */
export function resetReceiverFloors(): void {
  floorByRx.clear();
}

/**
 * Median of the reported HARDWARE receiver floors (dB) — the common
 * normalization anchor and the "0 dB" reference the master scale reads against.
 * Median, not mean, so one dead or carrier-stuffed band can't drag the anchor.
 * The foreign Kiwi slice ({@link KIWI_RX_INDEX}) is excluded so it aligns to the
 * hardware anchor without redefining it. Returns null until at least one
 * hardware floor has been reported. Allocation-free.
 */
export function referenceFloorDb(): number | null {
  let n = 0;
  for (const [idx, v] of floorByRx) {
    if (idx === KIWI_RX_INDEX) continue;
    if (n >= refScratch.length) break;
    refScratch[n++] = v;
  }
  if (n === 0) return null;
  const view = refScratch.subarray(0, n);
  view.sort();
  const mid = n >> 1;
  return n % 2 ? view[mid]! : (view[mid - 1]! + view[mid]!) / 2;
}

/**
 * dB-window offset to add to THIS pane's [dbMin, dbMax] so its noise floor
 * aligns to the shared median floor. Returns 0 when this pane's floor or the
 * reference is not yet known, so an un-reported pane simply shows the raw
 * window. Clamped to +/-MAX_OFFSET_DB against a transient bad estimate.
 */
export function floorNormalizationOffsetDb(thisRxIndex: number): number {
  const here = floorByRx.get(thisRxIndex);
  const ref = referenceFloorDb();
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
