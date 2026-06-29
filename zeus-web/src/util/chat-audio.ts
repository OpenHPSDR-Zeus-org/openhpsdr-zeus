// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

// Browser voice-snippet capture for ZeusChat inline audio attachments.
//
// A voice message rides inside the chat message exactly like an inline photo
// (see util/chat-image.ts): the encoded bytes are carried as a base64 data URL
// the relay persists in a single Durable-Object value (128 KiB cap). To stay
// comfortably under MAX_ATTACHMENT_DATAURL_LEN for a full-length clip we record
// Opus at a low voice-grade bitrate and hard-cap the duration at 60 s.
//
// MediaRecorder + getUserMedia require a secure context (https or localhost)
// and a one-time mic grant. The hook surfaces a friendly message when either is
// missing rather than throwing, so the composer can show it inline.

import { useCallback, useEffect, useRef, useState } from 'react';
import { MAX_ATTACHMENT_DATAURL_LEN, type ChatAttachment } from '../api/chat';

export class ChatAudioError extends Error {}

/** Hard cap on a single voice snippet (ms). The recorder auto-stops here. */
export const MAX_VOICE_MS = 60_000;

/**
 * Encoder target bitrate (bits/s). 10 kbps Opus keeps an entire 60 s clip well
 * under the data-URL cap (~75 KB decoded → ~100 k base64 chars) while staying
 * intelligible for speech. Treated as a hint by the browser; the final size is
 * re-checked against the cap before the attachment is staged.
 */
const VOICE_BITS_PER_SEC = 10_000;

/** Candidate container/codec types, smallest-first (Opus), then Safari's mp4. */
const MIME_CANDIDATES = [
  'audio/webm;codecs=opus',
  'audio/webm',
  'audio/ogg;codecs=opus',
  'audio/ogg',
  'audio/mp4',
];

/** Whether this browser can capture a voice snippet at all. */
export function isVoiceRecordingSupported(): boolean {
  return (
    typeof navigator !== 'undefined' &&
    !!navigator.mediaDevices &&
    typeof navigator.mediaDevices.getUserMedia === 'function' &&
    typeof MediaRecorder !== 'undefined'
  );
}

/** Best supported recorder MIME type, or undefined to let the browser choose. */
function pickMimeType(): string | undefined {
  if (typeof MediaRecorder === 'undefined' || typeof MediaRecorder.isTypeSupported !== 'function') {
    return undefined;
  }
  for (const t of MIME_CANDIDATES) {
    if (MediaRecorder.isTypeSupported(t)) return t;
  }
  return undefined;
}

/** Friendly download/display name for a recorded clip's container type. */
function nameForMime(mime: string): string {
  if (mime.includes('mp4')) return 'voice-message.m4a';
  if (mime.includes('ogg')) return 'voice-message.ogg';
  return 'voice-message.webm';
}

function blobToDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : '');
    reader.onerror = () => reject(new ChatAudioError("Couldn't read that recording."));
    reader.readAsDataURL(blob);
  });
}

export interface VoiceRecorderController {
  /** True when the platform can record at all (gate the mic button on this). */
  supported: boolean;
  /** Acquiring the mic / spinning up the recorder. */
  preparing: boolean;
  /** Actively capturing. */
  recording: boolean;
  /** Elapsed capture time (ms), updated ~10×/s while recording. */
  elapsedMs: number;
  /** Last user-facing error, or null. */
  error: string | null;
  /** Begin capture (prompts for mic permission the first time). */
  start: () => void;
  /** Stop and finalize — invokes onComplete with the staged attachment. */
  stop: () => void;
  /** Stop and discard the in-progress recording. */
  cancel: () => void;
  /** Dismiss the current error. */
  clearError: () => void;
}

/**
 * Microphone capture lifecycle for a single voice snippet. `onComplete` fires
 * once with the encoded attachment when {@link VoiceRecorderController.stop} is
 * called (not on cancel). All native resources (recorder + mic tracks) are
 * released on stop, cancel, and unmount so the OS mic indicator clears.
 */
