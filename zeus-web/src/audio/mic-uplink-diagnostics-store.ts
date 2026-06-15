// SPDX-License-Identifier: GPL-2.0-or-later

import { create } from 'zustand';
import type { MicPcmSendResult } from '../realtime/ws-client';

const MIC_DBFS_FLOOR = -100;

function finiteDbfs(value: number): number {
  return Number.isFinite(value) ? value : MIC_DBFS_FLOOR;
}

export type MicUplinkDiagnosticsState = {
  localBlocks: number;
  sentBlocks: number;
  droppedBlocks: number;
  transportReady: boolean;
  txForced: boolean;
  lastLocalBlockAtMs: number | null;
  lastSentAtMs: number | null;
  lastDropReason: MicPcmSendResult | null;
  lastPeakDbfs: number;
  setTransportReady: (ready: boolean) => void;
  setTxForced: (forced: boolean) => void;
  recordLocalBlock: (peakDbfs: number) => void;
  recordSendResult: (result: MicPcmSendResult) => void;
};

export const useMicUplinkDiagnosticsStore = create<MicUplinkDiagnosticsState>((set) => ({
  localBlocks: 0,
  sentBlocks: 0,
  droppedBlocks: 0,
  transportReady: false,
  txForced: false,
  lastLocalBlockAtMs: null,
  lastSentAtMs: null,
  lastDropReason: null,
  lastPeakDbfs: MIC_DBFS_FLOOR,
  setTransportReady: (transportReady) => set({ transportReady }),
  setTxForced: (txForced) => set({ txForced }),
  recordLocalBlock: (peakDbfs) => set((prev) => ({
    localBlocks: prev.localBlocks + 1,
    lastLocalBlockAtMs: Date.now(),
    lastPeakDbfs: finiteDbfs(peakDbfs),
  })),
  recordSendResult: (result) => set((prev) => {
    if (result === 'sent') {
      return {
        sentBlocks: prev.sentBlocks + 1,
        lastSentAtMs: Date.now(),
        lastDropReason: null,
      };
    }
    return {
      droppedBlocks: prev.droppedBlocks + 1,
      lastDropReason: result,
    };
  }),
}));
