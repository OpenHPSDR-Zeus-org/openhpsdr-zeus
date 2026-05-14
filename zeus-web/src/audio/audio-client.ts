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
  // #299 Step 2 frontend probe — attributed underrun causes.
  // latePush = (now - lastPushWallTime) > 90 ms when underrun fired → the
  //   WS-message delivery / main-thread starvation path is the culprit.
  // latenessVsSchedule = push arrived on time but nextPlayTime had already
  //   drifted within 50 ms of now → audio render thread itself was preempted
  //   (less common; means OS/browser-side, not main-thread, is the issue).
  latePushCount: number;
  latenessVsScheduleCount: number;
};

type Listener = (state: AudioClientState, stats: AudioStats | null) => void;

// 300 ms re-anchor floor. The 100 ms baseline (and earlier 50 ms experiment,
// commit 503e0d2) underflowed in two distinct cases the #299 Step 2 probe
// surfaced:
//   1. OBS streaming load preempted the audio render thread → registered as
//      `latenessVsSchedule`. Addressed primarily by `latencyHint: 'playback'`
//      below (larger internal render buffers) but the headroom helps too.
//   2. A heavy-WebAudio companion tab (e.g. kiwisdr.com) occasionally
//      starved the Zeus tab's main thread for ~200-220 ms — registered as
//      `latePush`. The 200 ms intermediate floor still tripped on a measured
//      215 ms gap during idle with a kiwisdr tab open; 300 ms is comfortably
//      above the observed worst case with 85 ms of margin.
// Operator-perceptible TX→RX gap rises 200 ms vs the 100 ms baseline, still
// well under monitoring-latency expectations. A follow-up could swap this for
// an adaptive scheme (200 ms normal, bump on underrun, decay back).
const BUFFER_TARGET_SECS = 0.3;
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
  // #299 Step 2 frontend probe — see AudioStats type for semantics.
  private lastPushPerfTime = 0;
  private latePushCount = 0;
  private latenessVsScheduleCount = 0;
  private lastLoggedLate = 0;
  private lastLoggedSched = 0;
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
      // #299 fix — latencyHint: 'playback' tells the browser this is a media
      // playback context (not interactive UI feedback), so it can allocate
      // larger internal render-thread buffers. The audio render thread becomes
      // much more tolerant of OS-level preemption (the dominant underrun cause
      // surfaced by the Step 2 probe: latenessVsSchedule >> latePush under OBS
      // streaming load). Costs a few extra ms of intrinsic latency, but the
      // operator-perceptible gap is dominated by BUFFER_TARGET_SECS anyway.
      const ctx = new AudioContext({ sampleRate: 48000, latencyHint: 'playback' });
      const gain = ctx.createGain();
      gain.gain.value = 1.0;
      gain.connect(ctx.destination);
      if (ctx.state === 'suspended') await ctx.resume();
      this.context = ctx;
      this.gain = gain;
      this.nextPlayTime = 0;
      this.underruns = 0;
      this.dropped = 0;
      this.lastPushPerfTime = 0;
      this.latePushCount = 0;
      this.latenessVsScheduleCount = 0;
      this.lastLoggedLate = 0;
      this.lastLoggedSched = 0;
      this.stats = { available: 0, underrunCount: 0, droppedSamples: 0, latePushCount: 0, latenessVsScheduleCount: 0 };
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

    // #299 Step 2 probe — wall-clock gap since previous push, used below
    // to attribute the cause of any underrun the schedule re-anchor catches.
    const pushPerfMs = performance.now();
    const dtSinceLastPushMs = this.lastPushPerfTime === 0 ? 0 : pushPerfMs - this.lastPushPerfTime;
    this.lastPushPerfTime = pushPerfMs;

    // Drop if we've already scheduled more than the max ahead — prevents
    // unbounded drift when the producer is faster than real time.
    if (this.nextPlayTime > now + BUFFER_MAX_SECS) {
      this.dropped += frame.sampleCount;
      return;
    }

    // If we've fallen behind (or this is the first frame after start/reset),
    // re-anchor the schedule one target interval in the future.
    if (this.nextPlayTime < now + BUFFER_TARGET_SECS * 0.5) {
      if (this.nextPlayTime !== 0) {
        this.underruns++;
        // #299 Step 2 probe — attribute the underrun. 90 ms threshold matches
        // BUFFER_TARGET_SECS * 0.9 with a small margin; at 50 Hz audio frame
        // rate (one ~20 ms frame per push) the expected dt is 20 ms, so >90 ms
        // strongly indicates main-thread / WS delivery starvation.
        if (dtSinceLastPushMs > 90) {
          this.latePushCount++;
          console.warn(
            `audio.underrun.latePush dtMs=${dtSinceLastPushMs.toFixed(1)} totalLate=${this.latePushCount}`,
          );
        } else {
          this.latenessVsScheduleCount++;
          const schedAheadMs = (this.nextPlayTime - now) * 1000;
          console.warn(
            `audio.underrun.latenessVsSchedule dtMs=${dtSinceLastPushMs.toFixed(1)} schedAheadMs=${schedAheadMs.toFixed(1)} totalSched=${this.latenessVsScheduleCount}`,
          );
        }
      }
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
      latePushCount: this.latePushCount,
      latenessVsScheduleCount: this.latenessVsScheduleCount,
    };
    // #299 Step 2 probe — once-per-window roll-up so the user has a readable
    // summary without parsing every per-event console.warn. Only fires when
    // something new happened in this 500 ms window.
    const dLate = this.latePushCount - this.lastLoggedLate;
    const dSched = this.latenessVsScheduleCount - this.lastLoggedSched;
    if (dLate > 0 || dSched > 0) {
      console.log(
        `audio.probe latePush=${this.latePushCount} (+${dLate}) latenessVsSchedule=${this.latenessVsScheduleCount} (+${dSched}) totalUnderruns=${this.underruns}`,
      );
      this.lastLoggedLate = this.latePushCount;
      this.lastLoggedSched = this.latenessVsScheduleCount;
    }
    // Expose latest probe values on window for post-test snapshot inspection.
    (window as unknown as { __zeusAudioProbe?: object }).__zeusAudioProbe = {
      latePushCount: this.latePushCount,
      latenessVsScheduleCount: this.latenessVsScheduleCount,
      totalUnderruns: this.underruns,
      droppedSamples: this.dropped,
      availableSamples: this.stats.available,
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
