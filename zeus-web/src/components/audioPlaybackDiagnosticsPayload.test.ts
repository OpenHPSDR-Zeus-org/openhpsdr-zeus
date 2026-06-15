// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { AudioPlaybackDiagnosticsSnapshot } from '../audio/audio-client';
import { buildAudioPlaybackDiagnosticsPayload } from './audioPlaybackDiagnosticsPayload';

function snapshot(overrides: Partial<AudioPlaybackDiagnosticsSnapshot> = {}): AudioPlaybackDiagnosticsSnapshot {
  return {
    playbackState: 'playing',
    contextState: 'running',
    bufferedSamples: 14_400,
    bufferedMs: 300,
    sampleRateHz: 48_000,
    contextSampleRateHz: 48_000,
    baseLatencyMs: 22,
    outputLatencyMs: 48,
    underrunCount: 0,
    droppedSamples: 0,
    latePushCount: 0,
    latenessVsScheduleCount: 0,
    pendingSources: 15,
    bufferTargetMs: 300,
    bufferMaxMs: 500,
    errorMessage: null,
    ...overrides,
  };
}

describe('buildAudioPlaybackDiagnosticsPayload', () => {
  it('maps browser RX playback telemetry into the diagnostics API payload', () => {
    const payload = buildAudioPlaybackDiagnosticsPayload(
      snapshot(),
      '2026-06-15T13:00:00.000Z',
    );

    expect(payload.sourceAtUtc).toBe('2026-06-15T13:00:00.000Z');
    expect(payload.sourceClientId).toMatch(/^frontend-/);
    expect(payload.playbackState).toBe('playing');
    expect(payload.contextState).toBe('running');
    expect(payload.bufferedSamples).toBe(14_400);
    expect(payload.bufferedMs).toBe(300);
    expect(payload.sampleRateHz).toBe(48_000);
    expect(payload.contextSampleRateHz).toBe(48_000);
    expect(payload.baseLatencyMs).toBe(22);
    expect(payload.outputLatencyMs).toBe(48);
    expect(payload.pendingSources).toBe(15);
  });

  it('normalizes non-finite numeric fields to null before publishing', () => {
    const payload = buildAudioPlaybackDiagnosticsPayload(
      snapshot({
        bufferedSamples: Number.NaN,
        bufferedMs: Number.POSITIVE_INFINITY,
        baseLatencyMs: Number.NEGATIVE_INFINITY,
        droppedSamples: Number.NaN,
      }),
      '2026-06-15T13:00:00.000Z',
    );

    expect(payload.bufferedSamples).toBeNull();
    expect(payload.bufferedMs).toBeNull();
    expect(payload.baseLatencyMs).toBeNull();
    expect(payload.droppedSamples).toBeNull();
  });
});
