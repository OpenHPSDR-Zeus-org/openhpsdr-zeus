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

export const MSG_TYPE_DISPLAY_FRAME = 0x01;

export const HEADER_BYTES = 16;
export const BODY_FIXED_BYTES = 16;

export type DecodedFrame = {
  msgType: number;
  headerFlags: number;
  seq: number;
  tsUnixMs: number;
  rxId: number;
  bodyFlags: number;
  panValid: boolean;
  wfValid: boolean;
  width: number;
  centerHz: bigint;
  hzPerPixel: number;
  panDb: Float32Array;
  wfDb: Float32Array;
};

export class FrameDecodeError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'FrameDecodeError';
  }
}

function validDisplayGeometry(width: number, hzPerPixel: number): boolean {
  return (
    Number.isInteger(width) &&
    width > 0 &&
    width <= 0xffff &&
    Number.isFinite(hzPerPixel) &&
    hzPerPixel > 0
  );
}

export function decodeDisplayFrame(buffer: ArrayBuffer): DecodedFrame {
  if (buffer.byteLength < HEADER_BYTES + BODY_FIXED_BYTES) {
    throw new FrameDecodeError(
      `buffer too short: ${buffer.byteLength} < ${HEADER_BYTES + BODY_FIXED_BYTES}`,
    );
  }

  const dv = new DataView(buffer);

  const msgType = dv.getUint8(0);
  const headerFlags = dv.getUint8(1);
  const payloadLen = dv.getUint16(2, true);
  const seq = dv.getUint32(4, true);
  const tsUnixMs = dv.getFloat64(8, true);

  if (msgType !== MSG_TYPE_DISPLAY_FRAME) {
    throw new FrameDecodeError(`unexpected msgType 0x${msgType.toString(16)}`);
  }

  const expectedTotal = HEADER_BYTES + payloadLen;
  if (buffer.byteLength < expectedTotal) {
    throw new FrameDecodeError(
      `payloadLen ${payloadLen} exceeds buffer (${buffer.byteLength - HEADER_BYTES})`,
    );
  }

  const rxId = dv.getUint8(HEADER_BYTES + 0);
  const bodyFlags = dv.getUint8(HEADER_BYTES + 1);
  const width = dv.getUint16(HEADER_BYTES + 2, true);
  const centerHz = dv.getBigInt64(HEADER_BYTES + 4, true);
  const hzPerPixel = dv.getFloat32(HEADER_BYTES + 12, true);
  if (!validDisplayGeometry(width, hzPerPixel)) {
    throw new FrameDecodeError(
      `invalid display geometry: width ${width}, hzPerPixel ${hzPerPixel}`,
    );
  }

  const pixelBytes = width * 4;
  const needed = BODY_FIXED_BYTES + pixelBytes * 2;
  if (payloadLen < needed) {
    throw new FrameDecodeError(
      `payloadLen ${payloadLen} < required ${needed} for width ${width}`,
    );
  }

  const panOffset = HEADER_BYTES + BODY_FIXED_BYTES;
  const wfOffset = panOffset + pixelBytes;

  // Source offsets (32, 32 + width*4) are 4-byte aligned by construction,
  // but the incoming ArrayBuffer's base offset need not be — copy if misaligned.
  const baseMod = (buffer as ArrayBuffer & { byteOffset?: number }).byteOffset ?? 0;
  const panDb =
    (baseMod + panOffset) % 4 === 0
      ? new Float32Array(buffer, panOffset, width)
      : new Float32Array(buffer.slice(panOffset, panOffset + pixelBytes));
  const wfDb =
    (baseMod + wfOffset) % 4 === 0
      ? new Float32Array(buffer, wfOffset, width)
      : new Float32Array(buffer.slice(wfOffset, wfOffset + pixelBytes));

  return {
    msgType,
    headerFlags,
    seq,
    tsUnixMs,
    rxId,
    bodyFlags,
    panValid: (bodyFlags & 0x01) !== 0,
    wfValid: (bodyFlags & 0x02) !== 0,
    width,
    centerHz,
    hzPerPixel,
    panDb,
    wfDb,
  };
}

export function encodeDisplayFrame(frame: {
  seq: number;
  tsUnixMs: number;
  rxId: number;
  bodyFlags: number;
  width: number;
  centerHz: bigint;
  hzPerPixel: number;
  panDb: Float32Array;
  wfDb: Float32Array;
}): ArrayBuffer {
  const { width } = frame;
  if (!validDisplayGeometry(width, frame.hzPerPixel)) {
    throw new FrameDecodeError(
      `invalid display geometry: width ${width}, hzPerPixel ${frame.hzPerPixel}`,
    );
  }
  if (frame.panDb.length !== width || frame.wfDb.length !== width) {
    throw new FrameDecodeError(
      `display bin length mismatch: width ${width}, pan ${frame.panDb.length}, wf ${frame.wfDb.length}`,
    );
  }
  const pixelBytes = width * 4;
  const payloadLen = BODY_FIXED_BYTES + pixelBytes * 2;
  const total = HEADER_BYTES + payloadLen;
  const buf = new ArrayBuffer(total);
  const dv = new DataView(buf);

  dv.setUint8(0, MSG_TYPE_DISPLAY_FRAME);
  dv.setUint8(1, 0);
  dv.setUint16(2, payloadLen, true);
  dv.setUint32(4, frame.seq, true);
  dv.setFloat64(8, frame.tsUnixMs, true);

  dv.setUint8(HEADER_BYTES + 0, frame.rxId);
  dv.setUint8(HEADER_BYTES + 1, frame.bodyFlags);
  dv.setUint16(HEADER_BYTES + 2, width, true);
  dv.setBigInt64(HEADER_BYTES + 4, frame.centerHz, true);
  dv.setFloat32(HEADER_BYTES + 12, frame.hzPerPixel, true);

  new Float32Array(buf, HEADER_BYTES + BODY_FIXED_BYTES, width).set(frame.panDb);
  new Float32Array(buf, HEADER_BYTES + BODY_FIXED_BYTES + pixelBytes, width).set(frame.wfDb);

  return buf;
}
