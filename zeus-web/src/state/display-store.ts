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

import { create } from 'zustand';
import type { DecodedFrame } from '../realtime/frame';

export type DisplayState = {
  connected: boolean;
  width: number;
  centerHz: bigint;
  hzPerPixel: number;
  panDb: Float32Array | null;
  wfDb: Float32Array | null;
  panValid: boolean;
  wfValid: boolean;
  lastSeq: number;
  setConnected: (c: boolean) => void;
  pushFrame: (f: DecodedFrame) => void;
};

export const useDisplayStore = create<DisplayState>((set) => ({
  connected: false,
  width: 0,
  centerHz: 0n,
  hzPerPixel: 0,
  panDb: null,
  wfDb: null,
  panValid: false,
  wfValid: false,
  lastSeq: 0,
  setConnected: (connected) => set({ connected }),
  pushFrame: (f) =>
    set({
      width: f.width,
      centerHz: f.centerHz,
      hzPerPixel: f.hzPerPixel,
      panDb: f.panDb,
      wfDb: f.wfDb,
      panValid: f.panValid,
      wfValid: f.wfValid,
      lastSeq: f.seq,
    }),
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

/**
 * Mark this caller as a live consumer of decoded display frames. Returns a
 * single-shot unregister function — call it on cleanup. Idempotent if the
 * returned function is invoked more than once.
 */
export function registerFrameConsumer(): () => void {
  frameConsumerCount++;
  let released = false;
  return () => {
    if (released) return;
    released = true;
    frameConsumerCount = Math.max(0, frameConsumerCount - 1);
  };
}

/** True when at least one consumer is mounted and needs decoded frames. */
export function hasActiveFrameConsumers(): boolean {
  return frameConsumerCount > 0;
}

/** Test-only escape hatch; not part of the public API. */
export function _resetFrameConsumerCount(): void {
  frameConsumerCount = 0;
}
