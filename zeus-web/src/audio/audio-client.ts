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

import type { DecodedAudioFrame } from './frame';

// createBuffer + scheduled-playback model ported from ProjectLongBanana
// commit 4acc255b, herpes-client audioPlayer.ts. Drops the AudioWorklet +
// ring buffer + linear resampler in favor of one scheduled BufferSource per
// frame at a fixed 48 kHz context — simpler, and confirmed to produce clean
// audio on HL2 in the reference implementation.

export type AudioClientState =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'playing' }
  | { kind: 'error'; message: string };

export type AudioStats = {
  available: number;
  underrunCount: number;
  droppedSamples: number;
};

type Listener = (state: AudioClientState, stats: AudioStats | null) => void;

// 100 ms re-anchor floor. The 50 ms experiment (commit 503e0d2) was right
// at the p99 LAN inter-arrival edge — any tail past p99 caused audible
// underrun on RX, regardless of server-side pipeline. Back to 100 ms; the
// TX→RX gap is slightly more perceptible than at 50 ms but RX is clean.
// Future work could adapt the floor from observed jitter stats instead.
const BUFFER_TARGET_SECS = 0.1;
const BUFFER_MAX_SECS = 0.5;
const STATS_INTERVAL_MS = 500;

class AudioClient {
  private context: AudioContext | null = null;
  private gain: GainNode | null = null;
  private nextPlayTime = 0;
  private pending = new Set<AudioBufferSourceNode>();
  private state: AudioClientState = { kind: 'idle' };
  private stats: AudioStats | null = null;
  private underruns = 0;
  private dropped = 0;
  private listeners = new Set<Listener>();
  private starting: Promise<void> | null = null;
  private statsTimer: ReturnType<typeof setInterval> | null = null;

