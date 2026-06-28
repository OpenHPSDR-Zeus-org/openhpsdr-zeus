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

/** One echoed transmission of OUR own station — derived from a rising
 *  transmitting edge so the operator sees their outgoing sequence interleaved
 *  with received decodes (the WSJT-X "yellow Tx line"). Purely a UI record. */
export interface Ft8TxEcho {
  id: string;
  /** Wall-clock ms when the transmission started (for timestamping/sorting). */
  timeUtcMs: number;
  message: string;
  mode: string;
  slot: string;
  audioHz: number;
}

/** Keep the TX echo list bounded — one entry per transmission. */
const MAX_TX_ECHO = 50;

interface Ft8TxState {
  status: Ft8TxStatus | null;
  /** Newest-first rolling list of our own transmissions (rising-edge derived). */
  txEcho: Ft8TxEcho[];
  ingest: (status: Ft8TxStatus) => void;
  /** Clear the TX echo list (e.g. when leaving the workspace). */
  clearTxEcho: () => void;
}

let txEchoSeq = 0;

export const useFt8TxStore = create<Ft8TxState>((set) => ({
  status: null,
  txEcho: [],
  ingest: (status) =>
    set((s) => {
      // Rising transmitting edge with a message → record our own TX line.
      const startedTx = status.transmitting && !s.status?.transmitting && !!status.message;
      if (!startedTx) return { status };
      const echo: Ft8TxEcho = {
        id: `tx:${Date.now()}:${txEchoSeq++}`,
        timeUtcMs: Date.now(),
        message: status.message as string,
        mode: status.mode,
        slot: status.slot,
        audioHz: status.audioHz,
      };
      return { status, txEcho: [echo, ...s.txEcho].slice(0, MAX_TX_ECHO) };
    }),
  clearTxEcho: () => set({ txEcho: [] }),
}));
