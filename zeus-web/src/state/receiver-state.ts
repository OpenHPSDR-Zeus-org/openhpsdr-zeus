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

// N-receiver accessor + action layer (multi-DDC RX3+ unification).
//
// The spectrum surfaces historically keyed on a binary `'A' | 'B'`, mapping to
// the flat RX1 fields and the RX2/VFO-B fields on the connection store. This
// module generalises that to `ReceiverKey = 'A' | 'B' | number`, where a number
// is a 0-based receiver index >= 2 (RX3 = 2, RX4 = 3, … RX8 = 7) backed by the
// `receivers: ReceiverDto[]` array.
//
// KEY INVARIANT: the 'A' and 'B' reads/writes are BYTE-IDENTICAL to the prior
// inline `receiver === 'B' ? <B-field> : <A-field>` expressions — index 0 maps
// to the RX1 flat fields, index 1 to the RX2/VFO-B fields. Numeric indices >= 2
// resolve to the matching `receivers[]` entry (found by `.index`). RX3+ are the
// same class of "secondary receiver" as RX2: their VFO IS the DDC center, with
// no separate radioLo / CTUN-sweep state — those stay RX1-only at the call site.

import { useConnectionStore, type ConnectionState } from './connection-store';
import {
  setFilter,
  setReceiver,
  setVfo,
  setVfoB,
  type ReceiverDto,
  type RxMode,
} from '../api/client';

/** A spectrum receiver key. 'A' = RX1, 'B' = RX2, number = 0-based index >= 2. */
export type ReceiverKey = 'A' | 'B' | number;

/** 0-based receiver index for a key: 'A' → 0, 'B' → 1, number → itself. */
export function rxIndexOf(key: ReceiverKey): number {
  if (key === 'A') return 0;
  if (key === 'B') return 1;
  return key;
}

/** True for any receiver other than RX1 (VFO A) — RX2 and every extra DDC. */
export function isSecondaryReceiver(key: ReceiverKey): boolean {
  return key !== 'A';
}

function receiverEntry(s: ConnectionState, idx: number): ReceiverDto | undefined {
  return s.receivers.find((r) => r.index === idx);
}

// ---------------------------------------------------------------------------
// Pure reads — usable directly as zustand selectors `(s) => getReceiverX(s, key)`.
// 'A'/'B' return the existing flat fields verbatim; numeric indices read the
// matching receivers[] entry, falling back to the RX1 field only as a last
// resort (no entry yet).

export function getReceiverVfoHz(s: ConnectionState, key: ReceiverKey): number {
  if (key === 'A') return s.vfoHz;
  if (key === 'B') return s.vfoBHz;
  return receiverEntry(s, key)?.vfoHz ?? s.vfoHz;
}

export function getReceiverMode(s: ConnectionState, key: ReceiverKey): RxMode {
  if (key === 'A') return s.mode;
  if (key === 'B') return s.modeB;
  return receiverEntry(s, key)?.mode ?? s.mode;
}

export function getReceiverFilterLowHz(s: ConnectionState, key: ReceiverKey): number {
  if (key === 'A') return s.filterLowHz;
  if (key === 'B') return s.filterLowHzB;
  return receiverEntry(s, key)?.filterLowHz ?? s.filterLowHz;
}

export function getReceiverFilterHighHz(s: ConnectionState, key: ReceiverKey): number {
  if (key === 'A') return s.filterHighHz;
  if (key === 'B') return s.filterHighHzB;
  return receiverEntry(s, key)?.filterHighHz ?? s.filterHighHz;
}

export function getReceiverFilterPresetName(
  s: ConnectionState,
  key: ReceiverKey,
): string | null {
  if (key === 'A') return s.filterPresetName;
  if (key === 'B') return s.filterPresetNameB;
  return receiverEntry(s, key)?.filterPresetName ?? s.filterPresetName;
}

// ---------------------------------------------------------------------------
// Optimistic writers — patch the connection store synchronously so the UI tracks
// the gesture before the server round-trip resolves. Numeric indices replace the
// matching receivers[] entry immutably (map over the array, leave the others).

export function optimisticSetReceiverVfo(key: ReceiverKey, hz: number): void {
  if (key === 'A') {
    useConnectionStore.setState({ vfoHz: hz });
    return;
  }
  if (key === 'B') {
    useConnectionStore.setState({ vfoBHz: hz });
    return;
  }
  useConnectionStore.setState((s) => ({
    receivers: s.receivers.map((r) => (r.index === key ? { ...r, vfoHz: hz } : r)),
  }));
}