  get currentState(): AudioClientState { return this.state; }
  get currentStats(): AudioStats | null { return this.stats; }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    listener(this.state, this.stats);
    return () => { this.listeners.delete(listener); };
  }

  async start(): Promise<void> {
    if (this.state.kind === 'playing') return;
    if (this.starting) return this.starting;
    this.starting = this.doStart().finally(() => { this.starting = null; });
    return this.starting;
  }

  private async doStart() {
    this.setState({ kind: 'loading' });
    try {
      const ctx = new AudioContext({ sampleRate: 48000 });
      const gain = ctx.createGain();
      gain.gain.value = 1.0;
      gain.connect(ctx.destination);
      if (ctx.state === 'suspended') await ctx.resume();
      this.context = ctx;
      this.gain = gain;
      this.nextPlayTime = 0;
      this.underruns = 0;
      this.dropped = 0;
      this.stats = { available: 0, underrunCount: 0, droppedSamples: 0 };
      this.statsTimer = setInterval(() => this.emitStats(), STATS_INTERVAL_MS);
      this.setState({ kind: 'playing' });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      this.setState({ kind: 'error', message });
    }
  }

  // Stop any sources scheduled for the old demod mode so USB↔LSB flips take
  // effect in near-real time. Paired with the server-side flush in
  // WdspDspEngine.SetMode (commit 88ecdc2).
  reset(): void {
    for (const src of this.pending) {
      try { src.stop(); } catch { /* already finished */ }
      try { src.disconnect(); } catch { /* ignore */ }
    }
    this.pending.clear();
    this.nextPlayTime = 0;
  }

  async stop(): Promise<void> {
    const ctx = this.context;
    this.reset();
    this.context = null;
    this.gain = null;
    if (this.statsTimer != null) {
      clearInterval(this.statsTimer);
      this.statsTimer = null;
    }
    if (ctx) {
      try { await ctx.close(); } catch { /* ignore */ }
    }
    this.stats = null;
    this.setState({ kind: 'idle' });
  }

  push(frame: DecodedAudioFrame) {
    const ctx = this.context;
    const gain = this.gain;
    if (!ctx || !gain) return;
    if (ctx.state === 'suspended') {
      // Chrome can auto-suspend a context on tab-backgrounding, focus loss, or
      // around getUserMedia prompts. A silent-drop here made RX audio go dead
      // after a MOX cycle: the mic prompt briefly suspended our output ctx and
      // nothing ever woke it. Fire-and-forget resume; this frame still drops,
      // but the next one (20 ms later) lands once the context is running.
      void ctx.resume().catch(() => { /* next tick will retry */ });
      return;
    }
    if (ctx.state !== 'running') return;

    const now = ctx.currentTime;

    // Drop if we've already scheduled more than the max ahead — prevents
    // unbounded drift when the producer is faster than real time.
    if (this.nextPlayTime > now + BUFFER_MAX_SECS) {
      this.dropped += frame.sampleCount;
      return;
    }

    // If we've fallen behind (or this is the first frame after start/reset),
    // re-anchor the schedule one target interval in the future.
    if (this.nextPlayTime < now + BUFFER_TARGET_SECS * 0.5) {
      if (this.nextPlayTime !== 0) this.underruns++;
      this.nextPlayTime = now + BUFFER_TARGET_SECS;
    }

    const buffer = ctx.createBuffer(1, frame.sampleCount, frame.sampleRateHz);
    // copyToChannel reads our floats into the buffer's own storage, so we can
    // pass frame.samples directly — the previous `new Float32Array(frame.samples)`
    // wrap copied the data twice (DOM + extra heap alloc) at 30 Hz. The cast
    // satisfies lib.dom.d.ts's `Float32Array<ArrayBuffer>` constraint; the value
    // is already that shape because `decodeAudioFrame` constructs it from an
    // ArrayBuffer view in `frame.ts`.
    buffer.copyToChannel(frame.samples as Float32Array<ArrayBuffer>, 0);

    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.connect(gain);
    source.onended = () => {
      this.pending.delete(source);
      try { source.disconnect(); } catch { /* ignore */ }
    };
    this.pending.add(source);
    source.start(this.nextPlayTime);
    // PERF_PASS_3_DEBUG: t4 — first audio frame after MOX-off. Uncommitted.
    {
      const w = window as unknown as {
        __zeusFirstAudioAfterMox?: boolean;
        __zeusPerf3?: {
          captures: Array<{
            cycle: number;
            t0_mox_off: number;
            t4_audio_scheduled?: number;
            nextPlayTime?: number;
            now?: number;
            delta_ms?: number;
          }>;
        };
      };
      if (w.__zeusFirstAudioAfterMox) {
        const delta_ms = (this.nextPlayTime - now) * 1000;
        console.log(
          'audio.scheduled',
          performance.now(),
          'nextPlayTime=', this.nextPlayTime,
          'now=', now,
          'delta_ms=', delta_ms,
        );
        const arr = w.__zeusPerf3?.captures;
        if (arr && arr.length > 0) {
          const last = arr[arr.length - 1];
          if (last && last.t4_audio_scheduled === undefined) {
            last.t4_audio_scheduled = performance.now();
            last.nextPlayTime = this.nextPlayTime;
            last.now = now;
            last.delta_ms = delta_ms;
          }
        }
        w.__zeusFirstAudioAfterMox = false;
      }
    }

    this.nextPlayTime += frame.sampleCount / frame.sampleRateHz;
  }

  private emitStats() {
    const ctx = this.context;
    if (!ctx) return;
    const ahead = Math.max(0, this.nextPlayTime - ctx.currentTime);
    this.stats = {
      available: Math.round(ahead * ctx.sampleRate),
      underrunCount: this.underruns,
      droppedSamples: this.dropped,
    };
    this.emit();
  }

  private setState(next: AudioClientState) {
    this.state = next;
    this.emit();
  }

  private emit() {
    for (const l of this.listeners) l(this.state, this.stats);
  }
}

let singleton: AudioClient | null = null;

export function getAudioClient(): AudioClient {
  if (!singleton) singleton = new AudioClient();
  return singleton;
}
