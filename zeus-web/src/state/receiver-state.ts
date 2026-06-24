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
//   - index >= 1     → the matching `receivers[]` entry, found by `.index`
//                      (falling back to the RX1 primary field only when the
//                      array entry is absent, e.g. disconnected/empty store)
//
// The `receivers[]` array is the SOLE client-side source of truth for RX2+ —
// the flat *B fields (vfoBHz/modeB/filter*B/rx2AfGainDb) have been removed from
// the client. RX2 changes still POST through the legacy A/B write endpoints
// (setVfoB / setMode?receiver=B / …); the server projects the result back into
// `receivers[1]`, which is what these selectors read.
//
// RX2 and RX3+ are the same class of "secondary receiver" (the VFO is the DDC
// center); only RX1 (index 0) gets radioLo pan, CTUN sweep, snap history /
// snap-lock, and TX auto-range. Those remain gated on index 0 at the call
// sites — this module just routes field reads/writes.

import { useConnectionStore } from './connection-store';
import type { RadioStateDto, ReceiverDto, RxMode } from '../api/client';
import {
  setFilter,
  setMode,
  setReceiver,
  setRx2,
  setRxAfGain,
  setVfo,
  setVfoB,
} from '../api/client';

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

/** Immutably patch the `receivers[]` entry with the given index, leaving the
 *  rest of the array untouched. Used by the optimistic writers so RX2 (index 1)
 *  and RX3+ live in the canonical array even before the server round-trips. */
function patchReceiverEntry(
  receivers: ReceiverDto[],
  idx: number,
  patch: Partial<ReceiverDto>,
): ReceiverDto[] {
  return receivers.map((r) => (r.index === idx ? { ...r, ...patch } : r));
}

// ---------------------------------------------------------------------------
// Pure selectors over connection-store state. Usable directly as zustand
// selectors (`useConnectionStore((s) => getReceiverVfoHz(s, key))`). For
// index 0/1 they return today's exact fields verbatim; for >= 2 they read the
// matching `receivers[]` entry (falling back to the RX1 field if missing).

export function getReceiverVfoHz(state: ConnState, key: ReceiverKey): number {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.vfoHz;
  return receiverEntry(state, idx)?.vfoHz ?? state.vfoHz;
}

export function getReceiverMode(state: ConnState, key: ReceiverKey) {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.mode;
  return receiverEntry(state, idx)?.mode ?? state.mode;
}

export function getReceiverFilterLowHz(state: ConnState, key: ReceiverKey): number {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.filterLowHz;
  return receiverEntry(state, idx)?.filterLowHz ?? state.filterLowHz;
}

export function getReceiverFilterHighHz(state: ConnState, key: ReceiverKey): number {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.filterHighHz;
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
  return state.receivers?.find((r) => r.index === idx)?.vfoHz ?? state.vfoHz;
}

export function getReceiverAfGainDb(state: ConnState, key: ReceiverKey): number {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.rxAfGainDb;
  return receiverEntry(state, idx)?.afGainDb ?? state.rxAfGainDb;
}