export function optimisticSetReceiverFilter(
  key: ReceiverKey,
  lo: number,
  hi: number,
): void {
  if (key === 'A') {
    useConnectionStore.setState({ filterLowHz: lo, filterHighHz: hi });
    return;
  }
  if (key === 'B') {
    useConnectionStore.setState({ filterLowHzB: lo, filterHighHzB: hi });
    return;
  }
  useConnectionStore.setState((s) => ({
    receivers: s.receivers.map((r) =>
      r.index === key ? { ...r, filterLowHz: lo, filterHighHz: hi } : r,
    ),
  }));
}

export function optimisticSetReceiverPreset(key: ReceiverKey, slot: string): void {
  if (key === 'A') {
    useConnectionStore.setState({ filterPresetName: slot });
    return;
  }
  if (key === 'B') {
    useConnectionStore.setState({ filterPresetNameB: slot });
    return;
  }
  useConnectionStore.setState((s) => ({
    receivers: s.receivers.map((r) =>
      r.index === key ? { ...r, filterPresetName: slot } : r,
    ),
  }));
}

// ---------------------------------------------------------------------------
// Async posts — route to the correct API endpoint per receiver. 'A'/'B' keep
// the existing per-receiver endpoints (setVfo / setVfoB / setFilter); numeric
// indices use the generic per-DDC endpoint (setReceiver).

export function postReceiverVfo(
  key: ReceiverKey,
  hz: number,
  signal?: AbortSignal,
) {
  if (key === 'A') return setVfo(hz, signal);
  if (key === 'B') return setVfoB(hz, signal);
  return setReceiver(key, { vfoHz: hz }, signal);
}

export function postReceiverFilter(
  key: ReceiverKey,
  lo: number,
  hi: number,
  slot?: string,
  signal?: AbortSignal,
) {
  if (key === 'A') return setFilter(lo, hi, slot, signal, 'A');
  if (key === 'B') return setFilter(lo, hi, slot, signal, 'B');
  return setReceiver(key, { filterLowHz: lo, filterHighHz: hi }, signal);
}

// ---------------------------------------------------------------------------
// Multi-RX exposed-count control. Shared by the Settings RECEIVERS panel and
// the MULTI-RX toolbar toggle so both compose the same per-index endpoint calls
// and honour the same practical ceiling.

// Protocol ceiling is WireContract.MaxReceivers (8 DDCs), but the count that
// actually streams on a G2/Saturn at the wide multi-DDC sample rates
// (768/1536 kHz) is 6 — beyond that the radio's DDC throughput budget is
// exceeded and the extra DDCs come up dead. Cap the operator-facing count here.
export const PRACTICAL_MAX_RECEIVERS = 6;

const DESIRED_COUNT_KEY = 'zeus.multiRx.desiredCount';

function clampDesired(n: number): number {
  return Math.min(Math.max(Math.round(n), 2), PRACTICAL_MAX_RECEIVERS);
}

/** The operator's chosen multi-RX count, remembered across an off toggle and
 *  restarts so the MULTI-RX button can re-enable the same set. Default 2. */
export function getDesiredReceiverCount(): number {
  try {
    const raw =
      typeof localStorage !== 'undefined' ? localStorage.getItem(DESIRED_COUNT_KEY) : null;
    const n = raw ? Number.parseInt(raw, 10) : Number.NaN;
    if (Number.isFinite(n)) return clampDesired(n);
  } catch {
    /* ignore */
  }
  return 2;
}

export function setDesiredReceiverCount(n: number): void {
  try {
    if (typeof localStorage !== 'undefined')
      localStorage.setItem(DESIRED_COUNT_KEY, String(clampDesired(n)));
  } catch {
    /* ignore */
  }
}

/**
 * Expose N contiguous receivers (RX1..RXn). Composes the per-index enable
 * endpoint; the server's contiguity cascade enables RX2..RXn-1 / disables the
 * rest. n>=2 also records the desired count so a later MULTI-RX re-enable
 * restores it. n<=1 collapses back to RX1 only.
 */
export async function setExposedReceiverCount(target: number): Promise<void> {
  const st = useConnectionStore.getState();
  const effectiveMax = Math.min(st.maxReceivers, PRACTICAL_MAX_RECEIVERS);
  const n = Math.min(Math.max(Math.round(target), 1), effectiveMax);
  const applyState = st.applyState;
  if (n >= 2) setDesiredReceiverCount(n);
  try {
    if (n <= 1) {
      applyState(await setReceiver(1, { enabled: false }));
      return;
    }
    applyState(await setReceiver(n - 1, { enabled: true }));
    // Turn off the receiver just above the target so the contiguous run stops
    // at RXn (no-op when n is already the ceiling).
    if (n < effectiveMax) applyState(await setReceiver(n, { enabled: false }));
  } catch {
    /* next state poll reconciles */
  }
}
