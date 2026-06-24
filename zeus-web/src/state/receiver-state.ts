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

// Per-receiver state accessors for the unified multi-DDC render path.
//
// The whole spectrum stack (panadapter, waterfall, overlays, tune gestures)
// used to key on a binary receiver discriminator `'A' | 'B'`. With RX3+ those
// two literals can't address an arbitrary DDC, so everything is generalised to
// a `ReceiverKey = 'A' | 'B' | number`. NUMBERS are the canonical form
// (0 = RX1, 1 = RX2, >= 2 = RX3+); `'A'`/`'B'` are retained ONLY as legacy
// aliases (0 ≡ 'A', 1 ≡ 'B') so existing internal call sites and tests that
// pass them keep compiling. The multi-DDC render path and every user-facing
// label use pure numbers.
//
// Field routing by receiver INDEX:
//   - index 0 (RX1)  → the connection-store primary fields (vfoHz, mode, …)
//   - index 1 (RX2)  → the *B fields (vfoBHz, modeB, …)
//   - index >= 2     → the matching `receivers[]` entry, found by `.index`
//
// RX2 and RX3+ are the same class of "secondary receiver" (the VFO is the DDC
// center); only RX1 (index 0) gets radioLo pan, CTUN sweep, snap history /
// snap-lock, and TX auto-range. Those remain gated on index 0 at the call
// sites — this module just routes field reads/writes.

import { useConnectionStore } from './connection-store';
import type { RadioStateDto, ReceiverDto } from '../api/client';
import { setFilter, setReceiver, setVfo, setVfoB } from '../api/client';

/** Receiver discriminator. Numbers are canonical (0 = RX1, 1 = RX2, >= 2 =
 *  RX3+); `'A'`/`'B'` are legacy aliases for 0/1. */
export type ReceiverKey = 'A' | 'B' | number;

/** 0-based receiver index for a key. A → 0, B → 1, number → itself. */
export function rxIndexOf(key: ReceiverKey): number {
  if (key === 'A') return 0;
  if (key === 'B') return 1;
  return key;
}

/** Every receiver except RX1 (index 0) is a "secondary" receiver whose VFO is
 *  the DDC center. */
export function isSecondaryReceiver(key: ReceiverKey): boolean {
  return rxIndexOf(key) !== 0;
}

type ConnState = ReturnType<typeof useConnectionStore.getState>;

function receiverEntry(state: ConnState, idx: number): ReceiverDto | undefined {
  return state.receivers.find((r) => r.index === idx);
}

// ---------------------------------------------------------------------------
// Pure selectors over connection-store state. Usable directly as zustand
// selectors (`useConnectionStore((s) => getReceiverVfoHz(s, key))`). For
// index 0/1 they return today's exact fields verbatim; for >= 2 they read the
// matching `receivers[]` entry (falling back to the RX1 field if missing).

export function getReceiverVfoHz(state: ConnState, key: ReceiverKey): number {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.vfoHz;
  if (idx === 1) return state.vfoBHz;
  return receiverEntry(state, idx)?.vfoHz ?? state.vfoHz;
}

export function getReceiverMode(state: ConnState, key: ReceiverKey) {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.mode;
  if (idx === 1) return state.modeB;
  return receiverEntry(state, idx)?.mode ?? state.mode;
}

export function getReceiverFilterLowHz(state: ConnState, key: ReceiverKey): number {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.filterLowHz;
  if (idx === 1) return state.filterLowHzB;
  return receiverEntry(state, idx)?.filterLowHz ?? state.filterLowHz;
}

export function getReceiverFilterHighHz(state: ConnState, key: ReceiverKey): number {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.filterHighHz;
  if (idx === 1) return state.filterHighHzB;
  return receiverEntry(state, idx)?.filterHighHz ?? state.filterHighHz;
}

/** Read a receiver's VFO out of a server RadioStateDto (e.g. to reconcile the
 *  applied pan center after a POST). 0 → vfoHz, 1 → vfoBHz, >= 2 → the matching
 *  receivers[] entry (falling back to vfoHz). */
export function getReceiverVfoFromState(
  state: RadioStateDto,
  key: ReceiverKey,
): number {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.vfoHz;
  if (idx === 1) return state.vfoBHz;
  return state.receivers?.find((r) => r.index === idx)?.vfoHz ?? state.vfoHz;
}

export function getReceiverFilterPresetName(
  state: ConnState,
  key: ReceiverKey,
): string | null {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.filterPresetName;
  if (idx === 1) return state.filterPresetNameB;
  return receiverEntry(state, idx)?.filterPresetName ?? state.filterPresetName;
}

// ---------------------------------------------------------------------------
// Optimistic writers. Mutate connection-store immediately so the UI reacts
// before the server round-trips. index 0 → primary fields, 1 → *B fields,
// >= 2 → an immutable update of the matching receivers[] entry.

export function optimisticSetReceiverVfo(key: ReceiverKey, hz: number): void {
  const idx = rxIndexOf(key);
  if (idx === 0) {
    useConnectionStore.setState({ vfoHz: hz });
  } else if (idx === 1) {
    useConnectionStore.setState({ vfoBHz: hz });
  } else {
    useConnectionStore.setState((s) => ({
      receivers: s.receivers.map((r) => (r.index === idx ? { ...r, vfoHz: hz } : r)),
    }));
  }
}

export function optimisticSetReceiverFilter(
  key: ReceiverKey,
  lo: number,
  hi: number,
): void {
  const idx = rxIndexOf(key);
  if (idx === 0) {
    useConnectionStore.setState({ filterLowHz: lo, filterHighHz: hi });
  } else if (idx === 1) {
    useConnectionStore.setState({ filterLowHzB: lo, filterHighHzB: hi });
  } else {
    useConnectionStore.setState((s) => ({
      receivers: s.receivers.map((r) =>
        r.index === idx ? { ...r, filterLowHz: lo, filterHighHz: hi } : r,
      ),
    }));
  }
}

export function optimisticSetReceiverPreset(key: ReceiverKey, slot: string): void {
  const idx = rxIndexOf(key);
  if (idx === 0) {
    useConnectionStore.setState({ filterPresetName: slot });
  } else if (idx === 1) {
    useConnectionStore.setState({ filterPresetNameB: slot });
  } else {
    useConnectionStore.setState((s) => ({
      receivers: s.receivers.map((r) =>
        r.index === idx ? { ...r, filterPresetName: slot } : r,
      ),
    }));
  }
}

// ---------------------------------------------------------------------------
// Async posts to the backend. index 0/1 route through the existing RX1/RX2
// setters (which the server special-cases); index >= 2 drives the matching
// hardware DDC via setReceiver.

export function postReceiverVfo(
  key: ReceiverKey,
  hz: number,
  signal?: AbortSignal,
) {
  const idx = rxIndexOf(key);
  if (idx === 0) return setVfo(hz, signal);
  if (idx === 1) return setVfoB(hz, signal);
  return setReceiver(idx, { vfoHz: hz }, signal);
}

export function postReceiverFilter(
  key: ReceiverKey,
  lo: number,
  hi: number,
  slot?: string,
  signal?: AbortSignal,
) {
  const idx = rxIndexOf(key);
  if (idx <= 1) {
    return setFilter(lo, hi, slot, signal, idx === 1 ? 'B' : 'A');
  }
  return setReceiver(idx, { filterLowHz: lo, filterHighHz: hi }, signal);
}
