// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { useEffect, useRef } from 'react';
import { CwDecoder } from './decoder';
import { useCwDecoderStore } from '../state/cw-decoder-store';
import { getAudioClient } from '../audio/audio-client';
import { isNativeAudio } from '../audio/host-mode';

/**
 * Hook that connects the CW decoder to the live audio stream.
 *
 * When the decoder is enabled (state !== 'idle'), this hook:
 * 1. Subscribes to AudioClient state changes
 * 2. Creates a CwDecoder instance with the target CW pitch
 * 3. For each AudioFrame received, processes samples and updates the store
 *
 * The decoder runs client-side with zero server cost.
 */
export function useCwDecoderAudio() {
  const { state, updateStats } = useCwDecoderStore();
  const decoderRef = useRef<CwDecoder | null>(null);
  const targetFreq = 600; // Default CW pitch

  useEffect(() => {
    if (state === 'idle') {
      decoderRef.current = null;
      return;
    }

    // Desktop mode: audio is rendered by the host process, not by
    // AudioContext. The decoder would need a different integration
    // (e.g., tapping the AudioFrame before it reaches AudioClient).
    if (isNativeAudio()) {
      console.warn('cw-decoder: native audio mode not supported yet');
      return;
    }

    // Create decoder instance
    const decoder = new CwDecoder({
      targetFreq,
      sampleRate: 48000, // Fixed sample rate in Zeus
    });
    decoderRef.current = decoder;

    // Subscribe to AudioClient to get audio frames
    const audioClient = getAudioClient();

    // Monkey-patch AudioClient.push to intercept frames
    // This is a pragmatic approach; a cleaner solution would involve
    // extending the AudioClient with a callback API.
    const originalPush = audioClient.push.bind(audioClient);
    audioClient.push = (frame) => {
      // First, let the normal audio play
      originalPush(frame);

      // Then decode
      if (decoderRef.current && state !== 'held') {
        const outputs = decoderRef.current.processSamples(frame.samples);

        // Update store with decoded characters
        for (const output of outputs) {
          if (output.char === ' ') {
            // Space triggers word completion
            useCwDecoderStore.getState().addToHistory('');
          } else {
            useCwDecoderStore.getState().appendChar(
              output.char,
              output.wpm,
              output.snrDb,
              output.confidence,
            );
          }
        }

        // Update stats periodically (only if we got some outputs)
        const lastOutput = outputs.at(-1);
        if (lastOutput) {
          updateStats(lastOutput.wpm, lastOutput.snrDb, lastOutput.confidence);
        }
      }
    };

    return () => {
      // Restore original push
      audioClient.push = originalPush;
      decoderRef.current = null;
    };
  }, [state, targetFreq, updateStats]);

  // Update target frequency if changed (e.g., from UI)
  useEffect(() => {
    if (decoderRef.current) {
      decoderRef.current.setTargetFreq(targetFreq);
    }
  }, [targetFreq]);
}