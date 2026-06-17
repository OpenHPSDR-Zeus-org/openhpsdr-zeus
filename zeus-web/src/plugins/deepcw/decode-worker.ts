// SPDX-License-Identifier: AGPL-3.0-only
//
// DeepCW decode worker. Runs the e04/deepcw-engine ONNX model
// (vendored as a git submodule under zeus-web/external/deepcw-engine) to
// turn a window of receive audio into decoded Morse text.
//
// The spectrogram + greedy-CTC pipeline below is a faithful port of the
// engine's reference decoder (examples/nodejs/decode_morse.mjs) so we stay
// bit-compatible with the model's expected input as the submodule updates.
// Because it links the AGPL-3.0 deepcw-engine model + algorithm, this file
// (and the Zeus build that bundles it) is AGPL-3.0; see the engine's LICENSE.
//
// onnxruntime-web runs single-threaded on purpose: multi-threaded WASM needs
// SharedArrayBuffer, which needs COOP/COEP cross-origin isolation, which
// would break Zeus's cross-origin iframe panels (HamClock, URL Embed). The
// CW tensor is tiny, so one thread is plenty.

/// <reference lib="webworker" />
import * as ort from 'onnxruntime-web/wasm';
// Bundled by Vite from node_modules — `?url` emits a hashed asset and yields
// its served URL. We hand ORT the loader URL + the raw wasm bytes so it never
// has to guess where its runtime lives (the hashed names defeat path guessing).
// These reach into node_modules by RELATIVE path on purpose: onnxruntime-web's
// package `exports` map doesn't expose `./dist/*`, so the bare specifier
// `onnxruntime-web/dist/...` is rejected by the bundler (upstream does the
// same relative-path trick).
import ortMjsUrl from '../../../node_modules/onnxruntime-web/dist/ort-wasm-simd-threaded.mjs?url';
import ortWasmUrl from '../../../node_modules/onnxruntime-web/dist/ort-wasm-simd-threaded.wasm?url';
// The model + its metadata travel with the submodule, so a `git submodule
// update --remote` ships any new model the author publishes — no separate
// download, fully offline.
import modelUrl from '../../../external/deepcw-engine/model.onnx?url';
import metadataUrl from '../../../external/deepcw-engine/model.onnx.json?url';

ort.env.wasm.numThreads = 1;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
(ort.env.wasm as any).wasmPaths = { mjs: ortMjsUrl };

type Metadata = {
  chars: string[];
  blank_index: number;
  sample_rate: number;
  fft_length: number;
  hop_length: number;
  spectrogram_min_freq_hz: number;
  spectrogram_max_freq_hz: number;
  spectrogram_frequency_bins: number;
  onnx_input_name: string;
  onnx_output_name: string;
};

type DecodeRequest = { type: 'decode'; id: number; samples: Float32Array };
type WorkerRequest = DecodeRequest | { type: 'load' };

type WorkerResponse =
  | { type: 'ready'; sampleRate: number }
  | { type: 'result'; id: number; text: string }
  | { type: 'error'; id: number | null; error: string };

const ctx: DedicatedWorkerGlobalScope =
  self as unknown as DedicatedWorkerGlobalScope;

let initPromise: Promise<{ session: ort.InferenceSession; meta: Metadata }> | null =
  null;

async function ensureSession(): Promise<{
  session: ort.InferenceSession;
  meta: Metadata;
}> {
  if (initPromise) return initPromise;
  initPromise = (async () => {
    // Provide the wasm bytes directly — avoids ORT trying to resolve the
    // hashed asset name from the loader's location.
    const wasmBytes = await (await fetch(ortWasmUrl)).arrayBuffer();
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    (ort.env.wasm as any).wasmBinary = wasmBytes;

    const meta = (await (await fetch(metadataUrl)).json()) as Metadata;
    const session = await ort.InferenceSession.create(modelUrl, {
      executionProviders: ['wasm'],
    });
    ctx.postMessage({ type: 'ready', sampleRate: meta.sample_rate } satisfies WorkerResponse);
    return { session, meta };
  })();
  return initPromise;
}

// --- Spectrogram + CTC, ported from deepcw-engine decode_morse.mjs ---------

function frequencyBinRange(
  sampleRate: number,
  fftLength: number,
  minHz: number,
  maxHz: number,
): { startBin: number; stopBin: number } {
  const binHz = sampleRate / fftLength;
  return {
    startBin: Math.ceil(minHz / binHz),
    stopBin: Math.floor(maxHz / binHz) + 1,
  };
}

