// SPDX-License-Identifier: GPL-2.0-or-later

import type { FrontendAudioPlaybackDiagnosticsPayload } from '../api/client';
import type { AudioPlaybackDiagnosticsSnapshot } from '../audio/audio-client';
import { frontendClientId } from './dspSceneDiagnosticsPayload';

function n(value: number | null | undefined): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

export function buildAudioPlaybackDiagnosticsPayload(
  snapshot: AudioPlaybackDiagnosticsSnapshot,
  sourceAtUtc = new Date().toISOString(),
): FrontendAudioPlaybackDiagnosticsPayload {
  return {
    sourceAtUtc,
    sourceClientId: frontendClientId(),
    playbackState: snapshot.playbackState,
    contextState: snapshot.contextState,
    bufferedSamples: n(snapshot.bufferedSamples),
    bufferedMs: n(snapshot.bufferedMs),
    sampleRateHz: n(snapshot.sampleRateHz),
    contextSampleRateHz: n(snapshot.contextSampleRateHz),
    baseLatencyMs: n(snapshot.baseLatencyMs),
    outputLatencyMs: n(snapshot.outputLatencyMs),
    underrunCount: n(snapshot.underrunCount),
    droppedSamples: n(snapshot.droppedSamples),
    latePushCount: n(snapshot.latePushCount),
    latenessVsScheduleCount: n(snapshot.latenessVsScheduleCount),
    pendingSources: n(snapshot.pendingSources),
    bufferTargetMs: n(snapshot.bufferTargetMs),
    bufferMaxMs: n(snapshot.bufferMaxMs),
    errorMessage: snapshot.errorMessage,
  };
}
