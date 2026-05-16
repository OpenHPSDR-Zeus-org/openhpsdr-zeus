// SPDX-License-Identifier: GPL-2.0-or-later
//
// Unit tests for the Phase 4 mic-peak path.
//
//   1. mic-peak-store: setPeak writes the float + timestamp; reset clears.
//   2. Wire decode (mirrors the ws-client dispatcher): a 13-byte
//      [0x1C][f32 LE][i64 LE] frame round-trips to the store correctly.
//
// We don't spin up a real WebSocket — the ws-client message handler is
// covered separately in mic-pcm.test.ts. The byte-pack contract is the
// load-bearing part here, since Phase 2 of the rollup is wire-format-only.

import { describe, expect, it } from 'vitest';

import { MIC_PEAK_FLOOR_DBFS, useMicPeakStore } from './mic-peak-store';

const MSG_TYPE_MIC_PEAK = 0x1c;
const MIC_PEAK_BYTES = 1 + 4 + 8;

/** Encode a MicPeakFrame as the server would, mirroring the C# wire format. */
function encodeMicPeakFrame(peakDbfs: number, tsUnixMs: number): ArrayBuffer {
  const buf = new ArrayBuffer(MIC_PEAK_BYTES);
  const dv = new DataView(buf);
  dv.setUint8(0, MSG_TYPE_MIC_PEAK);
  dv.setFloat32(1, peakDbfs, true);
  dv.setBigInt64(5, BigInt(tsUnixMs), true);
  return buf;
}

/** Decode a MicPeakFrame — same logic as the ws-client dispatcher branch. */
function decodeMicPeakFrame(data: ArrayBuffer): { peakDbfs: number; tsUnixMs: number } {
  const dv = new DataView(data);
  return {
    peakDbfs: dv.getFloat32(1, true),
    tsUnixMs: Number(dv.getBigInt64(5, true)),
  };
}

describe('mic-peak-store', () => {
  it('defaults to the silence floor and zero timestamp', () => {
    useMicPeakStore.getState().__resetForTests();
    const s = useMicPeakStore.getState();
    expect(s.peakDbfs).toBe(MIC_PEAK_FLOOR_DBFS);
    expect(s.tsUnixMs).toBe(0);
  });

  it('setPeak writes both fields atomically', () => {
    useMicPeakStore.getState().__resetForTests();
    useMicPeakStore.getState().setPeak(-23.5, 1_700_000_000_000);
    const s = useMicPeakStore.getState();
    expect(s.peakDbfs).toBeCloseTo(-23.5, 5);
    expect(s.tsUnixMs).toBe(1_700_000_000_000);
  });

  it('reset clears back to the floor', () => {
    useMicPeakStore.getState().setPeak(-12.0, 1234);
    useMicPeakStore.getState().__resetForTests();
    expect(useMicPeakStore.getState().peakDbfs).toBe(MIC_PEAK_FLOOR_DBFS);
    expect(useMicPeakStore.getState().tsUnixMs).toBe(0);
  });
});

describe('mic-peak wire decode', () => {
  it('byte-length is 13 (1 type + 4 float + 8 int64)', () => {
    const buf = encodeMicPeakFrame(-30.0, 1);
    expect(buf.byteLength).toBe(13);
  });

  it('first byte is the MicPeak type (0x1C)', () => {
    const buf = encodeMicPeakFrame(0, 0);
    expect(new DataView(buf).getUint8(0)).toBe(MSG_TYPE_MIC_PEAK);
  });

  it('round-trips a typical talking-level peak', () => {
    // -12 dBFS @ unix-ms 1_700_000_000_000 is what a typical mic test
    // produces when speaking at conversational volume.
    const buf = encodeMicPeakFrame(-12.5, 1_700_000_000_000);
    const decoded = decodeMicPeakFrame(buf);
    expect(decoded.peakDbfs).toBeCloseTo(-12.5, 3);
    expect(decoded.tsUnixMs).toBe(1_700_000_000_000);
  });

  it('round-trips the silence floor', () => {
    const buf = encodeMicPeakFrame(MIC_PEAK_FLOOR_DBFS, 0);
    const decoded = decodeMicPeakFrame(buf);
    expect(decoded.peakDbfs).toBeCloseTo(MIC_PEAK_FLOOR_DBFS, 3);
    expect(decoded.tsUnixMs).toBe(0);
  });

  it('round-trips zero dBFS (clipping case)', () => {
    const buf = encodeMicPeakFrame(0, 999);
    const decoded = decodeMicPeakFrame(buf);
    expect(decoded.peakDbfs).toBe(0);
    expect(decoded.tsUnixMs).toBe(999);
  });

  it('encoded float bytes are little-endian', () => {
    // 1.0 f32 LE = 0x00 0x00 0x80 0x3F — first float slot at offset 1.
    const buf = encodeMicPeakFrame(1.0, 0);
    const dv = new DataView(buf);
    expect(dv.getUint8(1)).toBe(0x00);
    expect(dv.getUint8(2)).toBe(0x00);
    expect(dv.getUint8(3)).toBe(0x80);
    expect(dv.getUint8(4)).toBe(0x3f);
  });

  it('encoded int64 bytes are little-endian', () => {
    // 1 i64 LE = 0x01 0x00 ... 0x00 — i64 slot at offset 5.
    const buf = encodeMicPeakFrame(0, 1);
    const dv = new DataView(buf);
    expect(dv.getUint8(5)).toBe(0x01);
    expect(dv.getUint8(12)).toBe(0x00);
  });

  it('decoded frame writes through to the store', () => {
    useMicPeakStore.getState().__resetForTests();
    const buf = encodeMicPeakFrame(-7.25, 1_700_000_000_500);
    const { peakDbfs, tsUnixMs } = decodeMicPeakFrame(buf);
    useMicPeakStore.getState().setPeak(peakDbfs, tsUnixMs);
    const s = useMicPeakStore.getState();
    expect(s.peakDbfs).toBeCloseTo(-7.25, 3);
    expect(s.tsUnixMs).toBe(1_700_000_000_500);
  });
});
