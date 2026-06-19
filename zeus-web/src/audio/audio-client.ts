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
import { isNativeAudio } from './host-mode';
import { useAudioDeviceStore } from '../state/audio-device-store';

// createBuffer + scheduled-playback model ported from ProjectLongBanana
// commit 4acc255b, herpes-client audioPlayer.ts. Drops the AudioWorklet +
// ring buffer + linear resampler in favor of one scheduled BufferSource per
// frame at a fixed 48 kHz context — simpler, and confirmed to produce clean
// audio on HL2 in the reference implementation.

export type AudioClientState =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'playing' }
  // `'native'` means the host process owns the audio device directly
  // (desktop mode, miniaudio sink) so the browser is not allowed to open
  // an AudioContext. UI surfaces show a passive "native (default device)"
  // indicator instead of the mute toggle in this state.
  | { kind: 'native' }
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

export type AudioPlaybackDiagnosticsSnapshot = {
  playbackState: AudioClientState['kind'];
  contextState: string | null;
  bufferedSamples: number;
  bufferedMs: number | null;
  sampleRateHz: number;
  contextSampleRateHz: number;
  baseLatencyMs: number | null;
  outputLatencyMs: number | null;
  underrunCount: number;
  droppedSamples: number;
  latePushCount: number;
  latenessVsScheduleCount: number;
  pendingSources: number;
  bufferTargetMs: number;
  bufferMaxMs: number;
  errorMessage: string | null;
};

type Listener = (state: AudioClientState, stats: AudioStats | null) => void;

type SinkSelectableAudioContext = AudioContext & {
  setSinkId?: (sinkId: string) => Promise<void>;
};