function selectedDftMagnitudes(
  frame: Float32Array,
  startBin: number,
  stopBin: number,
): Float32Array {
  const length = frame.length;
  const output = new Float32Array(stopBin - startBin);
  for (let bin = startBin; bin < stopBin; bin += 1) {
    let real = 0;
    let imaginary = 0;
    for (let n = 0; n < length; n += 1) {
      const angle = (-2 * Math.PI * bin * n) / length;
      real += frame[n]! * Math.cos(angle);
      imaginary += frame[n]! * Math.sin(angle);
    }
    output[bin - startBin] = Math.hypot(real, imaginary);
  }
  return output;
}

function audioToSpectrogram(
  audio: Float32Array,
  meta: Metadata,
): ort.Tensor {
  const fftLength = meta.fft_length;
  const hopLength = meta.hop_length;
  const expectedBins = meta.spectrogram_frequency_bins;
  if (audio.length < fftLength) {
    throw new Error(`audio too short for fft_length=${fftLength}`);
  }

  const { startBin, stopBin } = frequencyBinRange(
    meta.sample_rate,
    fftLength,
    meta.spectrogram_min_freq_hz,
    meta.spectrogram_max_freq_hz,
  );
  if (stopBin - startBin !== expectedBins) {
    throw new Error(
      `metadata expects ${expectedBins} bins, computed ${stopBin - startBin}`,
    );
  }

  const pad = Math.floor(fftLength / 2);
  const padded = new Float32Array(audio.length + pad * 2);
  for (let i = 0; i < pad; i += 1) {
    padded[i] = audio[pad - i]!;
    padded[pad + audio.length + i] = audio[audio.length - 2 - i]!;
  }
  padded.set(audio, pad);

  const frames = 1 + Math.floor((padded.length - fftLength) / hopLength);
  const tensorData = new Float32Array(frames * expectedBins);
  const window = new Float32Array(fftLength);
  for (let i = 0; i < fftLength; i += 1) {
    window[i] = 0.5 - 0.5 * Math.cos((2 * Math.PI * i) / fftLength);
  }

  const frame = new Float32Array(fftLength);
  for (let frameIndex = 0; frameIndex < frames; frameIndex += 1) {
    const start = frameIndex * hopLength;
    for (let i = 0; i < fftLength; i += 1) {
      frame[i] = padded[start + i]! * window[i]!;
    }
    const magnitudes = selectedDftMagnitudes(frame, startBin, stopBin);
    for (let bin = 0; bin < expectedBins; bin += 1) {
      tensorData[frameIndex * expectedBins + bin] = Math.log1p(magnitudes[bin]!);
    }
  }

  return new ort.Tensor('float32', tensorData, [1, 1, frames, expectedBins]);
}

function greedyCtcDecode(
  logProbs: ort.Tensor,
  chars: string[],
  blankIndex: number,
): string {
  const dims = logProbs.dims as readonly number[];
  const batch = dims[0]!;
  const frames = dims[1]!;
  const classes = dims[2]!;
  if (batch !== 1) throw new Error(`expected batch size 1, got ${batch}`);

  const data = logProbs.data as Float32Array;
  let previous: number | null = null;
  let decoded = '';
  for (let frame = 0; frame < frames; frame += 1) {
    let bestIndex = 0;
    let bestValue = -Infinity;
    for (let klass = 0; klass < classes; klass += 1) {
      const value = data[frame * classes + klass]!;
      if (value > bestValue) {
        bestValue = value;
        bestIndex = klass;
      }
    }
    if (bestIndex === blankIndex) {
      previous = null;
    } else {
      if (bestIndex !== previous) decoded += chars[bestIndex] ?? '';
      previous = bestIndex;
    }
  }
  return decoded;
}

async function handleDecode(req: DecodeRequest): Promise<void> {
  const { session, meta } = await ensureSession();
  const spectrogram = audioToSpectrogram(req.samples, meta);
  const outputs = await session.run({ [meta.onnx_input_name]: spectrogram });
  const text = greedyCtcDecode(
    outputs[meta.onnx_output_name]!,
    meta.chars,
    meta.blank_index,
  );
  ctx.postMessage({ type: 'result', id: req.id, text } satisfies WorkerResponse);
}

ctx.onmessage = (event: MessageEvent<WorkerRequest>) => {
  const message = event.data;
  if (message.type === 'load') {
    void ensureSession().catch((err) => {
      ctx.postMessage({
        type: 'error',
        id: null,
        error: err instanceof Error ? err.message : 'model load failed',
      } satisfies WorkerResponse);
    });
    return;
  }
  if (message.type === 'decode') {
    void handleDecode(message).catch((err) => {
      ctx.postMessage({
        type: 'error',
        id: message.id,
        error: err instanceof Error ? err.message : 'decode failed',
      } satisfies WorkerResponse);
    });
  }
};

export {};
