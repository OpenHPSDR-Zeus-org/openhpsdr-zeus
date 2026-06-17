// SPDX-License-Identifier: AGPL-3.0-only
//
// Main-thread handle to the DeepCW ONNX worker. Owns the worker lifecycle,
// correlates decode requests with responses, and exposes a tiny async API
// (load / decode) plus a readiness callback. One worker is shared across the
// app (the model is ~15 MB — loading it once is the whole point).

export type DecoderReadyListener = (sampleRate: number) => void;
export type DecoderErrorListener = (message: string) => void;

type WorkerResponse =
  | { type: 'ready'; sampleRate: number }
  | { type: 'result'; id: number; text: string }
  | { type: 'error'; id: number | null; error: string };

let worker: Worker | null = null;
let nextId = 1;
const pending = new Map<number, { resolve: (text: string) => void; reject: (e: Error) => void }>();
const readyListeners = new Set<DecoderReadyListener>();
const errorListeners = new Set<DecoderErrorListener>();

function getWorker(): Worker {
  if (worker) return worker;
  worker = new Worker(new URL('./decode-worker.ts', import.meta.url), {
    type: 'module',
  });
  worker.onmessage = (event: MessageEvent<WorkerResponse>) => {
    const msg = event.data;
    if (msg.type === 'ready') {
      for (const fn of readyListeners) fn(msg.sampleRate);
      return;
    }
    if (msg.type === 'error') {
      if (msg.id != null) {
        const p = pending.get(msg.id);
        if (p) {
          pending.delete(msg.id);
          p.reject(new Error(msg.error));
        }
      }
      for (const fn of errorListeners) fn(msg.error);
      return;
    }
    // result
    const p = pending.get(msg.id);
    if (p) {
      pending.delete(msg.id);
      p.resolve(msg.text);
    }
  };
  worker.onerror = (e: ErrorEvent) => {
    const message = e.message || 'decode worker crashed';
    for (const { reject } of pending.values()) reject(new Error(message));
    pending.clear();
    for (const fn of errorListeners) fn(message);
  };
  return worker;
}

/** Kick off model + runtime load so the first decode isn't cold. */
export function loadDecoder(): void {
  getWorker().postMessage({ type: 'load' });
}

export function onDecoderReady(fn: DecoderReadyListener): () => void {
  readyListeners.add(fn);
  return () => readyListeners.delete(fn);
}

export function onDecoderError(fn: DecoderErrorListener): () => void {
  errorListeners.add(fn);
  return () => errorListeners.delete(fn);
}

/**
 * Decode one window of model-rate mono samples → text. The buffer is copied
 * before transfer so callers can keep reusing their rolling buffer.
 */
export function decode(samples: Float32Array): Promise<string> {
  const w = getWorker();
  const id = nextId++;
  const copy = samples.slice();
  return new Promise<string>((resolve, reject) => {
    pending.set(id, { resolve, reject });
    w.postMessage({ type: 'decode', id, samples: copy }, [copy.buffer]);
  });
}