// Adaptive re-anchor target. A fixed 300 ms floor (the previous value) made the
// waterfall lead the audio by ~0.3 s — perceptible as display/audio lag. Instead
// of one conservative constant we run the self-correcting scheme the old comment
// proposed: start LOW for a realtime feel, BUMP up on every underrun, and DECAY
// slowly back toward the floor during clean playback. Worst case it climbs to
// BUFFER_TARGET_MAX_SECS — as robust as the old fixed 300 ms — but in good
// conditions it settles near BUFFER_TARGET_MIN_SECS.
//
// History the floor must survive (see #299 Step 2 probe): OBS streaming
// preempting the render thread (mitigated by `latencyHint: 'playback'` below),
// and a heavy-WebAudio companion tab (kiwisdr.com) starving the main thread for
// ~200-220 ms. The bump-on-underrun path absorbs those without a permanent tax.
const BUFFER_TARGET_START_SECS = 0.14;
const BUFFER_TARGET_MIN_SECS = 0.10;
const BUFFER_TARGET_MAX_SECS = 0.35;
// Step added to the target on each underrun, and removed per clean stats tick.
const BUFFER_UNDERRUN_BUMP_SECS = 0.06;
const BUFFER_DECAY_SECS_PER_TICK = 0.01;
// Drop frames scheduled further ahead than this to bound producer-faster-than
// -realtime drift; kept above the max target so it never fights the re-anchor.
const BUFFER_MAX_SECS = 0.6;
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
  // Adaptive jitter-buffer target (seconds). Bumped on underrun, decayed toward
  // the floor during clean playback — see the BUFFER_* constants.
  private targetSecs = BUFFER_TARGET_START_SECS;
  // Underrun count at the last stats tick; lets emitStats() decay only when no
  // new underrun happened in the window.
  private underrunsAtLastTick = 0;
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

  diagnosticsSnapshot(): AudioPlaybackDiagnosticsSnapshot {
    const ctx = this.context;
    const sampleRate = ctx?.sampleRate ?? 0;
    const bufferedSamples = this.stats?.available ?? 0;
    const bufferedMs = sampleRate > 0 ? Math.round(bufferedSamples * 1000 / sampleRate) : null;
    const contextWithOutputLatency = ctx as (AudioContext & { outputLatency?: number }) | null;
    return {
      playbackState: this.state.kind,
      contextState: ctx?.state ?? null,
      bufferedSamples,
      bufferedMs,
      sampleRateHz: sampleRate,
      contextSampleRateHz: sampleRate,
      baseLatencyMs: typeof ctx?.baseLatency === 'number' ? Math.round(ctx.baseLatency * 1000) : null,
      outputLatencyMs: typeof contextWithOutputLatency?.outputLatency === 'number'
        ? Math.round(contextWithOutputLatency.outputLatency * 1000)
        : null,
      underrunCount: this.underruns,
      droppedSamples: this.dropped,
      latePushCount: this.latePushCount,
      latenessVsScheduleCount: this.latenessVsScheduleCount,
      pendingSources: this.pending.size,
      bufferTargetMs: this.targetSecs * 1000,
      bufferMaxMs: BUFFER_MAX_SECS * 1000,
      errorMessage: this.state.kind === 'error' ? this.state.message : null,
    };
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    listener(this.state, this.stats);
    return () => { this.listeners.delete(listener); };
  }

  async start(): Promise<void> {
    // Phase 2c — desktop mode opt-out. The host process plays RX audio
    // through its native sink, so we must not create an AudioContext here
    // (a second consumer of the same /api/rx audio source would double-up
    // and fight for the device). Surface `'native'` so AudioToggle can
    // render the passive indicator and return.
    if (isNativeAudio()) {
      this.setState({ kind: 'native' });
      return;
    }
    if (this.state.kind === 'playing') return;
    if (this.starting) return this.starting;
    this.starting = this.doStart().finally(() => { this.starting = null; });
    return this.starting;
  }

  async setOutputDevice(deviceId = useAudioDeviceStore.getState().browserOutputDeviceId): Promise<void> {
    const ctx = this.context;
    if (!ctx) return;
    await this.applyOutputDevice(ctx, deviceId, true);
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
      await this.applyOutputDevice(ctx, useAudioDeviceStore.getState().browserOutputDeviceId, false);
      const gain = ctx.createGain();
      gain.gain.value = 1.0;
      gain.connect(ctx.destination);
      if (ctx.state === 'suspended') await ctx.resume();
      this.context = ctx;
      this.gain = gain;
      this.nextPlayTime = 0;
      this.underruns = 0;
      this.dropped = 0;
      this.targetSecs = BUFFER_TARGET_START_SECS;
      this.underrunsAtLastTick = 0;
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

  private async applyOutputDevice(
    ctx: AudioContext,
    deviceId: string,
    strict: boolean,
  ): Promise<void> {
    const normalized = deviceId.trim();
    const selectable = ctx as SinkSelectableAudioContext;
    if (typeof selectable.setSinkId !== 'function') {
      if (strict && normalized) {
        throw new Error('browser audio output selection is not supported');
      }
      if (normalized) {
        console.warn('audio.output.setSinkId unsupported; using browser default output');
      }
      return;
    }
    try {
      await selectable.setSinkId(normalized || '');
    } catch (err) {
      if (strict) throw err;
      console.warn('audio.output.setSinkId failed; using browser default output', err);
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
    // Defensive: in desktop mode the server should never emit 0x02 frames
    // (Phase 2b), and ws-client drops them at the dispatch layer. If one
    // sneaks through anyway, silently discard before allocating the
    // AudioBuffer the no-context fast-path below would have wasted.
    if (isNativeAudio()) return;
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

    // Re-anchor the schedule ONLY on a true underrun — i.e. when nextPlayTime
    // has fallen at/behind the clock, so the previously-queued audio has
    // actually finished and there is a real gap. The old condition re-anchored
    // whenever the buffer merely dipped below HALF the target (~70 ms); under
    // sub-target delivery jitter (e.g. a 2nd receiver's panadapter adding
    // main-thread render load — issue zeus-gdc7) that fired constantly while
    // 30–55 ms of audio was still queued and playing, and the forward jump it
    // performed INSERTED that much silence each time → the crackle. Riding the
    // buffer low is fine: contiguous scheduling (nextPlayTime += frame
    // duration) keeps audio seamless until it genuinely runs dry.
    if (this.nextPlayTime === 0) {
      // First frame after start/reset — seed the jitter cushion.
      this.nextPlayTime = now + this.targetSecs;
    } else if (this.nextPlayTime < now) {
      this.underruns++;
      // A real underrun — grow the buffer target so we carry more cushion.
      // Decays back toward the floor during clean playback (emitStats).
      this.targetSecs = Math.min(BUFFER_TARGET_MAX_SECS, this.targetSecs + BUFFER_UNDERRUN_BUMP_SECS);
      // #299 Step 2 probe — attribute the underrun. A >90 ms gap since the
      // previous push points at main-thread / WS delivery starvation; a
      // smaller gap means the render thread / OS scheduling drifted.
      if (dtSinceLastPushMs > 90) {
        this.latePushCount++;
        console.warn(
          `audio.underrun.latePush dtMs=${dtSinceLastPushMs.toFixed(1)} totalLate=${this.latePushCount}`,
        );
      } else {
        this.latenessVsScheduleCount++;
        const behindMs = (now - this.nextPlayTime) * 1000;
        console.warn(
          `audio.underrun.latenessVsSchedule dtMs=${dtSinceLastPushMs.toFixed(1)} behindMs=${behindMs.toFixed(1)} totalSched=${this.latenessVsScheduleCount}`,
        );
      }
      this.nextPlayTime = now + this.targetSecs;
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
    // Adaptive buffer decay: a clean window (no new underrun) walks the target
    // back toward the floor, so latency recovers after a transient stall instead
    // of being taxed for the rest of the session.
    if (this.underruns === this.underrunsAtLastTick) {
      this.targetSecs = Math.max(BUFFER_TARGET_MIN_SECS, this.targetSecs - BUFFER_DECAY_SECS_PER_TICK);
    }
    this.underrunsAtLastTick = this.underruns;
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
