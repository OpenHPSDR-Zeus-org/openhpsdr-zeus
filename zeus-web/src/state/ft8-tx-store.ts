// SPDX-License-Identifier: GPL-2.0-or-later
//
// FT8/FT4/WSPR TX keyer status store. Holds the authoritative keyer state pushed
// by the backend as 0x3A Ft8TxStatus WS frames (one per arm/stage/transmit
// edge). The TX HUD renders the arm/transmit lamps from THIS — what the backend
// actually keyed — not from what the operator staged locally. Mirrors the
// ft8-store / ptt-store split: WS push for live state.

import { create } from 'zustand';

/** The 0x3A Ft8TxStatusDto payload (camelCase JSON off the wire). */
export interface Ft8TxStatus {
  armed: boolean;
  transmitting: boolean;
  mode: string; // "FT8" | "FT4" | "WSPR"
  message: string | null;
  audioHz: number;
  slot: string; // "even" | "odd" | ""
  watchdogSecsRemaining: number;
  lastTxSlotMs: number | null;
  nativeAvailable: boolean;
}

interface Ft8TxState {
  status: Ft8TxStatus | null;
  ingest: (status: Ft8TxStatus) => void;
}

export const useFt8TxStore = create<Ft8TxState>((set) => ({
  status: null,
  ingest: (status) => set({ status }),
}));
