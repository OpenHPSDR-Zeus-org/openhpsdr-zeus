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

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { _resetFrameConsumerCount, registerFrameConsumer } from '../state/display-store';
import {
  MSG_TYPE_DISPLAY_STREAM_REQUEST,
  MSG_TYPE_MIC_PCM,
  MSG_TYPE_RX_AUDIO_MASTER_BYPASS,
  sendMicPcm,
} from './ws-client';

// sendMicPcm reads a module-scoped `activeWs` that's only set by startRealtime.
// For tests we stub the WebSocket constructor so we can capture what was sent.

class MockWebSocket {
  static OPEN = 1;
  static instances: MockWebSocket[] = [];
  readyState = MockWebSocket.OPEN;
  sent: ArrayBuffer[] = [];
  binaryType = 'arraybuffer';
  // ws-client assigns these callbacks; we keep them harmless.
  onopen: ((ev: unknown) => void) | null = null;
  onclose: ((ev: unknown) => void) | null = null;
  onerror: ((ev: unknown) => void) | null = null;
  onmessage: ((ev: unknown) => void) | null = null;
  constructor() {
    MockWebSocket.instances.push(this);
  }
  send(data: ArrayBuffer) {
    this.sent.push(data);
  }
  close() { /* no-op */ }
}

describe('sendMicPcm', () => {
  let origWs: typeof WebSocket;

  beforeEach(() => {
    MockWebSocket.instances.length = 0;
    origWs = globalThis.WebSocket;
    // @ts-expect-error — structural match is fine for the surface we use.
    globalThis.WebSocket = MockWebSocket;
  });

  afterEach(() => {
    _resetFrameConsumerCount();
    globalThis.WebSocket = origWs;
    vi.restoreAllMocks();
  });

  it('no-ops when no active WS (does not throw)', () => {
    const samples = new Float32Array(960);
    expect(() => sendMicPcm(samples)).not.toThrow();
  });

  it('wire format is [0x20] + 960 × f32le when WS is open', async () => {
    // startRealtime is the only path that sets the module-internal activeWs.
    const { startRealtime } = await import('./ws-client');
    const stop = startRealtime('/ws');
    try {
      const ws = MockWebSocket.instances[0];
      expect(ws).toBeDefined();
      if (ws?.onopen) ws.onopen({} as unknown);
      ws!.sent.length = 0;

      const samples = new Float32Array(960);
      // Distinguishable pattern so we can verify LE ordering in the wire buffer.
      for (let i = 0; i < 960; i++) samples[i] = i * 0.001;

      sendMicPcm(samples);

      expect(ws?.sent.length).toBe(1);
      const sentBuf = ws!.sent[0];
      if (!(sentBuf instanceof ArrayBuffer)) throw new Error('expected ArrayBuffer');
      expect(sentBuf.byteLength).toBe(1 + 960 * 4);
      const view = new DataView(sentBuf);
      expect(view.getUint8(0)).toBe(MSG_TYPE_MIC_PCM);
      for (let i = 0; i < 960; i++) {
        expect(view.getFloat32(1 + i * 4, true)).toBeCloseTo(i * 0.001, 6);
      }
    } finally {
      stop();
    }
  });

  it('sanitizes non-finite and overrange samples before serializing', async () => {
    const { startRealtime } = await import('./ws-client');
    const stop = startRealtime('/ws');
    try {
      const ws = MockWebSocket.instances[0];
      if (ws?.onopen) ws.onopen({} as unknown);
      ws!.sent.length = 0;

      const samples = new Float32Array(960);
      samples[0] = Number.NaN;
      samples[1] = Number.POSITIVE_INFINITY;
      samples[2] = Number.NEGATIVE_INFINITY;
      samples[3] = 1.5;
      samples[4] = -2;
      samples[5] = 0.25;

      sendMicPcm(samples);

      expect(ws?.sent.length).toBe(1);
      const sentBuf = ws!.sent[0];
      if (!(sentBuf instanceof ArrayBuffer)) throw new Error('expected ArrayBuffer');
      const view = new DataView(sentBuf);
      expect(view.getUint8(0)).toBe(MSG_TYPE_MIC_PCM);
      expect(view.getFloat32(1, true)).toBe(0);
      expect(view.getFloat32(5, true)).toBe(0);
      expect(view.getFloat32(9, true)).toBe(0);
      expect(view.getFloat32(13, true)).toBe(1);
      expect(view.getFloat32(17, true)).toBe(-1);
      expect(view.getFloat32(21, true)).toBeCloseTo(0.25, 6);
    } finally {
      stop();
    }
  });

  it('drops blocks with wrong length', async () => {
    const { startRealtime } = await import('./ws-client');
    const stop = startRealtime('/ws');
    try {
      const ws = MockWebSocket.instances[0];
      if (ws?.onopen) ws.onopen({} as unknown);
      ws!.sent.length = 0;
      sendMicPcm(new Float32Array(128));
      expect(ws?.sent.length).toBe(0);
    } finally {
      stop();
    }
  });

  it('requests the display stream when a frame consumer is already mounted', async () => {
    const { startRealtime } = await import('./ws-client');
    const release = registerFrameConsumer();
    const stop = startRealtime('/ws');
    try {
      const ws = MockWebSocket.instances[0];
      expect(ws).toBeDefined();
      if (ws?.onopen) ws.onopen({} as unknown);

      expect(ws?.sent.length).toBe(1);
      const sentBuf = ws!.sent[0];
      if (!(sentBuf instanceof ArrayBuffer)) throw new Error('expected ArrayBuffer');
      const view = new DataView(sentBuf);
      expect(view.getUint8(0)).toBe(MSG_TYPE_DISPLAY_STREAM_REQUEST);
      expect(view.getUint8(1)).toBe(1);
    } finally {
      stop();
      release();
    }
  });

  it('updates the display stream request as frame consumers mount and unmount', async () => {
    const { startRealtime } = await import('./ws-client');
    const stop = startRealtime('/ws');
    try {
      const ws = MockWebSocket.instances[0];
      expect(ws).toBeDefined();
      if (ws?.onopen) ws.onopen({} as unknown);
      ws!.sent.length = 0;

      const release = registerFrameConsumer();
      expect(ws?.sent.length).toBe(1);
      let sentBuf = ws!.sent[0];
      if (!(sentBuf instanceof ArrayBuffer)) throw new Error('expected ArrayBuffer');
      let view = new DataView(sentBuf);
      expect(view.getUint8(0)).toBe(MSG_TYPE_DISPLAY_STREAM_REQUEST);
      expect(view.getUint8(1)).toBe(1);

      release();
      expect(ws?.sent.length).toBe(2);
      sentBuf = ws!.sent[1];
      if (!(sentBuf instanceof ArrayBuffer)) throw new Error('expected ArrayBuffer');
      view = new DataView(sentBuf);
      expect(view.getUint8(0)).toBe(MSG_TYPE_DISPLAY_STREAM_REQUEST);
      expect(view.getUint8(1)).toBe(0);
    } finally {
      stop();
    }
  });

  it('applies RX audio-suite master-bypass websocket frames', async () => {
    const { startRealtime } = await import('./ws-client');
    const { useAudioSuiteStore } = await import('../state/audio-suite-store');
    useAudioSuiteStore.setState({
      masterBypassed: false,
      rxMasterBypassed: false,
    });

    const stop = startRealtime('/ws');
    try {
      const ws = MockWebSocket.instances[0];
      expect(ws).toBeDefined();
      if (ws?.onopen) ws.onopen({} as unknown);

      const frame = new ArrayBuffer(2);
      const view = new DataView(frame);
      view.setUint8(0, MSG_TYPE_RX_AUDIO_MASTER_BYPASS);
      view.setUint8(1, 1);
      ws?.onmessage?.({ data: frame });

      expect(useAudioSuiteStore.getState().masterBypassed).toBe(false);
      expect(useAudioSuiteStore.getState().rxMasterBypassed).toBe(true);
    } finally {
      stop();
    }
  });
});
