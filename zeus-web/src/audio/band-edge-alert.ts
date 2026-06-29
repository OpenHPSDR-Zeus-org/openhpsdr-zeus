// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

// Issue #846: short two-tone beep played through the operator's speakers when
// the VFO tunes into a band-plan segment that doesn't match their licence /
// mode. Mirrors the audible band-edge alarm Icoms ship — a low-effort cue that
// you've drifted out of your privileges, separate from (and in addition to) the
// existing MOX-block guard.
//
// The tone synth is fully local to the browser. It does NOT touch the RX
// audio pipeline (AudioClient / audio-bus) so it can never interfere with what
// the operator is listening to. A dedicated AudioContext is lazily created on
// first call so we don't burn an output device slot until the alert actually
// fires.

let ctx: AudioContext | null = null;
let lastPlayAt = 0;

// Don't retrigger too rapidly — a sweep across a couple of narrow gap
// segments could otherwise produce a stutter of beeps. 600 ms matches a
// comfortable "you crossed a line, now you're crossing another" cadence.
const MIN_INTERVAL_MS = 600;

function getCtx(): AudioContext | null {
  if (ctx) return ctx;
  const Ctor =
    typeof window === 'undefined'
      ? undefined
      : (window.AudioContext ??
          (window as unknown as { webkitAudioContext?: typeof AudioContext })
            .webkitAudioContext);
  if (!Ctor) return null;
  try {
    ctx = new Ctor();
  } catch {
    ctx = null;
  }
  return ctx;
}

/**
 * Play a short two-tone beep (440 Hz → 880 Hz, ~180 ms total).
 *
 * Volume is the peak gain on a 0..1 scale. 0.25 is comfortable on typical
 * desktop speakers without startling the operator. The call is throttled to
 * one beep per MIN_INTERVAL_MS so a fast VFO sweep doesn't stutter.
 *
 * Returns `false` when Web Audio isn't available (very old browsers) or when
 * the throttle gate suppresses the call.
 */
export function playBandEdgeAlert(volume = 0.25): boolean {
  const now = performance.now();
  if (now - lastPlayAt < MIN_INTERVAL_MS) return false;
  const audio = getCtx();
  if (!audio) return false;
  // Browsers auto-suspend a context that was created before the first user
  // gesture; resume() is a no-op if already running.
  if (audio.state === 'suspended') {
    audio.resume().catch(() => {});
  }
  lastPlayAt = now;
  const peak = Math.max(0, Math.min(1, volume));
  const t0 = audio.currentTime;
  const beepLenSec = 0.09;
  const gap = 0.005;
  playTone(audio, 440, t0, beepLenSec, peak);
  playTone(audio, 880, t0 + beepLenSec + gap, beepLenSec, peak);
  return true;
}

function playTone(
  audio: AudioContext,
  freqHz: number,
  startSec: number,
  durSec: number,
  peak: number,
): void {
  const osc = audio.createOscillator();
  const gain = audio.createGain();
  osc.type = 'sine';
  osc.frequency.value = freqHz;
  // Quick attack/decay envelope so the tone doesn't click on edges.
  gain.gain.setValueAtTime(0, startSec);
  gain.gain.linearRampToValueAtTime(peak, startSec + 0.005);
  gain.gain.setValueAtTime(peak, startSec + durSec - 0.02);
  gain.gain.linearRampToValueAtTime(0, startSec + durSec);
  osc.connect(gain).connect(audio.destination);
  osc.start(startSec);
  osc.stop(startSec + durSec + 0.01);
}