export function useVoiceRecorder(
  onComplete: (att: ChatAttachment) => void,
): VoiceRecorderController {
  const [preparing, setPreparing] = useState(false);
  const [recording, setRecording] = useState(false);
  const [elapsedMs, setElapsedMs] = useState(0);
  const [error, setError] = useState<string | null>(null);

  const recorderRef = useRef<MediaRecorder | null>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const chunksRef = useRef<BlobPart[]>([]);
  const cancelledRef = useRef(false);
  const timerRef = useRef<number | null>(null);
  const autoStopRef = useRef<number | null>(null);
  const startedAtRef = useRef(0);
  const onCompleteRef = useRef(onComplete);
  onCompleteRef.current = onComplete;

  const clearTimers = useCallback(() => {
    if (timerRef.current !== null) {
      window.clearInterval(timerRef.current);
      timerRef.current = null;
    }
    if (autoStopRef.current !== null) {
      window.clearTimeout(autoStopRef.current);
      autoStopRef.current = null;
    }
  }, []);

  const releaseStream = useCallback(() => {
    streamRef.current?.getTracks().forEach((t) => t.stop());
    streamRef.current = null;
  }, []);

  const finalize = useCallback(async (mime: string) => {
    const blob = new Blob(chunksRef.current, { type: mime });
    chunksRef.current = [];
    try {
      const dataUrl = await blobToDataUrl(blob);
      if (!dataUrl || dataUrl.length > MAX_ATTACHMENT_DATAURL_LEN) {
        throw new ChatAudioError('That recording is too long to send — keep it under a minute.');
      }
      onCompleteRef.current({
        kind: 'audio',
        mime,
        dataUrl,
        name: nameForMime(mime),
        width: null,
        height: null,
        size: blob.size,
      });
    } catch (e) {
      setError(e instanceof ChatAudioError ? e.message : "Couldn't process that recording.");
    }
  }, []);

  const start = useCallback(async () => {
    if (recorderRef.current || preparing) return;
    if (!isVoiceRecordingSupported()) {
      setError('Voice recording is not available in this browser.');
      return;
    }
    setError(null);
    setPreparing(true);
    cancelledRef.current = false;
    chunksRef.current = [];
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      streamRef.current = stream;
      const mimeType = pickMimeType();
      const recorder = new MediaRecorder(
        stream,
        mimeType ? { mimeType, audioBitsPerSecond: VOICE_BITS_PER_SEC } : { audioBitsPerSecond: VOICE_BITS_PER_SEC },
      );
      recorderRef.current = recorder;
      recorder.ondataavailable = (e) => {
        if (e.data && e.data.size > 0) chunksRef.current.push(e.data);
      };
      recorder.onstop = () => {
        clearTimers();
        releaseStream();
        recorderRef.current = null;
        setRecording(false);
        setPreparing(false);
        setElapsedMs(0);
        if (!cancelledRef.current) void finalize(recorder.mimeType || mimeType || 'audio/webm');
        else chunksRef.current = [];
      };
      startedAtRef.current = Date.now();
      recorder.start();
      setPreparing(false);
      setRecording(true);
      setElapsedMs(0);
      timerRef.current = window.setInterval(() => {
        setElapsedMs(Date.now() - startedAtRef.current);
      }, 100);
      // Auto-stop at the hard cap so a clip can never overflow the wire size.
      autoStopRef.current = window.setTimeout(() => {
        recorderRef.current?.stop();
      }, MAX_VOICE_MS);
    } catch (err) {
      releaseStream();
      recorderRef.current = null;
      setPreparing(false);
      setRecording(false);
      const name = err instanceof DOMException ? err.name : '';
      if (name === 'NotAllowedError' || name === 'SecurityError') {
        setError('Microphone access was blocked. Allow mic access to send a voice message.');
      } else if (name === 'NotFoundError') {
        setError('No microphone was found.');
      } else {
        setError('Voice messages need a secure (https or localhost) connection and mic permission.');
      }
    }
  }, [preparing, clearTimers, releaseStream, finalize]);

  const stop = useCallback(() => {
    cancelledRef.current = false;
    recorderRef.current?.stop();
  }, []);

  const cancel = useCallback(() => {
    cancelledRef.current = true;
    recorderRef.current?.stop();
  }, []);

  const clearError = useCallback(() => setError(null), []);

  // Release the mic + timers if the component unmounts mid-recording.
  useEffect(() => {
    return () => {
      cancelledRef.current = true;
      clearTimers();
      try {
        recorderRef.current?.stop();
      } catch {
        /* already stopped */
      }
      recorderRef.current = null;
      streamRef.current?.getTracks().forEach((t) => t.stop());
      streamRef.current = null;
    };
  }, [clearTimers]);

  return {
    supported: isVoiceRecordingSupported(),
    preparing,
    recording,
    elapsedMs,
    error,
    start: () => void start(),
    stop,
    cancel,
    clearError,
  };
}

/** Format an elapsed/total ms value as M:SS for the recording indicator. */
export function fmtDuration(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}
