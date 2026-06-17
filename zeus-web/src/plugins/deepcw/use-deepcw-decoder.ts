// SPDX-License-Identifier: GPL-2.0-or-later
//
// Drives the DeepCW decoder while the panel is active: taps the RX audio
// bus, keeps a rolling source-rate window, and every ~second resamples it to
// the model rate and asks the worker to decode. Also raises the desktop
// on-demand audio request so the decoder is fed even when the host plays
// audio natively (desktop/Photino mode sends no 0x02 frames otherwise).

import { useEffect, useRef } from 'react';
import { getAudioBus } from '../../audio/audio-bus';
import { sendAudioStreamRequest } from '../../realtime/ws-client';
import { RollingAudioBuffer, resampleLinear } from './audio-pipeline';
import {
  decode,
  loadDecoder,
  onDecoderError,
  onDecoderReady,
} from './decoder-client';
import { useDeepCwStore } from './deepcw-store';

// Don't decode until we have at least this much real audio (engine floor is
// 5 s) — avoids emitting noise from a mostly-silent startup window.
const MIN_DECODE_SECONDS = 5;
// Re-decode cadence. The whole window is re-decoded each pass, so this is the
// transcript refresh rate, not a per-character rate.
const DECODE_INTERVAL_MS = 800;

/** Deinterleave to mono channel 0 (RX audio is mono today, but be safe). */
function toMono(frameSamples: Float32Array, channels: number): Float32Array {
  if (channels <= 1) return frameSamples;
  const n = Math.floor(frameSamples.length / channels);
  const mono = new Float32Array(n);
  for (let i = 0; i < n; i += 1) mono[i] = frameSamples[i * channels]!;
  return mono;
}

/**
 * Stitch the current window decode onto the previous one. Each pass decodes
 * an overlapping sliding window, so consecutive decodes share most of their
 * text: the previous window's *suffix* equals the current window's *prefix*
 * (the window advanced by the audio decoded since last pass). We return only
 * the genuinely-new trailing characters so the transcript grows like a
 * teleprinter instead of being rewritten each pass. Longest overlap wins; if
 * there's no overlap (a gap, or a re-anchor after CLEAR) the whole window is
 * treated as new.
 */
function newTail(prev: string, cur: string): string {
  if (!prev) return cur;
  const max = Math.min(prev.length, cur.length);
  for (let l = max; l > 0; l -= 1) {
    if (prev.slice(prev.length - l) === cur.slice(0, l)) return cur.slice(l);
  }
  return cur;
}

export function useDeepCwDecoder(): void {
  const state = useDeepCwStore((s) => s.state);
  const windowSeconds = useDeepCwStore((s) => s.windowSeconds);
  const active = state !== 'idle';

  // Mutable refs so the bus callback and decode loop share state without
  // re-subscribing on every render.
  const bufferRef = useRef<RollingAudioBuffer | null>(null);
  const sourceRateRef = useRef(48000);
  const modelRateRef = useRef(0); // 0 until the worker reports 'ready'
  // Previous window decode (for overlap stitching) + the last CLEAR anchor we
  // acted on (so CLEAR re-anchors instead of appending against stale text).
  const prevWindowRef = useRef<string | null>(null);
  const seenAnchorRef = useRef(0);

  useEffect(() => {
    if (!active) return;

    const store = useDeepCwStore.getState();
    let cancelled = false;
    let busy = false;
    let lastDecodedVersion = -1;
    // Re-anchor stitching whenever the decoder (re)starts.
    prevWindowRef.current = null;
    seenAnchorRef.current = store.anchorSeq;

    loadDecoder();
    const offReady = onDecoderReady((sampleRate) => {
      modelRateRef.current = sampleRate;
      store.setModelLoaded(true);
      store.setLoadError(null);
    });
    const offError = onDecoderError((message) => {
      store.setLoadError(message);
    });

    // Ask the server to stream RX audio to us (no-op/ignored in web mode
    // where it already flows for playback; load-bearing in desktop mode).
    sendAudioStreamRequest(true);

    const unsubscribe = getAudioBus().subscribe((frame) => {
      const srcRate = frame.sampleRateHz || 48000;
      let buf = bufferRef.current;
      if (!buf || sourceRateRef.current !== srcRate) {
        sourceRateRef.current = srcRate;
        buf = new RollingAudioBuffer(Math.round(windowSeconds * srcRate));
        bufferRef.current = buf;
      }
      buf.push(toMono(frame.samples, frame.channels));
    });

    const tick = async () => {
      const buf = bufferRef.current;
      const modelRate = modelRateRef.current;
      if (
        !cancelled &&
        !busy &&
        buf &&
        modelRate > 0 &&
        buf.version !== lastDecodedVersion &&
        buf.secondsFilled(sourceRateRef.current) >= MIN_DECODE_SECONDS
      ) {
        busy = true;
        lastDecodedVersion = buf.version;
        store.setDecoding(true);
        try {
          const modelSamples = resampleLinear(
            buf.samples,
            sourceRateRef.current,
            modelRate,
          );
          const cur = await decode(modelSamples);
          if (!cancelled) {
            const fresh = useDeepCwStore.getState();
            if (fresh.anchorSeq !== seenAnchorRef.current) {
              // CLEAR fired during this decode — re-anchor, drop this window
              // so we don't re-dump the just-cleared text.
              seenAnchorRef.current = fresh.anchorSeq;
              prevWindowRef.current = cur;
            } else {
              fresh.appendDecoded(newTail(prevWindowRef.current ?? '', cur));
              prevWindowRef.current = cur;
            }
          }
        } catch {
          // decoder-client surfaces fatal errors via onDecoderError; a
          // single failed decode is non-fatal — try again next tick.
        } finally {
          busy = false;
          if (!cancelled) store.setDecoding(false);
        }
      }
    };
    const timer = window.setInterval(() => void tick(), DECODE_INTERVAL_MS);

    return () => {
      cancelled = true;
      window.clearInterval(timer);
      unsubscribe();
      offReady();
      offError();
      sendAudioStreamRequest(false);
      bufferRef.current?.reset();
      bufferRef.current = null;
      useDeepCwStore.getState().setDecoding(false);
    };
    // windowSeconds re-runs the effect so the rolling buffer is rebuilt at the
    // new length (cleanup nulls it; the next bus frame allocates the new size).
  }, [active, windowSeconds]);
}
