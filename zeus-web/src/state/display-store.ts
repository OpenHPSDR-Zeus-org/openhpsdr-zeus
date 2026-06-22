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

import { create } from 'zustand';
import type { DecodedFrame } from '../realtime/frame';
import { maybeUpdateEstimator } from '../dsp/signal-estimator';

const DISPLAY_INVALID_BIN_DB = -200;

export type SpectrumReceiver = 'A' | 'B';

export type ReceiverDisplaySlice = {
  width: number;
  centerHz: bigint;
  hzPerPixel: number;
  panDb: Float32Array | null;
  wfDb: Float32Array | null;
  panValid: boolean;
  wfValid: boolean;
  panFloorDb: number | null;
  wfFloorDb: number | null;
  lastSeq: number;
};

export type DisplayState = {
  connected: boolean;
  width: number;
  centerHz: bigint;
  hzPerPixel: number;
  panDb: Float32Array | null;
  wfDb: Float32Array | null;
  panValid: boolean;
  wfValid: boolean;
  panFloorDb: number | null;
  wfFloorDb: number | null;
  lastSeq: number;
  rx2: ReceiverDisplaySlice;
  // Display slices for RX3+ (multi-DDC). Indexed by rxId - 2, so extra[0] is
  // RX3 (rxId 2), extra[1] is RX4, etc. RX1 lives in the root fields and RX2 in
  // `rx2`; keeping those untouched preserves the VFO-A/B ('A'/'B') consumers
  // while RX3+ frames land here instead of clobbering the primary slice.
  extra: ReceiverDisplaySlice[];
  setConnected: (c: boolean) => void;
  pushFrame: (f: DecodedFrame) => void;
};

export function createEmptyDisplaySlice(): ReceiverDisplaySlice {
  return {
    width: 0,
    centerHz: 0n,
    hzPerPixel: 0,
    panDb: null,
    wfDb: null,
    panValid: false,
    wfValid: false,
    panFloorDb: null,
    wfFloorDb: null,
    lastSeq: 0,
  };
}

export function sanitizeDisplayBins(bins: Float32Array): Float32Array {
  let firstBad = -1;
  for (let i = 0; i < bins.length; i++) {
    if (!Number.isFinite(bins[i])) {
      firstBad = i;
      break;
    }
  }
  if (firstBad < 0) return bins;

  const sanitized = new Float32Array(bins);
  for (let i = firstBad; i < sanitized.length; i++) {
    if (!Number.isFinite(sanitized[i])) sanitized[i] = DISPLAY_INVALID_BIN_DB;
  }
  return sanitized;
}

function validFrameGeometry(width: number, hzPerPixel: number): boolean {
  return (
    Number.isInteger(width) &&
    width > 0 &&
    Number.isFinite(hzPerPixel) &&
    hzPerPixel > 0
  );
}

function validPayload(
  enabled: boolean,
  bins: Float32Array,
  width: number,
  geometryValid: boolean,
): boolean {
  return enabled && geometryValid && bins.length === width;
}

function estimateDisplayFloorDb(bins: Float32Array | null): number | null {
  if (!bins || bins.length === 0) return null;
  const sample: number[] = [];
  const stride = Math.max(1, Math.floor(bins.length / 512));
  for (let i = 0; i < bins.length; i += stride) {
    const v = bins[i];
    if (v !== undefined && Number.isFinite(v) && v > DISPLAY_INVALID_BIN_DB + 1) {
      sample.push(v);
    }
  }
  if (sample.length < 8) return null;
  sample.sort((a, b) => a - b);
  return sample[Math.floor(sample.length * 0.22)] ?? null;
}

function cleanFrame(f: DecodedFrame): DecodedFrame {
  const geometryValid = validFrameGeometry(f.width, f.hzPerPixel);
  const width = geometryValid ? f.width : 0;
  const hzPerPixel = geometryValid ? f.hzPerPixel : 0;
  const panValid = validPayload(f.panValid, f.panDb, width, geometryValid);
  const wfValid = validPayload(f.wfValid, f.wfDb, width, geometryValid);
  const panDb = panValid ? sanitizeDisplayBins(f.panDb) : f.panDb;
  const wfDb = wfValid ? sanitizeDisplayBins(f.wfDb) : f.wfDb;
  return panDb === f.panDb &&
    wfDb === f.wfDb &&
    panValid === f.panValid &&
    wfValid === f.wfValid &&
    width === f.width &&
    hzPerPixel === f.hzPerPixel
    ? f
    : { ...f, width, hzPerPixel, panValid, wfValid, panDb, wfDb };
}

function sliceFromFrame(f: DecodedFrame): ReceiverDisplaySlice {
  const panDb = f.panValid ? f.panDb : null;
  const wfDb = f.wfValid ? f.wfDb : null;
  return {
    width: f.width,
    centerHz: f.centerHz,
    hzPerPixel: f.hzPerPixel,
    panDb,
    wfDb,
    panValid: f.panValid,
    wfValid: f.wfValid,
    panFloorDb: estimateDisplayFloorDb(panDb),
    wfFloorDb: estimateDisplayFloorDb(wfDb),
    lastSeq: f.seq,
  };
}

export function receiverFromRxId(rxId: number): SpectrumReceiver {
  return rxId === 1 ? 'B' : 'A';
}

