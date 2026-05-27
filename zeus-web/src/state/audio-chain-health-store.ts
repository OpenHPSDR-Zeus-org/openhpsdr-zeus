// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { create } from 'zustand';

// Wire-aligned with Zeus.Contracts/AudioChainHealthFrame.cs. Renumbering
// any of these is a wire break — the byte values are pinned to the
// server-side enums.
export enum AudioChainStageId {
  Mic = 0,
  Eq = 1,
  Leveler = 2,
  Cfc = 3,
  Comp = 4,
  Alc = 5,
  Out = 6,
  Wire = 7,
  Pa = 8,
}

export enum AudioChainSeverity {
  Ok = 0,
  Info = 1,
  Warn = 2,
  Error = 3,
}

export const VERDICT_FLAG_IMMEDIATE_ACTION = 1 << 0;
export const VERDICT_FLAG_HAS_APPLY = 1 << 1;

export type AudioChainVerdict = {
  stageId: AudioChainStageId;
  severity: AudioChainSeverity;
  flags: number;
  message: string;
  applyLabel: string;
};

// RxMode byte ordering from Zeus.Contracts/Dtos.cs.
export enum RxMode {
  Lsb = 0,
  Usb = 1,
  Cwl = 2,
  Cwu = 3,
  Am = 4,
  Fm = 5,
  Sam = 6,
  Dsb = 7,
  Digl = 8,
  Digu = 9,
}

type DismissKey = string;

const dismissKey = (id: AudioChainStageId, severity: AudioChainSeverity, message: string): DismissKey =>
  `${id}|${severity}|${message}`;

type Snapshot = {
  mode: RxMode;
  // Keyed by stageId for O(1) lookup by the widget tiles.
  byStage: Map<AudioChainStageId, AudioChainVerdict>;
  receivedAt: number;
};

const emptySnapshot: Snapshot = {
  mode: RxMode.Usb,
  byStage: new Map(),
  receivedAt: 0,
};

type AudioChainHealthState = {
  snapshot: Snapshot;
  // Session-only dismissals per ADR-0003. Keyed by (stageId, severity,
  // message) so a verdict that clears and re-fires with a different
  // message comes back from suppression — matches CONTEXT.md
  // "Dismissal" definition.
  dismissed: Set<DismissKey>;
  setSnapshot: (s: Snapshot) => void;
  dismiss: (v: AudioChainVerdict) => void;
  isDismissed: (v: AudioChainVerdict) => boolean;
  reset: () => void;
};

export const useAudioChainHealthStore = create<AudioChainHealthState>((set, get) => ({
  snapshot: emptySnapshot,
  dismissed: new Set<DismissKey>(),
  setSnapshot: (s) => set({ snapshot: s }),
  dismiss: (v) =>
    set((prev) => {
      const next = new Set(prev.dismissed);
      next.add(dismissKey(v.stageId, v.severity, v.message));
      return { dismissed: next };
    }),
  isDismissed: (v) => get().dismissed.has(dismissKey(v.stageId, v.severity, v.message)),
  reset: () => set({ snapshot: emptySnapshot, dismissed: new Set() }),
}));

/**
 * Decode an AudioChainHealth frame (MsgType 0x32) payload starting at
 * byte 0 of `buffer` (the 0x32 type byte is at offset 0). Throws on
 * malformed payload — the ws-client catches and logs once.
 *
 * Wire format (see Zeus.Contracts/AudioChainHealthFrame.cs):
 *   [type:1=0x32][mode:u8][verdictCount:u8]
 *   per verdict: [stageId:u8][severity:u8][flags:u8][msgLen:u8][applyLen:u8][msg…][apply…]
 */
export function decodeAudioChainHealthFrame(buffer: ArrayBuffer): {
  mode: RxMode;
  verdicts: AudioChainVerdict[];
} {
  if (buffer.byteLength < 3) {
    throw new Error(`audio-chain-health frame too short: ${buffer.byteLength}`);
  }
  const dv = new DataView(buffer);
  if (dv.getUint8(0) !== 0x32) {
    throw new Error(`audio-chain-health frame: wrong msgtype 0x${dv.getUint8(0).toString(16)}`);
  }
  const mode = dv.getUint8(1) as RxMode;
  const count = dv.getUint8(2);
  const verdicts: AudioChainVerdict[] = [];
  let offset = 3;
  const decoder = new TextDecoder('utf-8');
  for (let i = 0; i < count; i++) {
    if (offset + 5 > buffer.byteLength) {
      throw new Error(`audio-chain-health frame: truncated at verdict ${i} header`);
    }
    const stageId = dv.getUint8(offset + 0) as AudioChainStageId;
    const severity = dv.getUint8(offset + 1) as AudioChainSeverity;
    const flags = dv.getUint8(offset + 2);
    const msgLen = dv.getUint8(offset + 3);
    const aplLen = dv.getUint8(offset + 4);
    offset += 5;
    if (offset + msgLen + aplLen > buffer.byteLength) {
      throw new Error(`audio-chain-health frame: truncated at verdict ${i} payload`);
    }
    const message = msgLen === 0 ? '' : decoder.decode(new Uint8Array(buffer, offset, msgLen));
    offset += msgLen;
    const applyLabel = aplLen === 0 ? '' : decoder.decode(new Uint8Array(buffer, offset, aplLen));
    offset += aplLen;
    verdicts.push({ stageId, severity, flags, message, applyLabel });
  }
  return { mode, verdicts };
}
