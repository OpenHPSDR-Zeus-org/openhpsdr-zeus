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

export const MSG_TYPE_AUDIO_PCM = 0x02;

export const AUDIO_HEADER_BYTES = 16;
export const AUDIO_BODY_FIXED_BYTES = 1 + 1 + 4 + 2;

export type DecodedAudioFrame = {
  msgType: number;
  seq: number;
  tsUnixMs: number;
  rxId: number;
  channels: number;
  sampleRateHz: number;
  sampleCount: number;
  samples: Float32Array;
};

export class AudioFrameDecodeError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'AudioFrameDecodeError';
  }
}

export function decodeAudioFrame(buffer: ArrayBuffer): DecodedAudioFrame {
  if (buffer.byteLength < AUDIO_HEADER_BYTES + AUDIO_BODY_FIXED_BYTES) {
    throw new AudioFrameDecodeError(
      `buffer too short: ${buffer.byteLength} < ${AUDIO_HEADER_BYTES + AUDIO_BODY_FIXED_BYTES}`,
    );
  }

  const dv = new DataView(buffer);

  const msgType = dv.getUint8(0);
  const payloadLen = dv.getUint16(2, true);
  const seq = dv.getUint32(4, true);
  const tsUnixMs = dv.getFloat64(8, true);

  if (msgType !== MSG_TYPE_AUDIO_PCM) {
    throw new AudioFrameDecodeError(`unexpected msgType 0x${msgType.toString(16)}`);
  }

  const expectedTotal = AUDIO_HEADER_BYTES + payloadLen;
  if (buffer.byteLength < expectedTotal) {
    throw new AudioFrameDecodeError(
      `payloadLen ${payloadLen} exceeds buffer (${buffer.byteLength - AUDIO_HEADER_BYTES})`,
    );
  }

  const rxId = dv.getUint8(AUDIO_HEADER_BYTES + 0);
  const channels = dv.getUint8(AUDIO_HEADER_BYTES + 1);
  const sampleRateHz = dv.getUint32(AUDIO_HEADER_BYTES + 2, true);
  const sampleCount = dv.getUint16(AUDIO_HEADER_BYTES + 6, true);

  if (channels < 1) {
    throw new AudioFrameDecodeError(`channels must be >= 1, got ${channels}`);
  }

  const sampleFloats = sampleCount * channels;
  const sampleBytes = sampleFloats * 4;
  const needed = AUDIO_BODY_FIXED_BYTES + sampleBytes;
  if (payloadLen < needed) {
    throw new AudioFrameDecodeError(
      `payloadLen ${payloadLen} < required ${needed} for ${sampleCount} x ${channels}`,
    );
  }

  const samplesOffset = AUDIO_HEADER_BYTES + AUDIO_BODY_FIXED_BYTES;
  const baseMod = (buffer as ArrayBuffer & { byteOffset?: number }).byteOffset ?? 0;
  const samples =
    (baseMod + samplesOffset) % 4 === 0
      ? new Float32Array(buffer, samplesOffset, sampleFloats)
      : new Float32Array(buffer.slice(samplesOffset, samplesOffset + sampleBytes));

  return {
    msgType,
    seq,
    tsUnixMs,
    rxId,
    channels,
    sampleRateHz,
    sampleCount,
    samples,
  };
}

export function encodeAudioFrame(frame: {
  seq: number;
  tsUnixMs: number;
  rxId: number;
  channels: number;
  sampleRateHz: number;
  sampleCount: number;
  samples: Float32Array;
}): ArrayBuffer {
  const { sampleCount, channels } = frame;
  const sampleBytes = sampleCount * channels * 4;
  const payloadLen = AUDIO_BODY_FIXED_BYTES + sampleBytes;
  const total = AUDIO_HEADER_BYTES + payloadLen;
  const buf = new ArrayBuffer(total);
  const dv = new DataView(buf);

  dv.setUint8(0, MSG_TYPE_AUDIO_PCM);
  dv.setUint8(1, 0);
  dv.setUint16(2, payloadLen, true);
  dv.setUint32(4, frame.seq, true);
  dv.setFloat64(8, frame.tsUnixMs, true);

  dv.setUint8(AUDIO_HEADER_BYTES + 0, frame.rxId);
  dv.setUint8(AUDIO_HEADER_BYTES + 1, frame.channels);
  dv.setUint32(AUDIO_HEADER_BYTES + 2, frame.sampleRateHz, true);
  dv.setUint16(AUDIO_HEADER_BYTES + 6, frame.sampleCount, true);

  new Float32Array(buf, AUDIO_HEADER_BYTES + AUDIO_BODY_FIXED_BYTES, sampleCount * channels).set(
    frame.samples,
  );

  return buf;
}
