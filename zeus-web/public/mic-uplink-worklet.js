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
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// AudioWorkletProcessor that converts mono mic samples into 960-sample
// (20 ms @ 48 kHz) blocks and transfers each block to the main thread over
// this.port. Matches the server-side MSG_TYPE_MIC_PCM = 0x20 contract.
//
// Lives in public/ (not src/) so Vite serves it verbatim at /mic-uplink-worklet.js
// without ES-module transpilation. AudioWorklet loader (addModule) needs a URL
// to a standalone JS file with no imports; a bundled worker chunk won't work.
//
// Sample rate: 48 kHz contexts take the fast path. If a browser/device ignores
// the requested AudioContext rate, a small windowed-sinc converter keeps the
// downstream TX mic-block contract correct instead of mis-framing audio.

const OUTPUT_RATE = 48000;
const BLOCK_SAMPLES = 960;
const RESAMPLE_RADIUS = 16;

function sinc(x) {
  if (Math.abs(x) < 1e-8) return 1;
  const pix = Math.PI * x;
  return Math.sin(pix) / pix;
}

function raisedCosineWindow(distance) {
  if (distance >= RESAMPLE_RADIUS) return 0;
  return 0.5 + 0.5 * Math.cos(Math.PI * distance / RESAMPLE_RADIUS);
}

function sanitizeSample(sample) {
  if (!Number.isFinite(sample)) return 0;
  if (sample > 1) return 1;
  if (sample < -1) return -1;
  return sample;
}

class MicUplinkProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this._buf = new Float32Array(BLOCK_SAMPLES);
    this._fill = 0;
    const actualSampleRate = typeof sampleRate === 'number' ? sampleRate : OUTPUT_RATE;
    this._inputRate = Number.isFinite(actualSampleRate) && actualSampleRate > 0
      ? actualSampleRate
      : OUTPUT_RATE;
    this._passthrough = this._inputRate === OUTPUT_RATE;
    this._input = [];
    this._phase = 0;
    this._step = this._inputRate / OUTPUT_RATE;
    this._cutoff = Math.min(0.5, (OUTPUT_RATE / this._inputRate) * 0.5) * 0.94;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0) return true;
    const ch = input[0];
    if (!ch) return true;

    if (!this._passthrough) {
      this._appendResampled(ch);
      return true;
    }

    let srcIdx = 0;
    while (srcIdx < ch.length) {
      const room = BLOCK_SAMPLES - this._fill;
      const take = Math.min(room, ch.length - srcIdx);
      this._buf.set(ch.subarray(srcIdx, srcIdx + take), this._fill);
      this._fill += take;
      srcIdx += take;

      if (this._fill === BLOCK_SAMPLES) {
        this._emitBlock();
      }
    }
    return true;
  }

  _appendResampled(ch) {
    for (let i = 0; i < ch.length; i++) this._input.push(sanitizeSample(ch[i]));

    while (this._phase + RESAMPLE_RADIUS < this._input.length) {
      this._appendOutputSample(this._sampleAt(this._phase));
      this._phase += this._step;
    }

    const drop = Math.max(0, Math.floor(this._phase) - RESAMPLE_RADIUS);
    if (drop > 0) {
      this._input = this._input.slice(drop);
      this._phase -= drop;
    }
  }

  _sampleAt(phase) {
    const start = Math.ceil(phase - RESAMPLE_RADIUS);
    const end = Math.floor(phase + RESAMPLE_RADIUS);
    let sum = 0;
    let norm = 0;
    for (let i = start; i <= end; i++) {
      if (i < 0 || i >= this._input.length) continue;
      const d = phase - i;
      const w = 2 * this._cutoff * sinc(2 * this._cutoff * d) *
        raisedCosineWindow(Math.abs(d));
      sum += this._input[i] * w;
      norm += w;
    }
    return norm !== 0 ? sum / norm : 0;
  }

  _appendOutputSample(sample) {
    this._buf[this._fill++] = sample;
    if (this._fill === BLOCK_SAMPLES) {
      this._emitBlock();
    }
  }

  _emitBlock() {
    // Transfer the buffer to the main thread (zero-copy) and allocate a fresh
    // one. New-alloc cost is ~192 KB/s at 50 Hz, acceptable. Peak-level
    // computation runs here (once per 20 ms block) so the main thread can drive
    // a mic meter at exactly the uplink cadence.
    const out = this._buf;
    let peak = 0;
    for (let i = 0; i < BLOCK_SAMPLES; i++) {
      const sample = sanitizeSample(out[i]);
      out[i] = sample;
      const a = sample < 0 ? -sample : sample;
      if (a > peak) peak = a;
    }
    this._buf = new Float32Array(BLOCK_SAMPLES);
    this._fill = 0;
    this.port.postMessage({ samples: out, peak }, [out.buffer]);
  }
}

registerProcessor('mic-uplink', MicUplinkProcessor);
