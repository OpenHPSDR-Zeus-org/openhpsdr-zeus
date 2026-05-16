// SPDX-License-Identifier: GPL-2.0-or-later
//
// Native-mode mic-peak store. Single peak dBFS float, written from the WS
// dispatcher when the server-side MicPeakFrame (0x1C) arrives (desktop
// host mode only — Phase 4), read by the MicMeter component.
//
// Mirrors the lightweight zustand pattern used by the other audio-path
// stores. We intentionally keep this *separate* from tx-store.micDbfs so
// the two transports (native MicPeakFrame vs. browser AudioWorklet) are
// cleanly addressable from one place each — there is no temptation to
// double-write the same field from two producers.

import { create } from 'zustand';

// Silence floor — must match Zeus.Contracts/MicPeakFrame.cs#MinDbfs. The
// server clamps below this before broadcasting, so the wire value is
// always ≥ MIC_PEAK_FLOOR_DBFS; we duplicate the constant here so the
// MicMeter component can render an "−∞-ish" state without importing
// server-only types.
export const MIC_PEAK_FLOOR_DBFS = -120;

interface MicPeakState {
  /** Latest peak dBFS reported by the server. Starts at the floor so the
   *  meter renders empty until the first frame arrives. */
  peakDbfs: number;
  /** Server-side unix-ms timestamp of the latest frame; 0 until the first
   *  frame. Useful for a future "mic stream stalled" indicator — the
   *  frontend can detect frame starvation by comparing this against
   *  Date.now() at render time. */
  tsUnixMs: number;
  setPeak: (peakDbfs: number, tsUnixMs: number) => void;
  /** Test escape hatch. Resets to the floor so a describe block can drive
   *  transitions without polluting later tests. */
  __resetForTests: () => void;
}

export const useMicPeakStore = create<MicPeakState>((set) => ({
  peakDbfs: MIC_PEAK_FLOOR_DBFS,
  tsUnixMs: 0,
  setPeak: (peakDbfs, tsUnixMs) => set({ peakDbfs, tsUnixMs }),
  __resetForTests: () => set({ peakDbfs: MIC_PEAK_FLOOR_DBFS, tsUnixMs: 0 }),
}));