export function selectDisplaySlice(
  state: DisplayState,
  receiver: SpectrumReceiver = 'A',
): ReceiverDisplaySlice {
  if (receiver === 'B') return state.rx2;
  return {
    width: state.width,
    centerHz: state.centerHz,
    hzPerPixel: state.hzPerPixel,
    panDb: state.panDb,
    wfDb: state.wfDb,
    panValid: state.panValid,
    wfValid: state.wfValid,
    panFloorDb: state.panFloorDb,
    wfFloorDb: state.wfFloorDb,
    lastSeq: state.lastSeq,
  };
}

// Shared empty slice for not-yet-seen RX3+ receivers. Slices are treated as
// read-only snapshots, so a single frozen instance avoids per-render allocation
// and keeps reference identity stable until the first frame arrives.
const EMPTY_DISPLAY_SLICE: ReceiverDisplaySlice = createEmptyDisplaySlice();

// Select a display slice by raw rxId (0 = RX1 / VFO A, 1 = RX2 / VFO B, >=2 =
// RX3+ from the multi-DDC `extra` array). This is the N-receiver accessor used
// by RX3+ panadapter/waterfall surfaces; the binary 'A'/'B' selectDisplaySlice
// above still serves the VFO-A/B UI unchanged.
export function selectDisplaySliceByRxId(
  state: DisplayState,
  rxId: number,
): ReceiverDisplaySlice {
  if (rxId <= 0) return selectDisplaySlice(state, 'A');
  if (rxId === 1) return state.rx2;
  return state.extra[rxId - 2] ?? EMPTY_DISPLAY_SLICE;
}

export const useDisplayStore = create<DisplayState>((set) => ({
  connected: false,
  width: 0,
  centerHz: 0n,
  hzPerPixel: 0,
  panDb: null,
  wfDb: null,
  panValid: false,
  wfValid: false,
  panFloorDb: null,
  wfFloorDb: null,
  lastSeq: 0,
  rx2: createEmptyDisplaySlice(),
  extra: [],
  setConnected: (connected) => set({ connected }),
  pushFrame: (f) => {
    const clean = cleanFrame(f);
    const nextSlice = sliceFromFrame(clean);
    const rxId = clean.rxId;

    if (rxId === 1) {
      set({ rx2: nextSlice });
      return;
    }

    // RX3+ (multi-DDC): land in the `extra` array keyed by rxId - 2 instead of
    // falling through to the primary slice. Before this, receiverFromRxId mapped
    // any rxId != 1 to 'A', so RX3 frames overwrote RX1's display.
    if (rxId >= 2) {
      set((state) => {
        const extra = state.extra.slice();
        extra[rxId - 2] = nextSlice;
        return { extra };
      });
      return;
    }

    // Advance the shared noise-floor tracker BEFORE notifying subscribers, so
    // the panadapter/waterfall enhance this frame against this frame's floor.
    // No-op (zero cost) unless Signal Pop or Snap is enabled.
    maybeUpdateEstimator(clean);
    set({
      ...nextSlice,
    });
  },
}));

export function subscribeFrames(cb: (s: DisplayState) => void): () => void {
  return useDisplayStore.subscribe(cb);
}

// Active-consumer registry. The realtime client (ws-client.ts) consults this
// before invoking decodeDisplayFrame + pushFrame on every spectrum tick. When
// every spectrum surface (panadapter, waterfall, filter mini-pan) is closed,
// decoding still happens for a store with no subscribers; the per-frame cost
// is small but it scales with backend tick rate (~25 Hz) and allocates two
// Float32Arrays per call. Components register on mount and unregister on
// unmount; whilever count > 0 we keep decoding, otherwise we short-circuit.
//
// Deliberately a module-level counter (not in the store) so toggling consumer
// presence doesn't itself fan out as a store update through React.
let frameConsumerCount = 0;
type FrameConsumerPresenceListener = (active: boolean, count: number) => void;
const frameConsumerPresenceListeners = new Set<FrameConsumerPresenceListener>();

function notifyFrameConsumerPresenceIfChanged(previousCount: number): void {
  const wasActive = previousCount > 0;
  const active = frameConsumerCount > 0;
  if (wasActive === active) return;
  for (const listener of frameConsumerPresenceListeners) listener(active, frameConsumerCount);
}

/**
 * Mark this caller as a live consumer of decoded display frames. Returns a
 * single-shot unregister function — call it on cleanup. Idempotent if the
 * returned function is invoked more than once.
 */
export function registerFrameConsumer(): () => void {
  const previousCount = frameConsumerCount;
  frameConsumerCount++;
  notifyFrameConsumerPresenceIfChanged(previousCount);
  let released = false;
  return () => {
    if (released) return;
    released = true;
    const beforeRelease = frameConsumerCount;
    frameConsumerCount = Math.max(0, frameConsumerCount - 1);
    notifyFrameConsumerPresenceIfChanged(beforeRelease);
  };
}

/** True when at least one consumer is mounted and needs decoded frames. */
export function hasActiveFrameConsumers(): boolean {
  return frameConsumerCount > 0;
}

export function subscribeFrameConsumerPresence(
  listener: FrameConsumerPresenceListener,
): () => void {
  frameConsumerPresenceListeners.add(listener);
  return () => frameConsumerPresenceListeners.delete(listener);
}

/** Test-only escape hatch; not part of the public API. */
export function _resetFrameConsumerCount(): void {
  const previousCount = frameConsumerCount;
  frameConsumerCount = 0;
  notifyFrameConsumerPresenceIfChanged(previousCount);
}
