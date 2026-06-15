// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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

// Mic uplink: getUserMedia -> AudioContext -> MediaStreamSource ->
// AudioWorkletNode('mic-uplink'). The worklet emits 960-sample 48 kHz blocks
// and posts them here; we forward each block to the caller-supplied handler
// (typically ws-client to ship [0x20] ...).
//
//
// Ham-radio constraints: echoCancellation/noiseSuppression/autoGainControl all
// OFF so WDSP TXA is the only thing shaping mic audio. Browser constraint
// request for sampleRate: 48000. Most browsers honor this, and the worklet
// resamples the actual AudioWorklet rate if a device/browser does not.

import { isNativeAudio } from './host-mode';

// `peak` is the max(abs(sample)) across the 20 ms block, linear [0..1].
// Callers convert to dBFS via 20 * log10(peak); floor at −100 for silence.
export type MicUplinkBlockHandler = (samples: Float32Array, peak: number) => void;

export type MicUplinkHandle = {
  stop: () => Promise<void>;
};

const MIC_CONSTRAINTS: MediaStreamConstraints = {
  audio: {
    echoCancellation: false,
    noiseSuppression: false,
    autoGainControl: false,
    channelCount: 1,
    sampleRate: 48000,
  },
};

const WORKLET_URL = '/mic-uplink-worklet.js';
const EXPECTED_BLOCK_SAMPLES = 960;

export async function startMicUplink(
  onBlock: MicUplinkBlockHandler,
): Promise<MicUplinkHandle> {
  // Phase 2c — desktop mode runs a native miniaudio capture in the host
  // process; calling getUserMedia in the webview would race the device
  // with the native sink and pop a redundant OS permission prompt. The
  // primary gate is in use-mic-uplink.ts; this is a belt-and-braces guard
  // for any future direct caller.
  if (isNativeAudio()) {
    return { stop: async () => { /* no-op */ } };
  }
  if (typeof navigator === 'undefined' || !navigator.mediaDevices?.getUserMedia) {
    throw new Error('getUserMedia not available in this environment');
  }
  const stream = await navigator.mediaDevices.getUserMedia(MIC_CONSTRAINTS);
  const context = new AudioContext({ sampleRate: 48000, latencyHint: 0.04 });

  const cleanupStream = () => {
    for (const t of stream.getTracks()) {
      try { t.stop(); } catch { /* already stopped */ }
    }
  };

  try {
    if (context.state === 'suspended') {
      try { await context.resume(); } catch { /* may resolve later */ }
    }
    await context.audioWorklet.addModule(WORKLET_URL);
    const source = context.createMediaStreamSource(stream);
    const node = new AudioWorkletNode(context, 'mic-uplink', {
      numberOfInputs: 1,
      numberOfOutputs: 0,
      channelCount: 1,
      channelCountMode: 'explicit',
      channelInterpretation: 'discrete',
    });
    node.port.onmessage = (ev: MessageEvent<{ samples?: Float32Array; peak?: number }>) => {
      const samples = ev.data?.samples;
      const peak = typeof ev.data?.peak === 'number' ? ev.data.peak : 0;
      if (samples instanceof Float32Array && samples.length === EXPECTED_BLOCK_SAMPLES) {
        onBlock(samples, peak);
      }
    };
    source.connect(node);

    return {
      stop: async () => {
        try { node.port.onmessage = null; } catch { /* ignore */ }
        try { source.disconnect(); } catch { /* ignore */ }
        try { node.disconnect(); } catch { /* ignore */ }
        cleanupStream();
        try { await context.close(); } catch { /* ignore */ }
      },
    };
  } catch (err) {
    cleanupStream();
    try { await context.close(); } catch { /* ignore */ }
    throw err;
  }
}
