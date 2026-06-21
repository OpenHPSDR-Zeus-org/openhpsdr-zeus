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

import { describe, expect, it } from 'vitest';
import {
  HEADER_BYTES,
  BODY_FIXED_BYTES,
  MSG_TYPE_DISPLAY_FRAME,
  decodeDisplayFrame,
  encodeDisplayFrame,
  FrameDecodeError,
} from './frame';

function sampleFrame(width: number) {
  const panDb = new Float32Array(width);
  const wfDb = new Float32Array(width);
  for (let i = 0; i < width; i++) {
    panDb[i] = -80 + (i % 32);
    wfDb[i] = -90 + (i % 16);
  }
  return {
    seq: 42,
    tsUnixMs: 1_700_000_000_123.5,
    rxId: 0,
    bodyFlags: 0x03,
    width,
    centerHz: 14_074_000n,
    hzPerPixel: 192_000 / width,
    panDb,
    wfDb,
  };
}

describe('decodeDisplayFrame', () => {
  it('round-trips happy path', () => {
    const frame = sampleFrame(1024);
    const buf = encodeDisplayFrame(frame);
    expect(buf.byteLength).toBe(HEADER_BYTES + BODY_FIXED_BYTES + frame.width * 4 * 2);

    const dec = decodeDisplayFrame(buf);
    expect(dec.msgType).toBe(MSG_TYPE_DISPLAY_FRAME);
    expect(dec.seq).toBe(frame.seq);
    expect(dec.tsUnixMs).toBe(frame.tsUnixMs);
    expect(dec.rxId).toBe(frame.rxId);
    expect(dec.bodyFlags).toBe(frame.bodyFlags);
    expect(dec.panValid).toBe(true);
    expect(dec.wfValid).toBe(true);
    expect(dec.width).toBe(frame.width);
    expect(dec.centerHz).toBe(frame.centerHz);
    expect(dec.hzPerPixel).toBeCloseTo(frame.hzPerPixel, 4);
    expect(dec.panDb.length).toBe(frame.width);
    expect(dec.wfDb.length).toBe(frame.width);
    for (let i = 0; i < frame.width; i++) {
      expect(dec.panDb[i]).toBeCloseTo(frame.panDb[i]!, 5);
      expect(dec.wfDb[i]).toBeCloseTo(frame.wfDb[i]!, 5);
    }
  });

  it('reports valid bits from bodyFlags', () => {
    const frame = sampleFrame(128);
    frame.bodyFlags = 0x01;
    const dec = decodeDisplayFrame(encodeDisplayFrame(frame));
    expect(dec.panValid).toBe(true);
    expect(dec.wfValid).toBe(false);
  });

  it('rejects wrong msgType', () => {
    const frame = sampleFrame(64);
    const buf = encodeDisplayFrame(frame);
    new DataView(buf).setUint8(0, 0x99);
    expect(() => decodeDisplayFrame(buf)).toThrow(FrameDecodeError);
  });

  it('rejects truncated buffer', () => {
    const frame = sampleFrame(64);
    const full = encodeDisplayFrame(frame);
    const truncated = full.slice(0, full.byteLength - 4);
    expect(() => decodeDisplayFrame(truncated)).toThrow(FrameDecodeError);
  });

  it('rejects mismatched payloadLen vs width', () => {
    const frame = sampleFrame(64);
    const buf = encodeDisplayFrame(frame);
    new DataView(buf).setUint16(2, BODY_FIXED_BYTES + 4, true);
    expect(() => decodeDisplayFrame(buf)).toThrow(FrameDecodeError);
  });

  it('rejects invalid display geometry before exposing bin arrays', () => {
    for (const patch of [
      (dv: DataView) => dv.setUint16(HEADER_BYTES + 2, 0, true),
      (dv: DataView) => dv.setFloat32(HEADER_BYTES + 12, Number.NaN, true),
      (dv: DataView) => dv.setFloat32(HEADER_BYTES + 12, Infinity, true),
      (dv: DataView) => dv.setFloat32(HEADER_BYTES + 12, 0, true),
    ]) {
      const buf = encodeDisplayFrame(sampleFrame(64));
      patch(new DataView(buf));
      expect(() => decodeDisplayFrame(buf)).toThrow(FrameDecodeError);
    }
  });
});

describe('encodeDisplayFrame', () => {
  it('rejects invalid geometry instead of wrapping it onto the wire', () => {
    expect(() => encodeDisplayFrame(sampleFrame(0))).toThrow(FrameDecodeError);
    expect(() =>
      encodeDisplayFrame({
        ...sampleFrame(64),
        hzPerPixel: Number.NaN,
      }),
    ).toThrow(FrameDecodeError);
  });

  it('rejects bin arrays that do not match the declared frame width', () => {
    expect(() =>
      encodeDisplayFrame({
        ...sampleFrame(64),
        panDb: new Float32Array(63),
      }),
    ).toThrow(FrameDecodeError);
    expect(() =>
      encodeDisplayFrame({
        ...sampleFrame(64),
        wfDb: new Float32Array(65),
      }),
    ).toThrow(FrameDecodeError);
  });
});
