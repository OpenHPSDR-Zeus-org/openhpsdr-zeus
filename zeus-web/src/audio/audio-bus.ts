// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RX audio fan-out bus. The WebSocket dispatcher decodes each 0x02
// AudioFrame exactly once and publishes it here; the bus fans that single
// stream out to every consumer — speaker playback (AudioClient) and any
// number of taps (the CW decoder, future analyzers) — so we never decode
// or stream the same audio twice.
//
// Subscribers run synchronously on the WS message thread, at the RX frame
// cadence (~46 Hz). Keep callbacks cheap: copy what you need and hand heavy
// work to a worker. The decoded frame's `samples` is, on the aligned fast
// path, a view onto the WebSocket receive buffer (see audio/frame.ts) — a
// subscriber that transfers it to a worker MUST copy first (`samples.slice()`)
// or it will detach the buffer out from under the other subscribers.

import type { DecodedAudioFrame } from './frame';

export type AudioFrameSubscriber = (frame: DecodedAudioFrame) => void;

class AudioBus {
  private readonly subscribers = new Set<AudioFrameSubscriber>();

  /** Register a consumer. Returns an unsubscribe function. */
  subscribe(fn: AudioFrameSubscriber): () => void {
    this.subscribers.add(fn);
    return () => {
      this.subscribers.delete(fn);
    };
  }

  /** Fan a decoded frame out to all consumers. Never throws — a faulty
   *  subscriber can't take down audio playback or the WS dispatcher. */
  publish(frame: DecodedAudioFrame): void {
    for (const fn of this.subscribers) {
      try {
        fn(frame);
      } catch (err) {
        // Swallow: one bad consumer must not starve the others or the
        // realtime dispatcher. Log once-ish via console (cheap at 46 Hz
        // only if a subscriber is persistently throwing, which is a bug).
        console.error('[audio-bus] subscriber threw', err);
      }
    }
  }
}

let bus: AudioBus | null = null;

export function getAudioBus(): AudioBus {
  if (!bus) bus = new AudioBus();
  return bus;
}
