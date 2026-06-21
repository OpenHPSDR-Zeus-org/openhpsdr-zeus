// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Shared browser-mic uplink session. The app-level hook starts this on mount
// when browser audio is active; mobile PTT can also call ensureMicUplinkRunning
// from the touch gesture so mobile browsers are allowed to grant/resume the
// microphone before Zeus keys MOX.

import { startMicUplink, type MicUplinkHandle } from './mic-uplink';
import { useMicUplinkDiagnosticsStore } from './mic-uplink-diagnostics-store';
import { createMicUplinkAutoGain } from './mic-uplink-gain';
import { sendMicPcm } from '../realtime/ws-client';
import { useAudioDeviceStore } from '../state/audio-device-store';
import { useAudioStore } from '../state/audio-store';
import { useTxStore } from '../state/tx-store';
import { warnOnce } from '../util/logger';

const MIC_DBFS_FLOOR = -100;
const MIC_VISUAL_INTERVAL_MS = 50;

let handle: MicUplinkHandle | null = null;
let starting: Promise<MicUplinkHandle> | null = null;
let consumers = 0;
let stopWhenIdle = false;
let windowPeak = 0;
let lastEmit = 0;
let forceTxSend = false;
const txAutoGain = createMicUplinkAutoGain();

function micErrorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

function onMicBlock(samples: Float32Array, peak: number): void {
  // Single-select: when a RADIO audio source is active (Radio Mic / Line In /
  // Balanced) the host browser mic is neither metered nor streamed — otherwise
  // the operator would see host-mic level on a radio source (the meter would
  // "lie") and we'd churn the WS with PCM the backend already drops. Park the
  // meter at the silence floor on the normal visual cadence and bail.
  if (useAudioStore.getState().settings.source !== 'Host') {
    const nowSuppressed = performance.now();
    if (nowSuppressed - lastEmit >= MIC_VISUAL_INTERVAL_MS) {
      useTxStore.getState().setMicDbfs(MIC_DBFS_FLOOR);
      lastEmit = nowSuppressed;
      windowPeak = 0;
    }
    return;
  }

  const tx = useTxStore.getState();
  const shouldSend = forceTxSend || tx.localMicArmed || tx.txMonitorEnabled;
  const outbound = shouldSend
    ? txAutoGain.process(samples, peak)
    : { samples, peak, gain: 1 };

  if (outbound.peak > windowPeak) windowPeak = outbound.peak;
  const now = performance.now();
  if (now - lastEmit >= MIC_VISUAL_INTERVAL_MS) {
    const dbfs = windowPeak > 0
      ? Math.max(MIC_DBFS_FLOOR, 20 * Math.log10(windowPeak))
      : MIC_DBFS_FLOOR;
    useTxStore.getState().setMicDbfs(dbfs);
    useMicUplinkDiagnosticsStore.getState().recordLocalBlock(dbfs);
    lastEmit = now;
    windowPeak = 0;
  }

  if (shouldSend) {
    const result = sendMicPcm(outbound.samples);
    useMicUplinkDiagnosticsStore.getState().recordSendResult(result);
  }
}

export function setMicUplinkTxForced(on: boolean): void {
  forceTxSend = on;
  useMicUplinkDiagnosticsStore.getState().setTxForced(on);
}

export function retainMicUplinkConsumer(): () => void {
  consumers += 1;
  stopWhenIdle = false;
  let released = false;
  return () => {
    if (released) return;
    released = true;
    consumers = Math.max(0, consumers - 1);
    if (consumers === 0) {
      stopWhenIdle = true;
      void stopMicUplinkRunning();
    }
  };
}

export async function ensureMicUplinkRunning(): Promise<boolean> {
  if (handle) {
    try {
      await handle.resume?.();
      useTxStore.getState().setMicError(null);
      return true;
    } catch (err) {
      const msg = micErrorMessage(err);
      useTxStore.getState().setMicError(msg);
      throw err;
    }
  }

  if (!starting) {
    const inputDeviceId = useAudioDeviceStore.getState().browserInputDeviceId || undefined;
    starting = startMicUplink(onMicBlock, inputDeviceId)
      .then((h) => {
        handle = h;
        useTxStore.getState().setMicError(null);
        if (stopWhenIdle && consumers === 0) {
          void stopMicUplinkRunning();
        }
        return h;
      })
      .catch((err: unknown) => {
        const msg = micErrorMessage(err);
        warnOnce('mic-uplink-failed', `mic capture unavailable: ${msg}`);
        useTxStore.getState().setMicError(msg);
        throw err;
      })
      .finally(() => {
        starting = null;
      });
  }

  await starting;
  return handle !== null;
}

export async function restartMicUplinkRunning(): Promise<void> {
  const shouldRestart = consumers > 0;
  if (starting) {
    try { await starting; } catch { /* ensureMicUplinkRunning owns visible errors */ }
  }
  await stopMicUplinkRunning();
  if (shouldRestart) await ensureMicUplinkRunning();
}

export async function stopMicUplinkRunning(): Promise<void> {
  const h = handle;
  handle = null;
  stopWhenIdle = false;
  windowPeak = 0;
  lastEmit = 0;
  txAutoGain.reset();
  setMicUplinkTxForced(false);
  if (h) await h.stop();
}

export function isMicUplinkRunning(): boolean {
  return handle !== null;
}

/** Geometry of the live mic spectrum tap, or null when no tap is available. */
export function getMicSpectrumInfo(): { binCount: number; fftSize: number; sampleRate: number } | null {
  if (!handle?.getSpectrum || !handle.spectrumBinCount) return null;
  return {
    binCount: handle.spectrumBinCount,
    fftSize: handle.spectrumFftSize ?? handle.spectrumBinCount * 2,
    sampleRate: handle.spectrumSampleRate ?? 48000,
  };
}

/**
 * Fill `out` (length must equal the current bin count) with the latest mic FFT
 * magnitudes in dBFS. Returns false when no live analyser is running — e.g.
 * desktop/native audio mode, or before the operator has granted the mic.
 */
export function getMicSpectrum(out: Float32Array): boolean {
  return handle?.getSpectrum?.(out) ?? false;
}
