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
  AUDIO_HEADER_BYTES,
  AUDIO_BODY_FIXED_BYTES,
  MSG_TYPE_AUDIO_PCM,
  decodeAudioFrame,
  encodeAudioFrame,
  AudioFrameDecodeError,
} from './frame';

function sampleFrame(sampleCount: number, channels = 1) {
  const samples = new Float32Array(sampleCount * channels);
  for (let i = 0; i < samples.length; i++) samples[i] = Math.sin(i * 0.01) * 0.5;
  return {
    seq: 7,
    tsUnixMs: 1_700_000_000_456.25,
    rxId: 0,
    channels,
    sampleRateHz: 48_000,
    sampleCount,
    samples,
  };
}

describe('decodeAudioFrame', () => {
  it('round-trips mono', () => {
    const frame = sampleFrame(256);
    const buf = encodeAudioFrame(frame);
    expect(buf.byteLength).toBe(
      AUDIO_HEADER_BYTES + AUDIO_BODY_FIXED_BYTES + frame.sampleCount * 4,
    );

    const dec = decodeAudioFrame(buf);
    expect(dec.msgType).toBe(MSG_TYPE_AUDIO_PCM);
    expect(dec.seq).toBe(frame.seq);
    expect(dec.tsUnixMs).toBe(frame.tsUnixMs);
    expect(dec.channels).toBe(1);
    expect(dec.sampleRateHz).toBe(48_000);
    expect(dec.sampleCount).toBe(frame.sampleCount);
    expect(dec.samples.length).toBe(frame.samples.length);
    for (let i = 0; i < frame.samples.length; i++) {
      expect(dec.samples[i]).toBeCloseTo(frame.samples[i]!, 5);
    }
  });

  it('round-trips stereo', () => {
    const frame = sampleFrame(128, 2);
    const dec = decodeAudioFrame(encodeAudioFrame(frame));
    expect(dec.channels).toBe(2);
    expect(dec.samples.length).toBe(128 * 2);
  });

  it('rejects wrong msgType', () => {
    const frame = sampleFrame(16);
    const buf = encodeAudioFrame(frame);
    new DataView(buf).setUint8(0, 0x99);
    expect(() => decodeAudioFrame(buf)).toThrow(AudioFrameDecodeError);
  });

  it('rejects truncated buffer', () => {
    const frame = sampleFrame(64);
    const full = encodeAudioFrame(frame);
    const truncated = full.slice(0, full.byteLength - 4);
    expect(() => decodeAudioFrame(truncated)).toThrow(AudioFrameDecodeError);
  });
});
