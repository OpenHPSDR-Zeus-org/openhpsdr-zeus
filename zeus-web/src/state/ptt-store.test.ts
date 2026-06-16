// SPDX-License-Identifier: GPL-2.0-or-later
//
// PTT-IN status store + wire decode (external-ports plan §4).
//   1. setKeyed writes the live lamp level; reset clears.
//   2. The 2-byte [0x33][keyed:u8] PttStatusFrame round-trips through the same
//      decode the ws-client dispatcher does, into the store.

import { describe, expect, it } from 'vitest';

import { usePttStore } from './ptt-store';

const MSG_TYPE_PTT_STATUS = 0x33;
const PTT_STATUS_BYTES = 2;

function encodePttStatusFrame(keyed: boolean): ArrayBuffer {
  const buf = new ArrayBuffer(PTT_STATUS_BYTES);
  const dv = new DataView(buf);
  dv.setUint8(0, MSG_TYPE_PTT_STATUS);
  dv.setUint8(1, keyed ? 1 : 0);
  return buf;
}

// Mirrors the ws-client dispatcher branch for 0x33.
function dispatchPttStatus(data: ArrayBuffer): void {
  const dv = new DataView(data);
  usePttStore.getState().setKeyed(dv.getUint8(1) !== 0);
}

describe('ptt-store', () => {
  it('defaults: idle, enabled ON, hang 250 ms', () => {
    usePttStore.getState().__resetForTests();
    const s = usePttStore.getState();
    expect(s.keyed).toBe(false);
    expect(s.enabled).toBe(true);
    expect(s.hangMs).toBe(250);
  });

  it('setKeyed toggles the lamp', () => {
    usePttStore.getState().__resetForTests();
    usePttStore.getState().setKeyed(true);
    expect(usePttStore.getState().keyed).toBe(true);
    usePttStore.getState().setKeyed(false);
    expect(usePttStore.getState().keyed).toBe(false);
  });
});

describe('ptt-status wire decode', () => {
  it('byte-length is 2 (1 type + 1 keyed)', () => {
    expect(encodePttStatusFrame(true).byteLength).toBe(2);
  });

  it('first byte is the PttStatus type (0x33)', () => {
    expect(new DataView(encodePttStatusFrame(false)).getUint8(0)).toBe(MSG_TYPE_PTT_STATUS);
  });

  it('keyed edge dispatches into the store', () => {
    usePttStore.getState().__resetForTests();
    dispatchPttStatus(encodePttStatusFrame(true));
    expect(usePttStore.getState().keyed).toBe(true);
    dispatchPttStatus(encodePttStatusFrame(false));
    expect(usePttStore.getState().keyed).toBe(false);
  });
});