export function getReceiverFilterPresetName(
  state: ConnState,
  key: ReceiverKey,
): string | null {
  const idx = rxIndexOf(key);
  if (idx === 0) return state.filterPresetName;
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
  } else {
    // index >= 1 (RX2 / RX3+) lives in the canonical receivers[] array.
    useConnectionStore.setState((s) => ({
      receivers: patchReceiverEntry(s.receivers, idx, { vfoHz: hz }),
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
  } else {
    useConnectionStore.setState((s) => ({
      receivers: patchReceiverEntry(s.receivers, idx, { filterLowHz: lo, filterHighHz: hi }),
    }));
  }
}

export function optimisticSetReceiverMode(key: ReceiverKey, mode: RxMode): void {
  const idx = rxIndexOf(key);
  if (idx === 0) {
    useConnectionStore.setState({ mode });
  } else {
    useConnectionStore.setState((s) => ({
      receivers: patchReceiverEntry(s.receivers, idx, { mode }),
    }));
  }
}

export function optimisticSetReceiverAfGain(key: ReceiverKey, db: number): void {
  const idx = rxIndexOf(key);
  if (idx === 0) {
    useConnectionStore.setState({ rxAfGainDb: db });
  } else {
    useConnectionStore.setState((s) => ({
      receivers: patchReceiverEntry(s.receivers, idx, { afGainDb: db }),
    }));
  }
}

export function optimisticSetReceiverPreset(key: ReceiverKey, slot: string): void {
  const idx = rxIndexOf(key);
  if (idx === 0) {
    useConnectionStore.setState({ filterPresetName: slot });
  } else {
    useConnectionStore.setState((s) => ({
      receivers: patchReceiverEntry(s.receivers, idx, { filterPresetName: slot }),
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
  // Carry the preset label so RX3+ rounds-trips the slot name (e.g. VAR1),
  // exactly as RX1/RX2 do via setFilter's presetName argument.
  return setReceiver(idx, { filterLowHz: lo, filterHighHz: hi, filterPresetName: slot }, signal);
}

/** Post an AF-gain (dB) change to any receiver. RX1 → master AF, RX2 → the
 *  rx2 setter, RX3+ → their own DDC channel. Mirrors the VFO/filter routing. */
export function postReceiverAfGain(
  key: ReceiverKey,
  db: number,
  signal?: AbortSignal,
) {
  const idx = rxIndexOf(key);
  if (idx === 0) return setRxAfGain(db, signal);
  if (idx === 1) return setRx2({ afGainDb: db }, signal);
  return setReceiver(idx, { afGainDb: db }, signal);
}

/** Post a mode change to any receiver. RX1/RX2 funnel through the legacy
 *  /api/mode A/B path the server special-cases; RX3+ drive their own DDC
 *  channel via setReceiver. Mirrors postReceiverFilter's routing. */
export function postReceiverMode(
  key: ReceiverKey,
  mode: RxMode,
  signal?: AbortSignal,
) {
  const idx = rxIndexOf(key);
  if (idx === 0) return setMode(mode, signal, 'A');
  if (idx === 1) return setMode(mode, signal, 'B');
  return setReceiver(idx, { mode }, signal);
}

// ---------------------------------------------------------------------------
// Ganged multi-select. A toolbar control (mode/filter/band/AF) acts on EVERY
// selected receiver at once; the focused receiver is the primary whose
// response reconciles the store. Direct per-pane manipulation (dragging a
// passband on one receiver's panadapter) is NOT ganged — it stays on that pane.

/** The receivers a ganged toolbar action targets (the live selection). */
export function selectedReceiverKeys(): number[] {
  return useConnectionStore.getState().selectedRxIndices;
}

/** Run a control action across every selected receiver: an optimistic store
 *  update plus a REST post for each, reconciling the store from the focused
 *  receiver's response (the others' responses are ignored to avoid clobbering
 *  the focused view; the next state poll reconciles them). */
export function gangedReceiverAction(opts: {
  optimistic?: (key: number) => void;
  post: (key: number) => Promise<RadioStateDto>;
}): void {
  const { selectedRxIndices, focusedRxIndex, applyState } = useConnectionStore.getState();
  for (const idx of selectedRxIndices) opts.optimistic?.(idx);
  for (const idx of selectedRxIndices) {
    opts
      .post(idx)
      .then((res) => {
        if (idx === focusedRxIndex) applyState(res);
      })
      .catch(() => {
        /* next state poll reconciles */
      });
  }
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
      // Collapse to RX1 only. Disabling RX2 (index 1) routes server-side to
      // SetRx2, which does NOT cascade the RX3+ extras off — so clear the extra
      // run first (index 2's disable cascades RX3.. off), then RX2. Without this
      // the extras linger as a contiguity gap (RX2 off but RX3+ still streaming).
      await setReceiver(2, { enabled: false }).catch(() => {});
      applyState(await setReceiver(1, { enabled: false }));
      return;
    }
    applyState(await setReceiver(n - 1, { enabled: true }));
    if (n < effectiveMax) applyState(await setReceiver(n, { enabled: false }));
  } catch {
    /* next state poll reconciles */
  }
}
